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
        return diagnostic with { Location = location };
    }
}

/// <summary>One deterministic lowering pass. Symbols and source locations are transient compiler bookkeeping.</summary>
public sealed partial class TaskPlanCompiler
{
    private sealed record Bound(PlanningValue Value, JsonObject Schema, string Expression);
    private sealed class Scope(PlanningWorkflow workflow, Scope? parent)
    {
        public PlanningWorkflow Workflow { get; } = workflow;
        public Scope? Parent { get; } = parent;
        public Dictionary<string, Bound> Inputs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, Bound>> Tasks { get; } = new(StringComparer.Ordinal);
        public List<PlanningMember> Captures { get; } = [];
        public HashSet<(string Kind, string Source, string Port)> Blocked { get; } = [];
        public Bound? Item { get; set; }
        public Bound? Index { get; set; }
    }
    private sealed class InvalidTask(string code, string location, string message) : Exception(message)
    { public PlanningDiagnostic Diagnostic { get; } = new(code, location, message); }
    private TaskPlan _plan = null!;
    private PlanningCatalog _catalog = null!;
    private PlanningGraph _graph = new();
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compilingGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PlanningWorkflow> _groups = new(StringComparer.Ordinal);
    private string _location = "/tasks";

    public TaskCompilation Compile(TaskPlan plan, PlanningCatalog catalog)
    {
        _plan = plan; _catalog = catalog; _location = "/"; _graph = new(); _sources.Clear(); _groups.Clear(); _compilingGroups.Clear();
        try
        {
            var findings = Preflight();
            if (findings.Count > 0) return new(null, findings, new Dictionary<string, string>());
            var main = new PlanningWorkflow(); _graph.Workflows.Add(main); _sources["main"] = "/root";
            var scope = new Scope(main, null); AddInputs(scope, plan.Inputs, "/inputs");
            CompileScope(plan.Root, scope, "main");
            return new(_graph, [], new Dictionary<string, string>(_sources));
        }
        catch (InvalidTask error) { return new(null, [error.Diagnostic with { Code = "TASK_COMPILER_VALIDATION", Message = error.Diagnostic.Code + ": " + error.Message }], new Dictionary<string, string>(_sources)); }
    }

