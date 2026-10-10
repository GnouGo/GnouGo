using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed record TaskCompilation(PlanningGraph? Graph, IReadOnlyList<PlanningDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, string> Sources)
{
    public PlanningDiagnostic Locate(PlanningDiagnostic diagnostic)
    {
        var location = diagnostic.Location;
        if (Graph is not null && location.StartsWith("/workflows/", StringComparison.Ordinal))
        {
            var parts = location.Split('/');
            if (parts.Length > 2 && int.TryParse(parts[2], out var wi) && wi >= 0 && wi < Graph.Workflows.Count)
            {
                var workflow = Graph.Workflows[wi];
                var workflowKey = workflow.Key == PlanningConfirmationGuards.Body ? Graph.Entrypoint : workflow.Key;
                var workflowPath = "/workflows/" + wi;
                foreach (var (node, path) in PlanningGraphValidation.Located(workflow.Steps, workflowPath + "/steps")
                    .Concat(PlanningGraphValidation.Located(workflow.Finally, workflowPath + "/finally")).OrderByDescending(p => p.Path.Length))
                {
                    if (location != path && !location.StartsWith(path + "/", StringComparison.Ordinal)) continue;
                    var address = node.Key + location[path.Length..];
                    var source = Sources.Where(p => address == p.Key || address.StartsWith(p.Key + "/", StringComparison.Ordinal))
                        .OrderByDescending(p => p.Key.Length).FirstOrDefault();
                    if (source.Key is not null) { location = source.Value; break; }
                }
                if (location == diagnostic.Location)
                {
                    var address = workflowKey + location[workflowPath.Length..];
                    var source = Sources.Where(p => address == p.Key || address.StartsWith(p.Key + "/", StringComparison.Ordinal))
                        .OrderByDescending(p => p.Key.Length).FirstOrDefault();
                    if (source.Key is not null) location = source.Value;
                }
            }
        }
        foreach (var (key, task) in Sources)
            if (location.Contains("/stages/" + key, StringComparison.Ordinal)) { location = task; break; }
        var message = diagnostic.Message;
        if (diagnostic.Code == "SCHEMA_INVALID" && diagnostic.Rule?.StartsWith("producer:", StringComparison.Ordinal) == true)
            message += " Producer: " + Sources.GetValueOrDefault(diagnostic.Rule[9..], diagnostic.Rule[9..]) + ".";
        return diagnostic with { Location = location, Message = message };
    }
}

/// <summary>One deterministic lowering pass. Symbols and source locations are transient compiler bookkeeping.</summary>
public sealed partial class TaskPlanCompiler
{
    private sealed record Bound(PlanningValue Value, JsonObject Schema,
        Bound? SelectionSource = null, List<string>? SelectionPath = null, string? TypeLocation = null);
    private sealed class Scope(PlanningWorkflow workflow, Scope? parent)
    {
        public PlanningWorkflow Workflow { get; } = workflow;
        public Scope? Parent { get; } = parent;
        public Dictionary<string, Bound> Inputs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, Bound>> Tasks { get; } = new(StringComparer.Ordinal);
        public List<PlanningMember> Captures { get; } = [];
        public HashSet<(string Kind, string Source, string Port)> Blocked { get; } = [];
        public IReadOnlySet<string>? NonNullReferences { get; init; }
        public bool FailurePath { get; init; }
        public Dictionary<string, Bound> FailureCaptures { get; } = new(StringComparer.Ordinal);
        public Bound? Item { get; set; }
        public Bound? Index { get; set; }
        public List<PlanningNode>? Target { get; set; }
        public bool Cleanup { get; set; }
    }
    private sealed class InvalidTask(string code, string location, string message) : Exception(message)
    { public PlanningDiagnostic Diagnostic { get; } = new(code, location, message); }
    private TaskPlan _plan = null!;
    private TaskPlanSymbols _symbols = null!;
    private PlanningCatalog _catalog = null!;
    private PlanningGraph _graph = new();
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compilingGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlanningWorkflow> _groups = new(StringComparer.Ordinal);
    internal const string CompactProfile = "compact-bindings-v7";
    internal static bool UsesCompactBindings(PlanningRequest request) => request.Options["compilation_profile"]?.ToString() is "compact-bindings-v1" or "compact-bindings-v2" or "compact-bindings-v3" or "compact-bindings-v4" or "compact-bindings-v5" or "compact-bindings-v6" or CompactProfile;
    internal static bool UsesNormalExports(PlanningRequest request) => request.Options["compilation_profile"]?.ToString() is "compact-bindings-v2" or "compact-bindings-v3" or "compact-bindings-v4" or "compact-bindings-v5" or "compact-bindings-v6" or CompactProfile;
    internal static bool UsesFusedBindings(PlanningRequest request) => request.Options["compilation_profile"]?.ToString() is "compact-bindings-v4" or "compact-bindings-v5" or "compact-bindings-v6" or CompactProfile;
    internal static bool UsesConsumerBindings(PlanningRequest request) => request.Options["compilation_profile"]?.ToString() is "compact-bindings-v5" or "compact-bindings-v6" or CompactProfile;
    internal static bool UsesDirectProjections(PlanningRequest request) => request.Options["compilation_profile"]?.ToString() is "compact-bindings-v6" or CompactProfile;
    internal static bool UsesNativeMappings(PlanningRequest request) => request.Options["compilation_profile"]?.ToString() == CompactProfile;
    internal const string AdaptiveMappingProfile = "adaptive-each-v1";
    private bool _adaptiveMappings;
    private bool _compactBindings;
    private bool _normalExports;
    private bool _indexedProjections;
    private bool _fusedBindings;
    private string _location = "/tasks";

    public TaskCompilation Compile(TaskPlan plan, PlanningCatalog catalog, bool compactBindings = false)
        => Compile(plan, catalog, compactBindings, false);

    internal TaskCompilation Compile(TaskPlan plan, PlanningCatalog catalog, PlanningRequest request)
        => Compile(plan, catalog, UsesCompactBindings(request), UsesNormalExports(request), request.Options["mapping_profile"]?.ToString() == AdaptiveMappingProfile,
            request.Options["compilation_profile"]?.ToString() is "compact-bindings-v3" or "compact-bindings-v4" or "compact-bindings-v5" or "compact-bindings-v6" or CompactProfile, UsesFusedBindings(request));

    internal TaskCompilation Compile(TaskPlan plan, PlanningCatalog catalog, bool compactBindings, bool normalExports, bool adaptiveMappings = false, bool indexedProjections = false, bool fusedBindings = false)
    {
        _fusedBindings = fusedBindings;
        _indexedProjections = indexedProjections;
        _adaptiveMappings = adaptiveMappings;
        _normalExports = normalExports;
        _compactBindings = compactBindings;
        _plan = plan; _symbols = new(plan); _catalog = catalog; _location = "/"; _graph = new(); _sources.Clear(); _groups.Clear(); _compilingGroups.Clear();

        try
        {
            var findings = Preflight();
            if (findings.Count > 0) return new(null, findings, new Dictionary<string, string>());
            var main = new PlanningWorkflow(); _graph.Workflows.Add(main); _sources["main"] = "/root";
            var scope = new Scope(main, null); AddInputs(scope, plan.Inputs, "/inputs");
            CompileScope(plan.Root, scope, "main");
            if (_normalExports)
                foreach (var node in _graph.Workflows.SelectMany(w => PlanningGraphCompiler.Enumerate(w.Steps.Concat(w.Finally))))
                    if (string.IsNullOrWhiteSpace(node.Purpose)) node.Purpose = TechnicalDescription(node);
            return new(_graph, [], new Dictionary<string, string>(_sources));
        }
        catch (InvalidTask error) { return new(null, [error.Diagnostic with { Code = "TASK_COMPILER_VALIDATION", Message = error.Diagnostic.Code + ": " + error.Message }], new Dictionary<string, string>(_sources)); }
    }