    public static JsonObject TypeSchema(TaskType type)
    {
        if (type.Kind is not ("string" or "number" or "integer" or "boolean" or "object" or "array" or "any"))
            throw new ArgumentException("Unknown business type.");
        if (type.Kind == "any") return new();
        var schema = new JsonObject { ["type"] = type.Nullable ? new JsonArray(type.Kind, "null") : JsonValue.Create(type.Kind) };
        if (type.Kind == "array") schema["items"] = TypeSchema(type.Items ?? throw new ArgumentException("Array items require a business type."));
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
        if (scope.Workflow.Steps.Count == 0)
        {
            var empty = Key(key, "empty"); _sources[empty] = "/tasks/" + key;
            scope.Workflow.Steps.Add(new() { Key = empty, Type = "set", Input = Object([]) });
        }
        Unique(source.Outputs.Select(o => o.Name));
        foreach (var output in source.Outputs)
        {
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
        _location = "/tasks/" + task.Id;
        if (string.IsNullOrWhiteSpace(task.Objective)) Fail("TASK_OBJECTIVE_REQUIRED", "Each task requires an objective.");
        if (task.DependsOn.Any(d => !scope.Tasks.ContainsKey(d))) Fail("TASK_DEPENDENCY_UNKNOWN", "A dependency must name a preceding task in this scope.");
        var key = Key(parent, task.Id); _sources[key] = _location;
        var start = target.Count;
        Dictionary<string, Bound> outputs;
        switch (task.Kind)
        {
            case "operation": outputs = Operation(task, scope, target, key); break;
            case "transform": outputs = Transform(task, scope, target, key); break;
            case "value":
                Unique(task.Outputs.Select(o => o.Name));
                var values = task.Outputs.Select(o => (o.Name, Bound: Value(o.Value, scope))).ToArray();
                target.Add(new() { Key = key, Purpose = task.Objective, Type = "set", Input = Object(values.Select(v => new PlanningMember(v.Name, v.Bound.Value))), OutputSchema = Contract(ObjectSchema(values.Select(v => (v.Name, v.Bound.Schema)))) });
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
                var yes = Child(task.Body ?? MissingScope(), scope, key, "yes");
                var no = Child(task.Otherwise ?? MissingScope(), scope, key, "no");
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
                    target.Add(new() { Key = projection, Type = "value.project", Input = Object([
                        new("value", Reference(key)), new("paths", Array([Strings([yes.Call.Key, "outputs", output.Name]), Strings([no.Call.Key, "outputs", output.Name])]))]),
                        OutputSchema = Contract(ObjectSchema([("value", schema)])) });
                    outputs.Add(output.Name, Output(projection, "value.project", ["value"], schema));
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
                if (items.Schema["type"]?.ToString() != "array" || items.Schema["items"] is not JsonObject itemSchema) Fail("TASK_ITEMS_INVALID", "Iteration needs an authoritative array contract.");
                itemSchema = items.Schema["items"]!.AsObject();
                var bounded = items.Schema.DeepClone().AsObject(); bounded["maxItems"] = task.MaxItems;
                var checkedKey = Key(key, "bound"); _sources[checkedKey] = _location;
                target.Add(new() { Key = checkedKey, Type = "value.validate", Input = Object([new("value", items.Value)]), OutputSchema = Contract(ObjectSchema([("value", bounded)])) });
                var iteration = new Scope(scope.Workflow, scope) { Item = new(new() { Kind = "loop_item", Source = key }, itemSchema, "data.item"), Index = new(new() { Kind = "loop_index", Source = key }, new() { ["type"] = "integer" }, "data.index") };
                var body = Child(task.Body ?? MissingScope(), iteration, key, "iteration");
                target.Add(new() { Key = key, Purpose = task.Objective, Type = task.Parallel ? "loop.parallel" : "loop.sequential", ItemVar = "item", IndexVar = "index",
                    Input = Object(task.Parallel ? [new("items", Reference(checkedKey, "value")), new("max_concurrency", Number(task.MaxConcurrency))] : [new("items", Reference(checkedKey, "value"))]), Steps = [body.Call] });
                outputs = new(StringComparer.Ordinal);
                foreach (var output in body.Workflow.Outputs)
                {
                    var schema = new JsonObject { ["type"] = "array", ["items"] = PlanningGraphCompiler.ToJsonSchema(output.Schema, _catalog) };
                    var projection = Key(key, "collect:" + output.Name); _sources[projection] = _location;
                    target.Add(new() { Key = projection, Type = "array.project", Input = Object([new("items", Reference(key, "results")), new("path", Strings([body.Call.Key, "outputs", output.Name]))]), OutputSchema = Contract(ObjectSchema([("values", schema)])) });
                    outputs.Add(output.Name, Output(projection, "array.project", ["values"], schema));
                }
                outputs = Aggregate(outputs, key, target); break;
            default: Fail("TASK_KIND_INVALID", "Unknown semantic task kind."); return;
        }
        scope.Tasks.Add(task.Id, outputs);
        // Cleanup can observe a failed/absent producer. Guard each generated stage before resolving inputs.
        if (cleanup)
        {
            foreach (var node in target.Skip(start))
            {
                GuardCleanup(node);
            }
        }
        foreach (var node in target.Skip(start))
        {
            _sources.TryAdd(node.Key, "/tasks/" + task.Id);
            node.Dependencies = task.DependsOn.SelectMany(id => scope.Tasks[id].Values.Select(b => b.Value.Source)).OfType<string>().Distinct().ToList();
        }
    }

    private static void GuardCleanup(PlanningNode node)
    {
        var producers = PlanningGraphTopology.ReferencedStages(node.Input).Distinct(StringComparer.Ordinal).ToArray();
        if (producers.Length > 0) node.If = new() { Kind = "expression", Text = string.Join(" && ", producers.Select(p => "data.steps[" + Quote(p) + "] != null")) };
    }

    private Dictionary<string, Bound> Operation(PlanTask task, Scope scope, List<PlanningNode> target, string key)
    {
        var matches = _catalog.Capabilities.Where(c => TaskOperations.Describe(c).Id == task.Operation).ToArray();
        if (matches.Length != 1) Fail("TASK_OPERATION_UNKNOWN", "Select one issued, unambiguous operation.");
        var capability = matches[0]; var operation = TaskOperations.Describe(capability);
        if (_catalog.Policy.DeniedCapabilityIds.Contains(capability.Id) || !_catalog.AllowedStepTypes.Contains(capability.StepType)) Fail("TASK_OPERATION_DENIED", "The operation is outside the approved host policy.");
        Unique(task.Inputs.Select(i => i.Name));
        var input = Object([]);
        foreach (var argument in task.Inputs)
        {
            _location = "/tasks/" + task.Id + "/inputs/" + argument.Name;
            var port = operation.Inputs.SingleOrDefault(p => p.Name == argument.Name);
            if (port is null) Fail("TASK_INPUT_UNKNOWN", "Choose a declared business input port: " + argument.Name);
            if (capability.StepType == "agent.run" && port!.Path[0] is "objective" or "workspace" or "capabilities" or "budget" or "verification" or "output_schema" && !Literal(argument.Value))
                Fail("AGENT_SCOPE_DYNAMIC", "Agent scope fields must be literal before approval; choices and runtime references cannot change them.");
            var bound = Value(argument.Value, scope);
            Fits(bound, port!.Schema, "TASK_INPUT_TYPE");
            Bind(input, port.Path, bound.Value);
        }
        foreach (var port in operation.Inputs.Where(p => p.Required))
            if (!task.Inputs.Any(i => i.Name == port.Name))
            { _location = "/tasks/" + task.Id + "/inputs/" + port.Name; Fail("TASK_INPUT_REQUIRED", "Required business input: " + port.Name); }
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
        _location = "/tasks/" + task.Id;
        target.Add(new() { Key = key, Purpose = task.Objective, Type = capability.StepType, CapabilityId = capability.Id,
            Input = capability.StepType == "mcp.call" ? Object([new("request", input)]) : input });
        var outputs = new Dictionary<string, Bound>(StringComparer.Ordinal) { [""] = Output(key, capability.StepType, [], capability.OutputSchema) };
        foreach (var port in operation.Outputs) outputs.Add(port.Name, Output(key, capability.StepType, port.Path, port.Schema));
        return outputs;
    }

    private (PlanningWorkflow Workflow, PlanningNode Call) Child(TaskScope source, Scope parent, string key, string role)
    {
        var childKey = Key(key, role); var workflow = new PlanningWorkflow { Key = childKey };
        _graph.Workflows.Add(workflow); _sources[childKey] = _location + "/" + (role switch
        { "yes" or "iteration" => "body", "no" => "otherwise", _ => role.Replace("branch:", "branches/", StringComparison.Ordinal) });
        var location = _location; var child = new Scope(workflow, parent); CompileScope(source, child, childKey); _location = location;
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
        if (arguments.Any(a => ports.All(p => p.Name != a.Name)) || ports.Any(p => p.Required && arguments.All(a => a.Name != p.Name)))
            Fail("TASK_GROUP_INPUTS", "Supply the declared reusable group's inputs.");
        return Object(arguments.Select(a =>
        {
            var bound = Value(a.Value, scope); var expected = PlanningGraphCompiler.ToJsonSchema(ports.Single(p => p.Name == a.Name).Schema, _catalog);
            Fits(bound, expected, "TASK_GROUP_INPUT_TYPE");
            return new PlanningMember(a.Name, bound.Value);
        }));
    }

    private Bound Value(TaskValue value, Scope scope)
    {
        if (scope.Blocked.Contains((value.Kind, value.Source ?? "", value.Port ?? "")) ||
            scope.Blocked.Contains((value.Kind, value.Source ?? "", "*"))) throw new UnavailableValue();
        switch (value.Kind)
        {
            case "null": return new(new(), new() { ["type"] = "null" }, "null");
            case "string":
                if (value.Text is null || value.Text.Contains("${", StringComparison.Ordinal)) Fail("TASK_LITERAL_INVALID", "Literal text cannot contain runtime interpolation.");
                return new(new() { Kind = "string", Text = value.Text }, new() { ["type"] = "string" }, Quote(value.Text!));
            case "number":
                if (value.Number is null) Fail("TASK_LITERAL_INVALID", "Number is required.");
                return new(Number(value.Number!.Value), new() { ["type"] = "number" }, value.Number.Value.ToString(CultureInfo.InvariantCulture));
            case "boolean":
                if (value.Boolean is null) Fail("TASK_LITERAL_INVALID", "Boolean is required.");
                return new(new() { Kind = "boolean", Boolean = value.Boolean }, new() { ["type"] = "boolean" }, value.Boolean!.Value ? "true" : "false");
            case "object":
                Unique(value.Members.Select(m => m.Name));
                var fields = value.Members.Select(m => (m.Name, Bound: Value(m.Value, scope))).ToArray();
                return new(Object(fields.Select(f => new PlanningMember(f.Name, f.Bound.Value))), ObjectSchema(fields.Select(f => (f.Name, f.Bound.Schema))), "({" + string.Join(",", fields.Select(f => Quote(f.Name) + ":" + f.Bound.Expression)) + "})");
            case "array":
                var items = value.Items.Select(i => Value(i, scope)).ToArray();
                var schemas = items.Select(i => i.Schema).DistinctBy(s => s.ToJsonString()).ToArray();
                return new(Array(items.Select(i => i.Value)), new() { ["type"] = "array", ["items"] = schemas.Length == 1 ? schemas[0].DeepClone() : schemas.Length == 0 ? new JsonObject() : new JsonObject { ["anyOf"] = new JsonArray(schemas.Select(s => s.DeepClone()).ToArray()) } }, "[" + string.Join(",", items.Select(i => i.Expression)) + "]");
            case "choice":
                var choice = _plan.Choices.SingleOrDefault(c => c.Id == value.Source);
                if (choice is null) Fail("CHOICE_UNKNOWN", "Choose a declared business decision.");
                if (choice!.Selected is null) Fail("CHOICE_REQUIRED", "Select a business alternative before compiling this task.");
                return Value(choice.Alternatives.Single(a => a.Id == choice.Selected).Value, scope);
            case "input":
                if (value.Source is not null && scope.Inputs.TryGetValue(value.Source, out var input)) return input;
                break;
            case "output":
                if (value.Source is not null && scope.Tasks.TryGetValue(value.Source, out var ports))
                {
                    if (ports.TryGetValue(value.Port ?? "", out var output)) return output;
                    Fail("TASK_OUTPUT_UNKNOWN", "This task has no declared business output '" + value.Port + "'. Opaque results cannot supply typed fields.");
                }
                break;
            case "item": if (scope.Item is not null) return scope.Item; break;
            case "index": if (scope.Index is not null) return scope.Index; break;
            case "present":
                if (value.Source is null || !scope.Tasks.TryGetValue(value.Source, out var producer)) Fail("TASK_PRESENCE_SCOPE", "Presence requires a preceding task in the same scope.");
                var result = scope.Tasks[value.Source!][""];
                return new(new() { Kind = "present", Source = result.Value.Source }, new() { ["type"] = "boolean" }, "data.steps[" + Quote(result.Value.Source!) + "] != null");
            case "predicate": return Predicate(value, scope);
            default: Fail("TASK_VALUE_INVALID", "Values allow literals, business references and typed predicates only."); break;
        }
        if (scope.Parent is null) Fail("TASK_REFERENCE_UNKNOWN", "Business reference '" + value.Source + "' is unavailable in this scope.");
        var captured = Value(value, scope.Parent!);
        var name = Key(value.Kind, (value.Source ?? "") + ":" + value.Port);
        if (scope.Inputs.TryGetValue(name, out var already)) return already;
        scope.Workflow.Inputs.Add(new() { Name = name, Schema = Contract(captured.Schema) });
        scope.Captures.Add(new(name, captured.Value));
        return scope.Inputs[name] = Input(name, captured.Schema);
    }

    private Bound Predicate(TaskValue value, Scope scope)
    {
        var operands = value.Items.Select(i => Value(i, scope)).ToArray();
        var unary = value.Predicate == "not";
        if (operands.Length != (unary ? 1 : 2)) Fail("TASK_PREDICATE_INVALID", "Predicate arity is invalid.");
        var op = value.Predicate switch { "not" => "!", "and" => "&&", "or" => "||", "equal" => "===", "not_equal" => "!==", "less" => "<", "less_equal" => "<=", "greater" => ">", "greater_equal" => ">=", _ => null };
        if (op is null) Fail("TASK_PREDICATE_INVALID", "Unknown typed predicate.");
        if (value.Predicate is "and" or "or" or "not") foreach (var operand in operands) RequireBoolean(operand);
        else if (value.Predicate is not ("equal" or "not_equal") && operands.Any(b => b.Schema["type"]?.ToString() is not ("number" or "integer")))
            Fail("TASK_PREDICATE_TYPE", "Ordering predicates require numeric operands.");
        var expression = unary ? "!(" + operands[0].Expression + ")" : "(" + operands[0].Expression + " " + op + " " + operands[1].Expression + ")";
        return new(new() { Kind = "expression", Text = expression }, new() { ["type"] = "boolean" }, expression);
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
    private static Bound Output(string key, string type, List<string> path, JsonObject schema) => new(Reference(key, path.ToArray()), schema,
        "data.steps[" + Quote(key) + "]" + (type == "mcp.call" ? ".response" : type == "workflow.call" ? ".outputs" : "") + string.Concat(path.Select(p => "[" + Quote(p) + "]")));
    private static Bound Input(string name, JsonObject schema) => new(new() { Kind = "input", Source = name }, schema, "data.inputs[" + Quote(name) + "]");
    private static PlanningNode Call(string key, string workflow, PlanningValue args) => new() { Key = key, Type = "workflow.call", Input = Object([new("ref", new() { Kind = "workflow", Source = workflow }), new("args", args)]) };
    private static PlanningValue Reference(string key, params string[] path) => new() { Kind = "output", Source = key, Path = path.ToList() };
    private static PlanningValue Object(IEnumerable<PlanningMember> members) => new() { Kind = "object", Members = members.ToList() };
    private static PlanningValue Array(IEnumerable<PlanningValue> items) => new() { Kind = "array", Items = items.ToList() };
    private static PlanningValue Strings(IEnumerable<string> items) => Array(items.Select(s => new PlanningValue { Kind = "string", Text = s }));
    private static PlanningValue Number(decimal number) => new() { Kind = "number", Number = number };
    private static JsonObject ObjectSchema(IEnumerable<(string Name, JsonObject Schema)> fields)
    {
        var ports = fields.ToArray(); return new() { ["type"] = "object", ["properties"] = new JsonObject(ports.Select(f => new KeyValuePair<string, JsonNode?>(f.Name, f.Schema.DeepClone()))), ["required"] = new JsonArray(ports.Select(f => (JsonNode?)JsonValue.Create(f.Name)).ToArray()), ["additionalProperties"] = false };
    }
    private static PlanningSchema Contract(JsonObject schema) => new() { Contract = schema.DeepClone().AsObject() };
    private static string Key(string parent, string role) => "task_" + PlanningGraphCompiler.Fingerprint(parent + "/" + role)[..24];
    private static string Quote(string value) => JsonSerializer.Serialize(value, PlanningJsonContext.Default.String);
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
        .Concat(task.Condition is null ? [] : Values(task.Condition)).Concat(task.Items is null ? [] : Values(task.Items));
    internal static IEnumerable<TaskValue> Values(TaskValue value) => new[] { value }.Concat(value.Members.SelectMany(m => Values(m.Value))).Concat(value.Items.SelectMany(Values));
}