    public static JsonObject TypeSchema(TaskType type)
    {
        if ((type.MinItems is not null || type.MaxItems is not null) && type.Kind != "array" ||
            type.MinItems < 0 || type.MaxItems < 0 || type.MinItems > type.MaxItems)
            throw new ArgumentException("Array cardinality requires nonnegative bounds with minItems <= maxItems.");
        if (!ValidEnum(type)) throw new ArgumentException("A string enum requires 1–256 distinct non-null strings; declare nullability separately.");
        if (type.Kind is not ("string" or "number" or "integer" or "boolean" or "object" or "array" or "any"))
            throw new ArgumentException("Unknown business type.");
        if (type.Kind == "any") return new();
        var schema = new JsonObject { ["type"] = type.Nullable ? new JsonArray(type.Kind, "null") : JsonValue.Create(type.Kind) };
        if (type.Enum is { } values)
        {
            schema["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            if (type.Nullable) schema["enum"]!.AsArray().Add((JsonNode?)null);
        }
        if (type.Kind == "array")
        {
            schema["items"] = TypeSchema(type.Items ?? throw new ArgumentException("Array items require a business type."));
            if (type.MinItems is { } min) schema["minItems"] = min;
            if (type.MaxItems is { } max) schema["maxItems"] = max;
        }
        if (type.Kind == "object")
        {
            if (type.Fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != type.Fields.Count) throw new ArgumentException("Duplicate business fields.");
            schema["properties"] = new JsonObject(type.Fields.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, TypeSchema(f.Type))));
            schema["required"] = new JsonArray(type.Fields.Where(f => f.Required).Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray());
            schema["additionalProperties"] = false;
        }
        return schema;
    }

    private void ValidateChoice(PlanningChoice choice)
    {
        _location = "/choices/" + choice.Id;
        Unique(choice.Alternatives.Select(a => a.Id));
        if (string.IsNullOrWhiteSpace(choice.Question) || choice.Alternatives.Count < 2 ||
            !choice.Alternatives.Any(a => a.Id == choice.Recommended) || choice.Selected is not null && !choice.Alternatives.Any(a => a.Id == choice.Selected))
            Fail("CHOICE_INVALID", "Choices need a question, distinct alternatives and a valid recommendation and selection.");
        var type = Schema(choice.Type);
        foreach (var alternative in choice.Alternatives)
        {
            if (!Literal(alternative.Value)) Fail("CHOICE_INVALID", "Choice alternatives must be literal business values.");
            var value = Value(alternative.Value, new(new(), null));
            if (PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(value.Value), type).Count > 0)
                Fail("CHOICE_INVALID", "An alternative violates the declared business type.");
        }
    }

    private PlanningPort InputPort(TaskInput input)
    {
        var schema = Schema(input.Type);
        PlanningValue? fallback = null;
        if (input.Default is { } supplied)
        {
            if (!Literal(supplied)) Fail("TASK_DEFAULT_INVALID", "Business input defaults must be literal.");
            fallback = Value(supplied, new(new(), null)).Value;
            if (PlanningContractValidation.ValidateInstance(PlanningGraphValidation.Literal(fallback), schema).Count > 0)
                Fail("TASK_DEFAULT_INVALID", "A default violates its business type.");
        }
        if (!input.Required && fallback is null) Fail("TASK_DEFAULT_REQUIRED", "Optional business inputs require a literal default.");
        return new() { Name = input.Name, Schema = Contract(schema), Required = input.Required, Default = fallback };
    }

    private void AddInputs(Scope scope, List<TaskInput> inputs, string path)
    {
        foreach (var input in inputs)
        {
            _location = path + "/" + input.Name;
            var port = InputPort(input);
            _sources[scope.Workflow.Key + "/inputs/" + scope.Workflow.Inputs.Count] = _location;
            scope.Workflow.Inputs.Add(port);
            scope.Inputs.Add(input.Name, Input(input.Name, port.Schema.Contract!));
        }
    }

    private void CompileScope(TaskScope source, Scope scope, string key)
    {
        Unique(source.Tasks.Concat(source.Always).Select(t => t.Id));
        foreach (var task in Ordered(source.Tasks)) CompileTask(task, scope, scope.Workflow.Steps, key, false);
        foreach (var task in Ordered(source.Always)) CompileTask(task, scope, scope.Workflow.Finally, key, true);
        if (_normalExports)
        {
            Unique(source.Outputs.Select(o => o.Name));
            CompileExports(source, scope, key);
        }
        if (scope.Workflow.Steps.Count == 0)
        {
            var empty = Key(key, "empty"); _sources[empty] = "/tasks/" + key;
            scope.Workflow.Steps.Add(new() { Key = empty, Type = "set", Input = Object([]) });
        }
        if (_normalExports) return;
        Unique(source.Outputs.Select(o => o.Name));
        foreach (var output in source.Outputs)
        {
            scope.Target = scope.Workflow.Finally; scope.Cleanup = true;
            _location = (_sources.GetValueOrDefault(key) ?? "/root") + "/outputs/" + output.Name;
            _sources[key + "/outputs/" + scope.Workflow.Outputs.Count] = _location;
            var bound = Value(output.Value, scope);
            if (bound.Value.Kind is "object" or "array")
            {
                // Outputs are evaluated after cleanup. Materialize structured values there,
                // using the existing typed primitive rather than opaque object expressions.
                var export = Key(key, "export:" + output.Name); _sources[export] = _location;
                var node = new PlanningNode { Key = export, Type = "set", Input = Object([new("value", bound.Value)]),
                    OutputSchema = Contract(ObjectSchema([("value", bound.Schema)])) };
                GuardCleanup(node);
                scope.Workflow.Finally.Add(node);
                bound = Output(export, "set", ["value"], bound.Schema);
            }
            scope.Workflow.Outputs.Add(new() { Name = output.Name, Value = bound.Value, Schema = Contract(bound.Schema) });
        }
    }

    private IEnumerable<PlanTask> Ordered(List<PlanTask> tasks)
    {
        var remaining = tasks.ToList(); var done = new HashSet<string>(StringComparer.Ordinal);
        var ids = tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(t => t.DependsOn.All(d => done.Contains(d) || !ids.Contains(d)) &&
                Values(t).Where(v => v.Kind is "output" or "present" && v.Source is not null && ids.Contains(v.Source)).All(v => done.Contains(v.Source!)));
            if (next is null) Fail("TASK_DEPENDENCY_CYCLE", "Task dependencies and business references must be acyclic.");
            remaining.Remove(next!); done.Add(next!.Id); yield return next;
        }
    }

    private void CompileTask(PlanTask task, Scope scope, List<PlanningNode> target, string parent, bool cleanup)
    {
        scope.Target = target; scope.Cleanup = cleanup;
        _location = "/tasks/" + task.Id;
        if (string.IsNullOrWhiteSpace(task.Objective)) Fail("TASK_OBJECTIVE_REQUIRED", "Each task requires an objective.");
        if (task.DependsOn.Any(d => !scope.Tasks.ContainsKey(d))) Fail("TASK_DEPENDENCY_UNKNOWN", "A dependency must name a preceding task in this scope.");
        var key = Key(parent, task.Id); _sources[key] = _location;
        var start = target.Count;
        if (task.Requires is not null)
        {
            _location += "/requires";
            var required = Value(task.Requires, scope); RequireBoolean(required);
            var guard = Key(key, "requires"); _sources[guard] = _location;
            target.Add(new() { Key = guard, Type = "set", Purpose = "Required condition: " + task.Objective,
                Input = Projection([new("value", required.Value), new("paths", Array([Strings([])]))]),
                OutputSchema = Contract(ObjectSchema([("value", new JsonObject { ["type"] = "boolean", ["enum"] = new JsonArray(true) })])) });
            _location = "/tasks/" + task.Id;
        }
        Dictionary<string, Bound> outputs;
        switch (task.Kind)
        {
            case "operation": outputs = Operation(task, scope, target, key); break;
            case "transform": outputs = Transform(task, scope, target, key); break;
            case "value":
                Unique(task.Outputs.Select(o => o.Name));
                var selectionStart = target.Count;
                var values = task.Outputs.Select(o => (o.Name, Bound: Value(o.Value, scope))).ToArray();
                var fuse = _compactBindings && !cleanup && task.Requires is null && task.Outputs.All(o => CopyValue(o.Value)) &&
                    target.Skip(selectionStart).All(n => n.Type == "set" && n.Input.Kind == "projection" && n.If is null);
                if (fuse) foreach (var node in target.Skip(selectionStart)) node.InternalRole = "inline:" + key;
                target.Add(new() { Key = key, Purpose = task.Objective, InternalRole = fuse ? "typed_assembly" : null, Type = "set", Input = Object(values.Select(v => new PlanningMember(v.Name, v.Bound.Value))), OutputSchema = Contract(ObjectSchema(values.Select(v => (v.Name, v.Bound.Schema)))) });
                outputs = Result(key, "set", ObjectSchema(values.Select(v => (v.Name, v.Bound.Schema)))); break;
            case "call":
                var definition = _plan.Groups.SingleOrDefault(g => g.Id == task.Group);
                if (definition is null) Fail("TASK_GROUP_UNKNOWN", "Choose a declared reusable task group.");
                var group = Group(definition!);
                var args = BindArguments(task.Inputs, scope, group.Inputs);
                target.Add(Call(key, group.Key, args)); outputs = WorkflowResult(key, group); break;
            case "sequence":
                var sequence = Child(task.Body ?? MissingScope(), scope, key, "body");
                target.Add(sequence.Call); outputs = WorkflowResult(sequence.Call.Key, sequence.Workflow); break;
            case "conditional":
                var condition = Value(task.Condition ?? MissingValue(), scope); RequireBoolean(condition);
                var yes = Child(task.Body ?? MissingScope(), scope, key, "yes", NonNullReferences(task.Condition, true));
                var no = Child(task.Otherwise ?? MissingScope(), scope, key, "no", NonNullReferences(task.Condition, false));
                if (!yes.Workflow.Outputs.Select(o => o.Name).Order().SequenceEqual(no.Workflow.Outputs.Select(o => o.Name).Order()))
                    Fail("TASK_BRANCH_OUTPUTS", "Both alternatives must declare the same named business outputs.");
                target.Add(new() { Key = key, Purpose = task.Objective, Type = "switch", Expr = condition.Value,
                    Cases = [new("true", null, [yes.Call])], Default = [no.Call] });
                outputs = new(StringComparer.Ordinal);
                foreach (var output in yes.Workflow.Outputs)
                {
                    var schema = PlanningGraphCompiler.ToJsonSchema(output.Schema, _catalog);
                    var alternate = PlanningGraphCompiler.ToJsonSchema(no.Workflow.Outputs.Single(o => o.Name == output.Name).Schema, _catalog);
                    if (!JsonNode.DeepEquals(schema, alternate)) schema = new() { ["anyOf"] = new JsonArray(schema, alternate) };
                    var projection = Key(key, "merge:" + output.Name); _sources[projection] = _location;
                    target.Add(new() { Key = projection, Type = "set", Input = Projection([
                        new("value", Reference(key)), new("paths", Array([Strings([yes.Call.Key, "outputs", output.Name]), Strings([no.Call.Key, "outputs", output.Name])]))]),
                        OutputSchema = Contract(ObjectSchema([("value", schema)])) });
                    outputs.Add(output.Name, Output(projection, "set", ["value"], schema));
                }
                outputs = Aggregate(outputs, key, target); break;
            case "parallel":
                if (task.Branches.Count < 2 || task.MaxConcurrency is < 1 or > 100) Fail("TASK_PARALLEL_INVALID", "Parallel scopes need at least two branches and bounded concurrency.");
                var children = task.Branches.Select((b, i) => Child(b, scope, key, "branch:" + i)).ToArray();
                target.Add(new() { Key = key, Purpose = task.Objective, Type = "parallel", Input = Object([new("max_concurrency", Number(task.MaxConcurrency))]), Branches = children.Select(c => new PlanningBranch([c.Call])).ToList() });
                outputs = new(StringComparer.Ordinal);
                for (var i = 0; i < children.Length; i++)
                    foreach (var output in children[i].Workflow.Outputs)
                    {
                        if (outputs.ContainsKey(output.Name)) Fail("TASK_BRANCH_OUTPUTS", "Parallel branches must export distinct business output names.");
                        var schema = PlanningGraphCompiler.ToJsonSchema(output.Schema, _catalog);
                        outputs.Add(output.Name, Output(key, "parallel", ["branches", i.ToString(CultureInfo.InvariantCulture), children[i].Call.Key, "outputs", output.Name], schema));
                    }
                outputs = Aggregate(outputs, key, target); break;
            case "foreach":
                if (task.MaxItems is < 1 or > 10000 || task.MaxConcurrency is < 1 or > 100) Fail("TASK_ITERATION_BOUND", "Iteration requires finite positive item and concurrency ceilings.");
                var items = Value(task.Items ?? MissingValue(), scope);
                var itemSchema = PlanningContractShapes.IterationItems(items.Schema);
                if (itemSchema is null) Fail("TASK_ITEMS_INVALID", "Iteration needs an authoritative array contract.");
                var bounded = items.Schema.DeepClone().AsObject();
                bounded["type"] = "array"; bounded["items"] = itemSchema!.DeepClone(); bounded["maxItems"] = task.MaxItems;
                var checkedKey = Key(key, "bound"); _sources[checkedKey] = _location;
                target.Add(new() { Key = checkedKey, Type = "set", Input = Projection([new("value", items.Value), new("paths", Array([Strings([])]))]), OutputSchema = Contract(ObjectSchema([("value", bounded)])) });
                var copyLoop = _compactBindings && !cleanup && PureProjection(task);
                var loopScope = scope; var loopTarget = target; var loopItems = Reference(checkedKey, "value");
                if (_compactBindings && !copyLoop)
                {
                    var workflow = new PlanningWorkflow { Key = Key(key, "collection") };
                    _graph.Workflows.Add(workflow); _sources[workflow.Key] = _location;
                    loopScope = new(workflow, scope) { Target = workflow.Steps, FailurePath = scope.FailurePath || cleanup };
                    loopTarget = workflow.Steps;
                    workflow.Inputs.Add(new() { Name = "__items", Schema = Contract(bounded) });
                    loopScope.Inputs.Add("__items", Input("__items", bounded));
                    loopScope.Captures.Add(new("__items", loopItems)); loopItems = Input("__items", bounded).Value;
                }
                var iteration = new Scope(loopScope.Workflow, loopScope) { Item = new(new() { Kind = "loop_item", Source = key }, itemSchema, TypeLocation: items.TypeLocation is { } itemType ? itemType + "/items" : null), Index = new(new() { Kind = "loop_index", Source = key }, new() { ["type"] = "integer" }) };
                var body = Child(task.Body ?? MissingScope(), iteration, key, "iteration");
                loopTarget.Add(new() { Key = key, Purpose = task.Objective, InternalRole = _compactBindings ? copyLoop ? _indexedProjections ? "typed_index_projection" : "typed_projection" : "isolated_collection" : null, Type = task.Parallel ? "loop.parallel" : "loop.sequential", ItemVar = "item", IndexVar = "index",
                    Input = Object(task.Parallel ? [new("items", loopItems), new("max_concurrency", Number(task.MaxConcurrency))] : [new("items", loopItems)]), Steps = [body.Call] });
                outputs = new(StringComparer.Ordinal);
                foreach (var output in body.Workflow.Outputs)
                {
                    var schema = new JsonObject { ["type"] = "array", ["items"] = PlanningGraphCompiler.ToJsonSchema(output.Schema, _catalog) };
                    if (_compactBindings) schema["maxItems"] = task.MaxItems;
                    var projection = Key(key, "collect:" + output.Name); _sources[projection] = _location;
                    loopTarget.Add(new() { Key = projection, Type = "set", Input = Projection([new("value", Reference(key, "results")), new("paths", Array([Strings([body.Call.Key, "outputs", output.Name])])), new("each", new() { Kind = "boolean", Boolean = true })]), OutputSchema = Contract(ObjectSchema([("value", schema)])) });
                    outputs.Add(output.Name, Output(projection, "set", ["value"], schema));
                }
                outputs = Aggregate(outputs, key, loopTarget);
                if (loopScope != scope)
                {
                    foreach (var output in body.Workflow.Outputs)
                    {
                        var bound = outputs[output.Name];
                        loopScope.Workflow.Outputs.Add(new() { Name = output.Name, Value = bound.Value, Schema = Contract(bound.Schema) });
                    }
                    var invoke = Call(Key(loopScope.Workflow.Key, "call"), loopScope.Workflow.Key, Object(loopScope.Captures));
                    target.Add(invoke); outputs = WorkflowResult(invoke.Key, loopScope.Workflow);
                }
                break;
            default: Fail("TASK_KIND_INVALID", "Unknown semantic task kind."); return;
        }
        if (_normalExports && task.Kind is "call" or "sequence" or "foreach")
            foreach (var call in target.Skip(start).Where(n => n.Type == "workflow.call")) call.Purpose = task.Objective;
        scope.Tasks.Add(task.Id, outputs);
        // Cleanup can observe a failed/absent producer. Guard each generated stage before resolving inputs.
        if (cleanup)
        {
            foreach (var node in PlanningGraphCompiler.Enumerate(target.Skip(start)))
            {
                GuardCleanup(node);
            }
        }
        var afterRequirement = false;
        foreach (var node in target.Skip(start))
        {
            if (cleanup && afterRequirement)
                node.If = PlanningValues.And(new() { Kind = "present", Source = Key(key, "requires") }, node.If);
            if (task.Requires is not null && node.Key == Key(key, "requires")) afterRequirement = true;
            _sources.TryAdd(node.Key, "/tasks/" + task.Id);
            node.Dependencies = task.DependsOn.SelectMany(id => scope.Tasks[id].Values.Select(b => b.Value.Source)).OfType<string>().Concat(node.Dependencies).Distinct().ToList();
        }
    }

    private bool CopyValue(TaskValue value) => (value.Kind is "null" or "string" or "number" or "boolean" or "item" or "input" or "output" or "field" or "object" or "array" || _indexedProjections && value.Kind == "index") &&
        value.Items.All(CopyValue) && value.Members.All(m => CopyValue(m.Value));

    private bool PureProjection(PlanTask task)
    {
        // The entry guard is emitted before the collection; only per-item work prevents fusion.
        return task.Body is { Always.Count: 0 } body && body.Outputs.Count > 0 &&
            body.Tasks.All(t => t.Kind == "value" && t.Requires is null && t.DependsOn.Count == 0 && t.Outputs.All(o => CopyValue(o.Value))) &&
            body.Outputs.All(o => CopyValue(o.Value));
    }

    private static PlanningValue Projection(IEnumerable<PlanningMember> fields) => new() { Kind = "projection", Members = fields.ToList() };

    private Bound? ContractDefault(JsonObject schema)
    {
        if (!schema.TryGetPropertyValue("default", out var supplied)) return null;
        if (PlanningContractValidation.ValidateInstance(supplied, schema).Count > 0)
            Fail("CATALOG_DEFAULT_INVALID", "The declared default does not satisfy its authoritative contract.");
        var value = PlanningJsonTransport.Literal(supplied?.DeepClone());
        return new(value, new() { ["const"] = supplied?.DeepClone() });
    }

    private Bound BindInput(TaskValue value, JsonObject expected, Scope scope)
    {
        if (value.Kind == "object" && expected["properties"] is JsonObject properties)
        {
            Unique(value.Members.Select(m => m.Name));
            var fields = value.Members.Select(m => (m.Name, Bound: properties[m.Name] is JsonObject field
                ? BindInput(m.Value, field, scope) : Value(m.Value, scope))).ToList();
            foreach (var name in (expected["required"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
                if (fields.All(f => f.Name != name) && properties[name] is JsonObject field && ContractDefault(field) is { } fallback)
                    fields.Add((name, fallback));
            return new(Object(fields.Select(f => new PlanningMember(f.Name, f.Bound.Value))), ObjectSchema(fields.Select(f => (f.Name, f.Bound.Schema))));
        }
        if (value.Kind == "array" && expected["items"] is JsonObject itemSchema && value.Items.Count > 0)
        {
            var items = value.Items.Select(v => BindInput(v, itemSchema, scope)).ToArray();
            return new(Array(items.Select(v => v.Value)), new() { ["type"] = "array", ["minItems"] = items.Length, ["maxItems"] = items.Length,
                ["items"] = new JsonObject { ["anyOf"] = new JsonArray(items.Select(v => (JsonNode)v.Schema.DeepClone()).ToArray()) } });
        }
        var bound = Value(value, scope);
        var checkedConstraints = !PlanningGraphValidation.IsLiteral(bound.Value) && PlanningContractShapes.CanCheckConstraints(bound.Schema, expected);
        if (!checkedConstraints && !PlanningContractShapes.IsOpaque(bound.Schema) && !PlanningContractShapes.CanDefer(bound.Schema, expected)) return bound;
        var schema = expected.Count == 0 ? PlanningContractShapes.Opaque() : expected;
        var dynamic = !checkedConstraints && !PlanningContractShapes.IsOpaque(schema);
        if (!_catalog.AllowedStepTypes.Contains(dynamic ? "mapping.dynamic" : "set"))
            Fail("TASK_OUTPUT_POLICY", "The host does not permit this required binding adaptation.");
        if (scope.Target is null) return bound with { Schema = schema };
        var key = Key(scope.Workflow.Key, "normalize:" + _location + ":" + ValueIdentity(bound.Value) + ":" + schema.ToJsonString());
        if (!scope.Target.Any(n => n.Key == key))
        {
            var check = dynamic ? Mapping(key, Object([new("value", bound.Value)]), schema, "Adapt the observed value to the required consumer contract without inventing data.")
                : new PlanningNode { Key = key, Type = "set", Input = Projection([new("value", bound.Value), new("paths", Array([Strings([])]))]),
                    OutputSchema = Contract(ObjectSchema([("value", schema)])) };
            if (scope.Cleanup) GuardCleanup(check);
            scope.Target.Add(check); _sources[key] = _location;
        }
        return Output(key, "set", ["value"], schema);
    }

    private PlanningNode Mapping(string key, PlanningValue sources, JsonObject schema, string objective)
    {
        var contracts = new JsonArray(_catalog.Capabilities.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => (JsonNode)new JsonObject
            { ["id"] = c.Id, ["version"] = c.Version, ["input"] = c.InputSchema.DeepClone(), ["output"] = c.OutputSchema.DeepClone() }).ToArray());
        var node = new PlanningNode { Key = key, Type = "set", Purpose = objective,
            Input = new() { Kind = "dynamic_mapping", Members = [new("sources", sources), new("objective", Text(objective)),
                new("binding", Text(key)), new("producer_contract", Text(PlanningGraphCompiler.Fingerprint(contracts.ToJsonString())))] },
            OutputSchema = Contract(ObjectSchema([("value", schema)])) };
        if (_adaptiveMappings) node.Input.Members.Add(new("adaptive_each", new() { Kind = "boolean", Boolean = true }));
        if (_compactBindings) node.Input.Members.Add(new("infer_each", new() { Kind = "boolean", Boolean = true }));
        return node;
    }

    private PlanningOperation OperationInputs(PlanTask task, PlanningCapability capability, Scope scope)
    {
        var operation = PlanningCapabilityArguments.Editable(capability);
        if (capability.InputSchema["oneOf"] is not JsonArray) return operation;
        // Only literal selectors establish a branch. Other bindings retain their
        // ordinary checks; unknown or ambiguous selectors grant no refinement.
        var literals = task.Inputs.Where(i => Literal(i.Value) && !PlanningCapabilityArguments.Assignment(capability, i.Name, i.Value))
            .Select(i => (Port: operation.Inputs.SingleOrDefault(p => p.Name == i.Name), Value: i.Value)).Where(i => i.Port is not null)
            .Select(i => (i.Port!, Schema: new JsonObject { ["const"] = PlanningGraphValidation.Literal(Value(i.Value, scope).Value) }));
        try
        {
            var request = PlanningCapabilityArguments.EffectiveSchema(capability, literals);
            return PlanningCapabilityArguments.Editable(capability, SelectedInputBranch(request, capability.InputSchema));
        }
        catch (InvalidOperationException)
        { Fail("TASK_INPUT_BINDING", "Input mappings must assemble disjoint declared fields."); return operation; }
    }

    private Dictionary<string, Bound> Operation(PlanTask task, Scope scope, List<PlanningNode> target, string key)
    {
        var matches = _catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Operation).ToArray();
        if (matches.Length != 1) Fail("TASK_OPERATION_UNKNOWN", "Select one issued, unambiguous operation.");
        var capability = matches[0]; var operation = OperationInputs(task, capability, scope);
        if (_catalog.Policy.DeniedCapabilityIds.Contains(capability.Id) || !_catalog.AllowedStepTypes.Contains(capability.StepType)) Fail("TASK_OPERATION_DENIED", "The operation is outside the approved host policy.");
        Unique(task.Inputs.Select(i => i.Name));
        var input = Object([]);
        var scopeDependencies = new List<string>();
        foreach (var argument in task.Inputs)
        {
            _location = "/tasks/" + task.Id + "/inputs/" + argument.Name;
            var port = operation.Inputs.SingleOrDefault(p => p.Name == argument.Name);
            if (port is null) Fail("TASK_INPUT_UNKNOWN", "Choose a declared business input port: " + argument.Name);
            var bound = BindInput(argument.Value, port!.Schema, scope);
            if (capability.StepType == "agent.run")
            {
                var approved = ScopeValue(task, port!.Path[0], argument.Value);
                if (!ReferenceEquals(approved, argument.Value))
                {
                    scopeDependencies.AddRange(PlanningGraphTopology.ReferencedStages(bound.Value));
                    bound = Value(approved, scope);
                }
            }
            Fits(bound, port!.Schema, "TASK_INPUT_TYPE");
            Bind(input, port.Path, bound.Value);
        }
        foreach (var port in operation.Inputs.Where(p => p.Required))
            if (!task.Inputs.Any(i => i.Name == port.Name))
            {
                _location = "/tasks/" + task.Id + "/inputs/" + port.Name;
                if (ContractDefault(port.Schema) is { } fallback) Bind(input, port.Path, fallback.Value);
                else Fail("TASK_INPUT_REQUIRED", "Required business input: " + port.Name);
            }
        foreach (var argument in task.Inputs)
        {
            var value = input; var path = key + "/input" + (capability.StepType == "mcp.call" ? "/members/0/value" : "");
            foreach (var segment in operation.Inputs.Single(p => p.Name == argument.Name).Path)
            {
                var index = value.Members.FindIndex(m => m.Name == segment);
                path += "/members/" + index + "/value"; value = value.Members[index].Value;
            }
            _sources[path] = "/tasks/" + task.Id + "/inputs/" + argument.Name;
        }
        input = PlanningCapabilityArguments.Apply(input, capability);
        _location = "/tasks/" + task.Id;
        target.Add(new() { Key = key, Purpose = task.Objective, Type = capability.StepType, CapabilityId = capability.Id,
            Input = capability.StepType == "mcp.call" ? Object([new("request", input)]) : input, Dependencies = scopeDependencies });
        return OperationResults(key, capability, input);
    }

    private (PlanningWorkflow Workflow, PlanningNode Call) Child(TaskScope source, Scope parent, string key, string role, IReadOnlySet<string>? nonNull = null)
    {
        var childKey = Key(key, role); var workflow = new PlanningWorkflow { Key = childKey };
        _graph.Workflows.Add(workflow); _sources[childKey] = _location + "/" + (role switch
        { "yes" or "iteration" => "body", "no" => "otherwise", _ => role.Replace("branch:", "branches/", StringComparison.Ordinal) });
        var location = _location; var child = new Scope(workflow, parent) { NonNullReferences = nonNull, FailurePath = parent.FailurePath || parent.Cleanup }; CompileScope(source, child, childKey); _location = location;
        return (workflow, Call(Key(childKey, "call"), childKey, Object(child.Captures)));
    }

    private PlanningWorkflow Group(TaskGroup group)
    {
        if (_compilingGroups.Contains(group.Id)) Fail("TASK_GROUP_CYCLE", "Reusable groups cannot recurse.");
        if (_groups.TryGetValue(group.Id, out var existing)) return existing;
        var workflow = new PlanningWorkflow { Key = Key("group", group.Id) };
        _graph.Workflows.Add(workflow); _sources[workflow.Key] = "/groups/" + group.Id + "/body"; _groups.Add(group.Id, workflow); _compilingGroups.Add(group.Id);
        var location = _location; var scope = new Scope(workflow, null); AddInputs(scope, group.Inputs, "/groups/" + group.Id + "/inputs"); CompileScope(group.Body, scope, workflow.Key); _location = location;
        _compilingGroups.Remove(group.Id); return workflow;
    }

    private PlanningValue BindArguments(List<TaskOutput> arguments, Scope scope, List<PlanningPort> ports)
    {
        Unique(arguments.Select(a => a.Name));
        if (arguments.Any(a => ports.All(p => p.Name != a.Name)) || ports.Any(p => p.Required && p.Default is null && arguments.All(a => a.Name != p.Name)))
            Fail("TASK_GROUP_INPUTS", "Supply the declared reusable group's inputs.");
        return Object(arguments.Select(a =>
        {
            var expected = PlanningGraphCompiler.ToJsonSchema(ports.Single(p => p.Name == a.Name).Schema, _catalog); var bound = BindInput(a.Value, expected, scope);
            Fits(bound, expected, "TASK_GROUP_INPUT_TYPE");
            return new PlanningMember(a.Name, bound.Value);
        }));
    }

    private static IReadOnlySet<string> NonNullReferences(TaskValue? condition, bool whenTrue)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        if (condition is not { Kind: "predicate" }) return references;
        if (condition.Predicate == "not" && condition.Items.Count == 1)
            return NonNullReferences(condition.Items[0], !whenTrue);
        // True conjunctions and false disjunctions establish every operand's facts.
        // The opposite paths do not establish which operand determined the result.
        if (whenTrue && condition.Predicate == "and" || !whenTrue && condition.Predicate == "or")
            foreach (var item in condition.Items) references.UnionWith(NonNullReferences(item, whenTrue));
        else if (condition.Items.Count == 2 &&
            (whenTrue && condition.Predicate == "not_equal" || !whenTrue && condition.Predicate == "equal"))
        {
            var reference = condition.Items[0].Kind == "null" ? condition.Items[1] : condition.Items[1].Kind == "null" ? condition.Items[0] : null;
            if (reference?.Kind is "input" or "output" or "field")
                references.Add(JsonSerializer.Serialize(reference, PlanningJsonContext.Default.TaskValue));
        }
        return references;
    }

    private static JsonObject? WithoutNull(JsonObject source)
    {
        var schema = source.DeepClone().AsObject();
        if (schema["type"]?.ToString() == "null") return null;
        if (schema["type"] is JsonArray types)
        {
            var remaining = types.Where(t => t?.ToString() != "null").Select(t => t!.DeepClone()).ToArray();
            if (remaining.Length == 0) return null;
            schema["type"] = remaining.Length == 1 ? remaining[0] : new JsonArray(remaining);
        }
        foreach (var keyword in new[] { "anyOf", "oneOf" })
            if (schema[keyword] is JsonArray alternatives && alternatives.All(a => a is JsonObject))
            {
                var remaining = alternatives.Cast<JsonObject>().Select(WithoutNull).OfType<JsonObject>().ToArray();
                if (remaining.Length == 0) return null;
                schema[keyword] = new JsonArray(remaining.Select(s => (JsonNode)s).ToArray());
            }
        return schema;
    }

    private Bound Value(TaskValue value, Scope scope, bool consume = true)
    {
        var bound = ValueCore(value, scope, consume);
        if (!consume || value.Kind is not ("input" or "output" or "field")) return bound;
        var identity = JsonSerializer.Serialize(value, PlanningJsonContext.Default.TaskValue);
        for (var ancestor = scope; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.NonNullReferences?.Contains(identity) == true && WithoutNull(bound.Schema) is { } schema &&
                !JsonNode.DeepEquals(schema, bound.Schema))
            {
                // Keep captured contracts intact; check the refined value in the selected branch.
                return Consume(bound with { Schema = schema, SelectionSource = bound, SelectionPath = [] }, scope);
            }
        return bound;
    }

    private Bound ValueCore(TaskValue value, Scope scope, bool consume)
    {
        if (value.Kind == "output" && value.Source is not null && !scope.Tasks.ContainsKey(value.Source) &&
            _symbols.Values.TryGetValue(_location, out var site) && _symbols.ExportedReference(site.Scope, value) is { } exported)
            value = exported;
        // Presence reads the whole task result, which preflight withholds after an invalid export.
        var referenceKind = value.Kind == "present" ? "output" : value.Kind;
        var referencePort = value.Kind == "present" ? "" : value.Port ?? "";
        if (scope.Blocked.Contains((referenceKind, value.Source ?? "", referencePort)) ||
            scope.Blocked.Contains((referenceKind, value.Source ?? "", "*"))) throw new UnavailableValue();
        switch (value.Kind)
        {
            case "null": return new(new(), new() { ["type"] = "null" });
            case "string":
                if (value.Text is null || value.Text.Contains("${", StringComparison.Ordinal)) Fail("TASK_LITERAL_INVALID", "Literal text cannot contain runtime interpolation.");
                // Preserve an explicitly declared literal through named values and captures.
                // Workflow defaults and transform results keep their declared (possibly broader) types.
                return new(new() { Kind = "string", Text = value.Text }, new() { ["type"] = "string", ["const"] = value.Text });
            case "number":
                if (value.Number is null || !double.IsFinite(value.Number.Value)) Fail("TASK_LITERAL_INVALID", "A finite number is required.");
                return new(Number(value.Number!.Value), new() { ["type"] = "number" });
            case "boolean":
                if (value.Boolean is null) Fail("TASK_LITERAL_INVALID", "Boolean is required.");
                return new(new() { Kind = "boolean", Boolean = value.Boolean }, new() { ["type"] = "boolean" });
            case "object":
                Unique(value.Members.Select(m => m.Name));
                var fields = value.Members.Select(m => (m.Name, Bound: Value(m.Value, scope))).ToArray();
                return new(Object(fields.Select(f => new PlanningMember(f.Name, f.Bound.Value))), ObjectSchema(fields.Select(f => (f.Name, f.Bound.Schema))));
            case "array":
                var items = value.Items.Select(i => Value(i, scope)).ToArray();
                var schemas = items.Select(i => i.Schema).DistinctBy(s => s.ToJsonString()).ToArray();
                if (items.Length == 0) return new(Array([]), new() { ["type"] = "array", ["const"] = new JsonArray() });
                return new(Array(items.Select(i => i.Value)), new() { ["type"] = "array", ["items"] = schemas.Length == 1 ? schemas[0].DeepClone() : schemas.Length == 0 ? new JsonObject() : new JsonObject { ["anyOf"] = new JsonArray(schemas.Select(s => s.DeepClone()).ToArray()) } });
            case "choice":
                var choice = _plan.Choices.SingleOrDefault(c => c.Id == value.Source);
                if (choice is null) Fail("CHOICE_UNKNOWN", "Choose a declared business decision. '" + value.Source + "' is " +
                    (value.Source is not null && _symbols.Tasks.TryGetValue(value.Source, out var namedTask) ? "a " + namedTask.Task.Kind + " task, not a declared choice" : "not a declared choice") +
                    ". Declared choices: " + new JsonArray(_plan.Choices.Select(c => (JsonNode?)JsonValue.Create(c.Id)).ToArray()).ToJsonString() +
                    ". Operation data uses typed output references; present only establishes completion, not success.");
                if (choice!.Selected is null) Fail("CHOICE_REQUIRED", "Select a business alternative before compiling this task.");
                return Value(choice.Alternatives.Single(a => a.Id == choice.Selected).Value, scope);
            case "input":
                if (value.Source is not null && scope.Inputs.TryGetValue(value.Source, out var input)) return input;
                break;
            case "output":
                if (value.Source is not null && scope.Tasks.TryGetValue(value.Source, out var ports))
                {
                    if (string.IsNullOrEmpty(value.Port) && ports.Keys.All(string.IsNullOrEmpty) &&
                        _symbols.Tasks.TryGetValue(value.Source, out var producerTask) &&
                        producerTask.Task.Kind is "sequence" or "foreach" or "conditional" or "parallel" or "call")
                        Fail("TASK_EXPORT_REQUIRED", "This scope declares no business outputs. Select explicit scope exports for data; use dependsOn or present for ordering or presence." + _symbols.ReferenceContext(_location, value.Source));
                    if (ports.TryGetValue(value.Port ?? "", out var output)) return consume ? Consume(output, scope) : output;
                    Fail("TASK_OUTPUT_UNKNOWN", "Task '" + value.Source + "' has no declared business output '" + value.Port +
                        "'. Available ports: " + new JsonArray(ports.Keys.Where(p => p.Length > 0).Order(StringComparer.Ordinal).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()).ToJsonString() +
                        ". Opaque results cannot supply typed fields." + _symbols.ReferenceContext(_location, value.Source));
                }
                break;
            case "item": if (scope.Item is not null) return scope.Item; break;
            case "index": if (scope.Index is not null) return scope.Index; break;
            case "present":
                if (value.Source is not null && scope.Tasks.TryGetValue(value.Source, out var producer))
                    return new(new() { Kind = "present", Source = producer[""].Value.Source }, new() { ["type"] = "boolean" });
                if (scope.Parent is null) Fail("TASK_PRESENCE_SCOPE", "Presence requires a preceding task in this scope or a lexical ancestor. A scope export exposes data, not the inner task's completion." + _symbols.ReferenceContext(_location, value.Source));
                break;
            case "arithmetic": return Arithmetic(value, scope);
            case "predicate": return Predicate(value, scope);
            case "json": return EncodeJson(value, scope);
            case "flatten": return Flatten(value, scope);
            case "lookup": return Lookup(value, scope);
            case "field": return SelectField(value, scope, consume);
            default: Fail("TASK_VALUE_INVALID", "Values allow literals, business references, declared fields, JSON encoding, typed arithmetic and predicates only."); break;
        }
        if (value.Kind == "output" && scope.FailurePath && value.Source is not null)
            return CaptureFailureOutput(value, scope, consume);
        if (scope.Parent is null) Fail("TASK_REFERENCE_UNKNOWN", "Business reference '" + value.Source + "' is unavailable in this scope." +
            (value.Kind is "output" or "present" ? _symbols.ReferenceContext(_location, value.Source) : ""));
        // Capture the authoritative container, not an unchecked optional field.
        // Its check belongs inside the consuming branch/iteration/finalizer.
        var captured = Value(value, scope.Parent!, consume: false);
        // The iteration scope owns loop variables, but is not another workflow boundary.
        if (ReferenceEquals(scope.Workflow, scope.Parent!.Workflow)) return consume ? Consume(captured, scope) : captured;
        var name = Key(value.Kind, (value.Source ?? "") + ":" + value.Port);
        if (!scope.Inputs.TryGetValue(name, out var already))
        {
            var transfer = captured.SelectionSource ?? captured;
            scope.Workflow.Inputs.Add(new() { Name = name, Schema = Contract(transfer.Schema) });
            scope.Captures.Add(new(name, transfer.Value));
            var input = Input(name, transfer.Schema) with { TypeLocation = captured.TypeLocation };
            already = captured.SelectionSource is null ? input : captured with { SelectionSource = input };
            scope.Inputs[name] = already;
        }
        return consume ? Consume(already, scope) : already;
    }

    private Dictionary<string, Bound> OperationResults(string key, PlanningCapability capability, PlanningValue input)
    {
        JsonObject schema;
        try { schema = PlanningOperationResults.Resolve(capability.StepType, capability.OutputSchema, PlanningGraphValidation.Member(input, "output_schema")); }
        catch (ArgumentException ex) { Fail("TASK_INPUT_SCHEMA", ex.Message); return null!; }
        catch (InvalidOperationException ex) { Fail("TASK_COMPILER_VALIDATION", ex.Message); return null!; }
        var whole = Output(key, capability.StepType, [], schema);
        var results = new Dictionary<string, Bound>(StringComparer.Ordinal) { [""] = whole };
        foreach (var port in TaskOperations.Describe(capability).Outputs)
        {
            var selected = schema;
            foreach (var segment in port.Path) selected = selected["properties"]![segment]!.AsObject();
            var result = Output(key, capability.StepType, port.Path, selected);
            results.Add(port.Name, TaskOperations.OutputNeedsCheck(schema, port.Path)
                ? result with { SelectionSource = whole, SelectionPath = port.Path } : result);
        }
        return results;
    }

    private Bound Consume(Bound value, Scope scope)
    {
        if (value.SelectionSource is not { } source) return value;
        if (!_catalog.AllowedStepTypes.Contains("set")) Fail("TASK_OUTPUT_POLICY", "Consuming this business field requires an allowed deterministic presence check.");
        if (value.Schema.Count == 0 || value.Schema["x-gnougo-opaque"]?.ToString() == "true")
            Fail("TASK_OUTPUT_CONTRACT", "An opaque optional result cannot establish a typed business field.");
        if (scope.Target is null) return value; // Semantic preflight: no graph emission.
        var key = Key(scope.Workflow.Key, "select:" + _location + ":" + ValueIdentity(source.Value) + ":" + new JsonArray(value.SelectionPath!.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()).ToJsonString());
        if (!scope.Target.Any(n => n.Key == key))
        {
            var node = new PlanningNode { Key = key, Type = "set",
                Input = Projection([new("value", source.Value), new("paths", Array([Strings(value.SelectionPath!)]))]),
                OutputSchema = Contract(ObjectSchema([("value", value.Schema)])) };
            if (scope.Cleanup) GuardCleanup(node);
            scope.Target.Add(node); _sources[key] = _location;
        }
        return Output(key, "set", ["value"], value.Schema) with { TypeLocation = value.TypeLocation };
    }

    private Bound Arithmetic(TaskValue value, Scope scope)
    {
        if (value.Source is not null || value.Port is not null || value.Predicate is not null || value.Number is not null ||
            value.Boolean is not null || value.Members.Count != 0)
            Fail("TASK_ARITHMETIC_INVALID", "Arithmetic contains unsupported fields.");
        var operands = value.Items.Select(i => Value(i, scope)).ToArray();
        var computation = new PlanningValue { Kind = "arithmetic", Text = value.Text, Items = operands.Select(o => o.Value).ToList() };
        try { return new(computation, PlanningValues.ComputationContract(computation, operand => operands.First(o => ReferenceEquals(o.Value, operand)).Schema)); }
        catch (InvalidOperationException ex) { Fail("TASK_ARITHMETIC_INVALID", ex.Message); throw; }
    }

    private Bound Flatten(TaskValue value, Scope scope)
    {
        if (value.Source is not null || value.Port is not null || value.Text is not null || value.Predicate is not null ||
            value.Number is not null || value.Boolean is not null || value.Members.Count != 0 || value.Items.Count != 1)
            Fail("TASK_FLATTEN_INVALID", "Flatten requires exactly one typed array-of-arrays operand and no other fields.");
        var source = Value(value.Items[0], scope);
        var computation = new PlanningValue { Kind = "flatten", Items = [source.Value] };
        JsonObject schema;
        try { schema = PlanningValues.ComputationContract(computation, _ => source.Schema); }
        catch (InvalidOperationException ex)
        {
            Fail("TASK_FLATTEN_INVALID", ex.Message + " Received " + Describe(source.Schema) +
                (source.Schema["items"] is JsonObject element ? " with items " + Describe(element) : "") +
                " from " + string.Join(", ", Values(value.Items[0]).Where(v => v.Kind is "output" or "input" or "item")
                    .Select(v => v.Kind + ":" + v.Source + (v.Port is null ? "" : "." + v.Port) +
                        (v.Kind == "output" && v.Source is { } id && _symbols.Tasks.TryGetValue(id, out var producer) && producer.Task.Kind == "transform"
                            ? " (declaration /tasks/" + id + "/resultType)" : "")).DefaultIfEmpty("the explicit " + value.Items[0].Kind + " binding")) +
                ". Per-item array extraction must declare the assembled array-of-arrays; do not use json text or cast the consumer.");
            throw;
        }
        // Validate the original collection before removing a boundary, including
        // constraints that cannot be transferred to the concatenated result.
        var checkedSource = Consume(source with { SelectionSource = source, SelectionPath = [] }, scope);
        computation.Items = [checkedSource.Value];
        return new(computation, schema);
    }

    private Bound Lookup(TaskValue value, Scope scope)
    {
        if (value.Items.Count != 2 || string.IsNullOrWhiteSpace(value.Port) || value.Source is not null || value.Text is not null ||
            value.Number is not null || value.Boolean is not null || value.Predicate is not null || value.Members.Count != 0)
            Fail("TASK_LOOKUP_INVALID", "Lookup requires records and selected identities in items, and one literal identity field in port.");
        var records = Value(value.Items[0], scope); var selected = Value(value.Items[1], scope);
        var computation = new PlanningValue { Kind = "lookup", Text = value.Port, Items = [records.Value, selected.Value] };
        JsonObject schema;
        try { schema = PlanningValues.ComputationContract(computation, operand => ReferenceEquals(operand, records.Value) ? records.Schema : selected.Schema); }
        catch (InvalidOperationException ex)
        {
            Fail("TASK_LOOKUP_INVALID", ex.Message + " " + Operand(0, records, "records: array of objects declaring identity field '" + value.Port + "'") +
                " " + Operand(1, selected, "IDs: array of strings or safe integers compatible with the record identity") +
                " json produces text, not a record collection.");
            throw;
        }
        computation.Items = [Consume(records with { SelectionSource = records, SelectionPath = [] }, scope).Value,
            Consume(selected with { SelectionSource = selected, SelectionPath = [] }, scope).Value];
        return new(computation, schema);

        string Operand(int index, Bound operand, string expected) => "items[" + index + "] expected " + expected + "; received " +
            (operand.Schema["type"]?.ToJsonString() ?? "union/opaque") +
            (operand.Schema["items"] is JsonObject element ? " with items " + (element["type"]?.ToJsonString() ?? "union/opaque") : "") +
            " from " + string.Join(", ", Values(value.Items[index]).Where(v => v.Kind is "output" or "input" or "item")
                .Select(v => v.Kind + ":" + v.Source + (v.Port is null ? "" : "." + v.Port)).DefaultIfEmpty("the explicit " + value.Items[index].Kind + " binding")) + ".";
    }

    private Bound Predicate(TaskValue value, Scope scope)
    {
        if (value.Predicate is "and" or "or" && value.Items.Count == 2 && scope.Target is { } target)
        {
            var left = Value(value.Items[0], scope); RequireBoolean(left);
            var location = _location; var rightNodes = new List<PlanningNode>(); Bound right;
            scope.Target = rightNodes; _location += "/items/1";
            try { right = Value(value.Items[1], scope); RequireBoolean(right); }
            finally { scope.Target = target; _location = location; }
            if (rightNodes.Count == 0)
                return new(PlanningValues.Predicate(value.Predicate, left.Value, right.Value), new() { ["type"] = "boolean" });
            // Checks of a selected field must short-circuit along with the predicate.
            // Use native control flow only when the right operand needs materialization.
            var key = Key(scope.Workflow.Key, "predicate:" + location + ":" + JsonSerializer.Serialize(value, PlanningJsonContext.Default.TaskValue));
            var selected = Key(key, "selected"); var skipped = Key(key, "skipped"); var merged = Key(key, "result");
            var schema = ObjectSchema([("value", new JsonObject { ["type"] = "boolean" })]);
            if (!target.Any(n => n.Key == merged))
            {
                rightNodes.Add(new() { Key = selected, Type = "set", Input = Object([new("value", right.Value)]), OutputSchema = Contract(schema) });
                target.Add(new() { Key = key, Type = "switch", Expr = left.Value,
                    Cases = [new(value.Predicate == "and" ? "true" : "false", null, rightNodes)],
                    Default = [new() { Key = skipped, Type = "set", Input = Object([new("value", new() { Kind = "boolean", Boolean = value.Predicate == "or" })]), OutputSchema = Contract(schema) }] });
                target.Add(new() { Key = merged, Type = "set", Input = Projection([new("value", Reference(key)),
                    new("paths", Array([Strings([selected, "value"]), Strings([skipped, "value"])]))]), OutputSchema = Contract(schema) });
                _sources[key] = location; _sources[merged] = location;
            }
            return Output(merged, "set", ["value"], new() { ["type"] = "boolean" });
        }
        var operands = value.Items.Select(i => Value(i, scope)).ToArray();
        var unary = value.Predicate == "not";
        if (operands.Length != (unary ? 1 : 2)) Fail("TASK_PREDICATE_INVALID", "Predicate arity is invalid.");
        var op = value.Predicate switch { "not" => "!", "and" => "&&", "or" => "||", "equal" => "===", "not_equal" => "!==", "less" => "<", "less_equal" => "<=", "greater" => ">", "greater_equal" => ">=", _ => null };
        if (op is null) Fail("TASK_PREDICATE_INVALID", "Unknown typed predicate.");
        if (value.Predicate is "and" or "or" or "not") foreach (var operand in operands) RequireBoolean(operand);
        else if (value.Predicate is not ("equal" or "not_equal") && operands.Any(b => b.Schema["type"]?.ToString() is not ("number" or "integer")))
            Fail("TASK_PREDICATE_TYPE", "Ordering predicates require numeric operands.");
        return new(PlanningValues.Predicate(value.Predicate!, operands.Select(o => o.Value).ToArray()), new() { ["type"] = "boolean" });
    }

    private Dictionary<string, Bound> Aggregate(Dictionary<string, Bound> outputs, string key, List<PlanningNode> target)
    {
        var aggregate = Key(key, "outputs"); var schema = ObjectSchema(outputs.Select(p => (p.Key, p.Value.Schema)));
        target.Add(new() { Key = aggregate, Type = "set", Input = Object(outputs.Select(p => new PlanningMember(p.Key, p.Value.Value))), OutputSchema = Contract(schema) });
        return Result(aggregate, "set", schema);
    }
    private static Dictionary<string, Bound> WorkflowResult(string key, PlanningWorkflow workflow) => Result(key, "workflow.call", ObjectSchema(workflow.Outputs.Select(o => (o.Name, o.Schema.Contract!))));
    private static Dictionary<string, Bound> Result(string key, string type, JsonObject schema)
    {
        var outputs = new Dictionary<string, Bound>(StringComparer.Ordinal) { [""] = Output(key, type, [], schema) };
        if (schema["properties"] is JsonObject properties)
            foreach (var (name, field) in properties) outputs.Add(name, Output(key, type, [name], field!.AsObject()));
        return outputs;
    }
    private static Bound Output(string key, string type, List<string> path, JsonObject schema) => new(Reference(key, path.ToArray()),
        type == "mcp.call" && schema.Count == 0 ? PlanningContractShapes.Opaque() : schema);
    private static Bound Input(string name, JsonObject schema) => new(new() { Kind = "input", Source = name }, schema);
    private static PlanningNode Call(string key, string workflow, PlanningValue args) => new() { Key = key, Type = "workflow.call", Input = Object([new("ref", new() { Kind = "workflow", Source = workflow }), new("args", args)]) };
    private static PlanningValue Reference(string key, params string[] path) => new() { Kind = "output", Source = key, Path = path.ToList() };
    private static PlanningValue Object(IEnumerable<PlanningMember> members) => new() { Kind = "object", Members = members.ToList() };
    private static PlanningValue Array(IEnumerable<PlanningValue> items) => new() { Kind = "array", Items = items.ToList() };
    private static PlanningValue Strings(IEnumerable<string> items) => Array(items.Select(s => new PlanningValue { Kind = "string", Text = s }));
    private static PlanningValue Number(double number) => new() { Kind = "number", Number = number };
    private static JsonObject ObjectSchema(IEnumerable<(string Name, JsonObject Schema)> fields)
    {
        var ports = fields.ToArray(); return new() { ["type"] = "object", ["properties"] = new JsonObject(ports.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema.DeepClone()))), ["required"] = new JsonArray(ports.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false };
    }
    private static PlanningSchema Contract(JsonObject schema) => new() { Contract = schema.Count == 0 ? PlanningContractShapes.Opaque() : schema.DeepClone().AsObject() };
    private static string Key(string parent, string role) => "task_" + PlanningGraphCompiler.Fingerprint(parent + "/" + role)[..24];
    private static string ValueIdentity(PlanningValue value) => JsonSerializer.Serialize(value, PlanningJsonContext.Default.PlanningValue);
    private static bool Literal(TaskValue value) => value.Kind is "null" or "string" or "number" or "boolean" || value.Kind == "object" && value.Members.All(m => Literal(m.Value)) || value.Kind == "array" && value.Items.All(Literal);
    private void RequireBoolean(Bound value) { if (value.Schema["type"]?.ToString() != "boolean") Fail("TASK_CONDITION_TYPE", "Conditions require a boolean business value."); }
    private JsonObject Schema(TaskType type) { try { return TypeSchema(type); } catch (ArgumentException ex) { Fail("TASK_TYPE_INVALID", ex.Message); return null!; } }
    private void Unique(IEnumerable<string> names)
    {
        var list = names.ToArray();
        if (list.Any(n => string.IsNullOrWhiteSpace(n) || n.StartsWith("__", StringComparison.Ordinal)) || list.Distinct(StringComparer.Ordinal).Count() != list.Length)
            Fail("TASK_IDENTITY_INVALID", "Business identities must be distinct, nonempty and outside reserved namespaces.");
    }
    private TaskScope MissingScope() { Fail("TASK_SCOPE_REQUIRED", "This task requires a semantic scope."); return null!; }
    private TaskValue MissingValue() { Fail("TASK_VALUE_REQUIRED", "This task requires a business value."); return null!; }
    private void Fail(string code, string message) => throw new InvalidTask(code, _location, message);
    private void Bind(PlanningValue root, List<string> path, PlanningValue value)
    {
        for (var i = 0; i < path.Count; i++)
        {
            var found = root.Members.SingleOrDefault(m => m.Name == path[i]);
            if (i == path.Count - 1)
            { if (found is not null) Fail("TASK_INPUT_CONFLICT", "Business inputs overlap the same contract field."); root.Members.Add(new(path[i], value)); }
            else
            {
                if (found is null) { found = new(path[i], Object([])); root.Members.Add(found); }
                if (found.Value.Kind != "object") Fail("TASK_INPUT_CONFLICT", "Business input mappings overlap.");
                root = found.Value;
            }
        }
    }
    internal static IEnumerable<TaskValue> Values(PlanTask task) => task.Inputs.Concat(task.Outputs).SelectMany(o => Values(o.Value))
        .Concat(task.Requires is null ? [] : Values(task.Requires)).Concat(task.Condition is null ? [] : Values(task.Condition)).Concat(task.Items is null ? [] : Values(task.Items));
    internal static IEnumerable<TaskValue> Values(TaskValue value) => new[] { value }.Concat(value.Members.SelectMany(m => Values(m.Value))).Concat(value.Items.SelectMany(Values));
}
