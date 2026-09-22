using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>A validation receipt over a private snapshot; never persisted or supplied by a model.</summary>
public sealed class ValidatedGroundedPlan
{
    internal GroundedPlan Plan { get; }
    internal PlanningCatalog Catalog { get; }
    internal GroundedTypes Types { get; }
    internal ValidatedGroundedPlan(GroundedPlan plan, PlanningCatalog catalog, GroundedTypes types)
        => (Plan, Catalog, Types) = (plan, catalog, types);
}

public sealed record GroundedValidationResult(ValidatedGroundedPlan? Plan, IReadOnlyList<PlanningDiagnostic> Diagnostics);

public static class GroundedPlanValidator
{
    public static GroundedValidationResult Validate(GroundedPlan plan, PlanningCatalog catalog)
    {
        plan = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.GroundedPlan), PlanningJsonContext.Default.GroundedPlan)!;
        catalog = JsonSerializer.Deserialize(JsonSerializer.Serialize(catalog, PlanningJsonContext.Default.PlanningCatalog), PlanningJsonContext.Default.PlanningCatalog)!;
        var diagnostics = PlanningGraphBuilder.ValidateStructure(plan);
        if (diagnostics.Count != 0) return new(null, diagnostics);
        var types = new GroundedTypes(plan, catalog);
        diagnostics.AddRange(types.Validate());
        return new(diagnostics.Count == 0 ? new(plan, catalog, types) : null, diagnostics);
    }

    public static ValidatedGroundedPlan RequireValid(GroundedPlan plan, PlanningCatalog catalog)
    {
        var result = Validate(plan, catalog);
        return result.Plan ?? throw new InvalidOperationException(string.Join("; ", result.Diagnostics.Select(d => d.Code + " at " + d.Location + ": " + d.Message)));
    }
}

/// <summary>Contract resolution on bound business scopes, before any executable graph exists.</summary>
internal sealed class GroundedTypes
{
    private readonly PlanningCatalog _catalog;
    private readonly Dictionary<string, Scope> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _structuredStrict = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
    internal static JsonObject Opaque() => new() { ["x-gnougo-opaque"] = true };
    internal static bool IsOpaque(JsonObject value) => value["x-gnougo-opaque"]?.ToString() == "true";
    internal static PlanningSchema Schema(JsonObject value) => new() { Contract = value.DeepClone().AsObject() };

    private sealed record Scope(string Key, List<GroundedInput> Inputs, List<GroundedOperation> Operations,
        List<GroundedOutput> Outputs, Scope? Parent, string? Iteration = null, GroundedValue? Items = null)
    {
        internal Dictionary<string, JsonObject> InputContracts { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, GroundedOperation> Direct { get; } = Flatten(Operations).ToDictionary(o => o.Id, StringComparer.Ordinal);
        internal HashSet<string> Finalizers { get; } = Operations.OfType<CleanupGroundedOperation>().SelectMany(c => Flatten(c.Operations)).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
    }

    internal GroundedTypes(GroundedPlan plan, PlanningCatalog catalog)
    {
        _catalog = catalog;
        Add(new("main", plan.Inputs, plan.Operations, plan.Outputs, null));
        foreach (var flow in plan.Subflows) Add(new(flow.Name, flow.Inputs, flow.Operations, flow.Outputs, null));
    }
    private static IEnumerable<GroundedOperation> Flatten(IEnumerable<GroundedOperation> operations)
        => operations.SelectMany(o => o is CleanupGroundedOperation c ? Flatten(c.Operations) : [o]);
    private void Add(Scope scope)
    {
        _scopes.Add(scope.Key, scope);
        foreach (var op in scope.Direct.Values)
        {
            void Block(GroundedBlock block, string[] identity, string? iteration = null, GroundedValue? items = null)
                => Add(new(PlanningGraphBuilder.BlockKey(scope.Key, identity), [], block.Operations, [new("value", block.Result)], scope, iteration, items));
            switch (op)
            {
                case EachGroundedOperation e: Block(e.Body, ["body", e.Id], e.Id, e.Items); break;
                case ChooseGroundedOperation c: Block(c.Then, ["then", c.Id]); Block(c.Otherwise, ["otherwise", c.Id]); break;
                case ParallelGroundedOperation p: foreach (var b in p.Branches) Block(b.Body, ["branch", p.Id, b.Name]); break;
            }
        }
    }
    internal JsonObject Input(string owner, string name) => Input(_scopes[owner], name);
    internal JsonObject Result(string owner, string id) => Result(_scopes[owner], id);
    internal bool StructuredStrict(string owner, string id) => _structuredStrict[owner + ":" + id];
    internal JsonObject Value(string owner, GroundedValue value) => Value(_scopes[owner], value);
    private JsonObject Input(Scope scope, string name)
    {
        if (scope.InputContracts.TryGetValue(name, out var cached)) return cached;
        var declaration = scope.Inputs.FirstOrDefault(i => i.Name == name);
        if (declaration is null) return scope.Parent is null ? throw new InvalidOperationException("Unknown workflow input: " + name) : Input(scope.Parent, name);
        JsonObject? contract = declaration.Type is null ? null : PlanningGraphCompiler.ToJsonSchema(PlanningGraphBuilder.Schema(declaration.Type), _catalog);
        foreach (var invoke in GroundedTraversal.Operations(scope.Operations).OfType<InvokeGroundedOperation>())
        {
            var capability = Capability(invoke);
            foreach (var arg in invoke.Arguments.Where(a => a.Value.Kind == "input" && a.Value.Source == name && a.Value.Path.Count == 0))
                if (capability.InputSchema["properties"]?[arg.Name] is JsonObject required)
                {
                    if (contract is not null && !PlanningContractCompatibility.Fits(required, contract) && !PlanningContractCompatibility.Fits(contract, required))
                        throw new InvalidOperationException("Incompatible consumer contracts for input: " + name);
                    if (contract is null || PlanningContractCompatibility.Fits(required, contract)) contract = required.DeepClone().AsObject();
                }
        }
        if (contract is null && declaration.Default is not null)
        {
            contract = Value(scope, declaration.Default).DeepClone().AsObject();
            // A default supplies a type, not an authorization to restrict future inputs to that sample.
            void Widen(JsonNode? node)
            {
                if (node is JsonObject obj) { obj.Remove("enum"); foreach (var child in obj.Select(p => p.Value)) Widen(child); }
                else if (node is JsonArray array) foreach (var child in array) Widen(child);
            }
            Widen(contract);
        }
        if (contract is null || !PlanningValues.Established(contract)) throw new InvalidOperationException("Declare the business type of input: " + name);
        scope.InputContracts[name] = contract;
        return contract;
    }
    private PlanningCapability Capability(InvokeGroundedOperation operation)
    {
        var capability = _catalog.Capabilities.SingleOrDefault(c => c.Id == operation.Capability);
        if (capability is null || _catalog.Policy.DeniedCapabilityIds.Contains(capability.Id) || !_catalog.AllowedStepTypes.Contains(capability.StepType))
            throw new InvalidOperationException("Invocation requires an exact authorized capability ID: " + operation.Id);
        return capability;
    }
    private JsonObject Result(Scope scope, string id)
    {
        if (!scope.Direct.TryGetValue(id, out var operation))
            return scope.Parent is null ? throw new InvalidOperationException("Unknown result: " + id) : Result(scope.Parent, id);
        var key = scope.Key + ":" + id;
        if (_results.TryGetValue(key, out var cached)) return cached;
        if (!_resolving.Add(key)) throw new InvalidOperationException("Cyclic data dependency: " + id);
        try
        {
            JsonObject Block(string role, params string[] suffix)
            {
                var child = _scopes[PlanningGraphBuilder.BlockKey(scope.Key, new[] { role, id }.Concat(suffix).ToArray())];
                return Value(child, child.Outputs[0].Value);
            }
            JsonObject result;
            switch (operation)
            {
                case InvokeGroundedOperation invocation:
                    var contract = Capability(invocation).OutputSchema;
                    result = contract.Count == 0 ? Opaque() : contract.DeepClone().AsObject(); break;
                case CalculateGroundedOperation calculate:
                    result = Value(scope, calculate.Value);
                    break;
                case TransformGroundedOperation transform:
                    result = Declared(transform.ResultType, transform.ResultContract, id);
                    // Provider strict-format eligibility is separate from mandatory runtime instance validation.
                    // Optional fields retain their exact standard JSON Schema; do not rewrite their contracts.
                    _structuredStrict[key] = PlanningContractValidation.ValidateSchema(Object([("value", result)]), strict: true).Count == 0;
                    break;
                case ValidateGroundedOperation validate:
                    if (validate.Format is not ("json_value" or "json_text")) throw new InvalidOperationException("Unknown validation format.");
                    result = Declared(validate.ResultType, validate.ResultContract, id); break;
                case EachGroundedOperation: result = new() { ["type"] = "array", ["items"] = Block("body").DeepClone() }; break;
                case ChooseGroundedOperation:
                    var yes = Block("then"); var no = Block("otherwise");
                    result = PlanningContractCompatibility.Fits(yes, no) ? no : PlanningContractCompatibility.Fits(no, yes) ? yes
                        : new JsonObject { ["anyOf"] = new JsonArray(yes.DeepClone(), no.DeepClone()) }; break;
                case ParallelGroundedOperation parallel:
                    result = Object(parallel.Branches.Select(b => (b.Name, Block("branch", b.Name)))); break;
                case CallGroundedOperation call:
                    if (!_scopes.TryGetValue(call.Flow, out var target) || target.Parent is not null) throw new InvalidOperationException("Unknown named subflow: " + call.Flow);
                    result = Object(target.Outputs.Select(o => (o.Name, Value(target, o.Value)))); break;
                default: throw new InvalidOperationException("Unsupported grounded operation.");
            }
            _results.Add(key, result); return result;
        }
        finally { _resolving.Remove(key); }
    }
    private JsonObject Declared(BusinessType? type, GroundedContractReference? reference, string id)
    {
        if (reference is not null)
        {
            if (type is not null) throw new InvalidOperationException("Choose one output contract: a business type or an authoritative reference, not both.");
            var capability = _catalog.Capabilities.SingleOrDefault(c => c.Id == reference.Capability);
            if (capability is null || _catalog.Policy.DeniedCapabilityIds.Contains(capability.Id) || !_catalog.AllowedStepTypes.Contains(capability.StepType))
                throw new InvalidOperationException("Output contract reference requires an exact authorized capability identity.");
            var root = reference.Direction switch { "input" => capability.InputSchema, "output" => capability.OutputSchema, _ => throw new InvalidOperationException("Contract direction must be input or output.") };
            if (root.Count == 0) throw new InvalidOperationException("An opaque producer has no authoritative output contract to reference.");
            var pointer = "/" + reference.Direction + string.Concat(reference.Path.Select(p => "/properties/" + PlanningSchemaReferences.Escape(p)));
            var resolved = PlanningSchemaReferences.Resolve(new() { CapabilityId = capability.Id, SchemaPointer = pointer }, _catalog);
            PlanningGraphValidation.RequireTyped(resolved, 0);
            return resolved;
        }
        if (type is null || type.Type == "opaque") throw new InvalidOperationException("Declare the new business result type for " + id);
        var schema = PlanningGraphCompiler.ToJsonSchema(PlanningGraphBuilder.Schema(type), _catalog);
        PlanningGraphValidation.RequireTyped(schema, 0);
        return schema;
    }
    internal static JsonObject Object(IEnumerable<(string Name, JsonObject Schema)> values)
    {
        var items = values.ToArray();
        return new() { ["type"] = "object", ["properties"] = new JsonObject(items.Select(i => new KeyValuePair<string, JsonNode?>(i.Name, i.Schema.DeepClone()))),
            ["required"] = new JsonArray(items.Select(i => (JsonNode?)JsonValue.Create(i.Name)).ToArray()), ["additionalProperties"] = false };
    }
    private JsonObject Value(Scope scope, GroundedValue value)
    {
        JsonObject schema;
        switch (value.Kind)
        {
            case "input": schema = Input(scope, value.Source ?? ""); break;
            case "result": schema = Result(scope, value.Source ?? ""); break;
            case "item":
            case "index":
                var iteration = scope;
                while (iteration.Iteration != value.Source && iteration.Parent is not null) iteration = iteration.Parent;
                if (iteration.Iteration != value.Source || iteration.Parent is null) throw new InvalidOperationException("Unknown iteration binding.");
                schema = value.Kind == "index" ? new() { ["type"] = "integer" }
                    : Value(iteration.Parent, iteration.Items!)["items"]?.DeepClone().AsObject() ?? throw new InvalidOperationException("Iteration requires a declared array item contract."); break;
            case "object": return Object(value.Members.Select(m => (m.Name, Value(scope, m.Value))));
            case "array":
                if (value.Items.Count == 0) return new() { ["type"] = "array", ["items"] = Opaque(), ["maxItems"] = 0 };
                if (value.Items.All(v => v.Kind == "string")) return new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(value.Items.Select(v => v.Text ?? "").Distinct().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) } };
                var contracts = value.Items.Select(v => Value(scope, v)).ToArray();
                var item = contracts[0];
                if (contracts.Skip(1).Any(c => !PlanningContractCompatibility.Fits(c, item))) item = new() { ["anyOf"] = new JsonArray(contracts.Select(c => (JsonNode)c.DeepClone()).ToArray()) };
                return new() { ["type"] = "array", ["items"] = item.DeepClone() };
            case "compute":
                var planningValue = Convert(value);
                PlanningComputations.Validate(planningValue);
                var args = value.Members.ToDictionary(m => m.Name, m => Value(scope, m.Value), StringComparer.Ordinal);
                PlanningComputationContracts.Validate(PlanningComputations.Expression(value.Text), args);
                var inferred = ExpressionContractInference.Infer(PlanningComputations.Expression(value.Text), args);
                // Safe syntax and projections are checked above. Unproved results remain whole opaque values;
                // only an explicit runtime validation boundary can establish their fields or narrower type.
                return inferred is not null && PlanningValues.Established(inferred) ? inferred : Opaque();
            case "template": return new() { ["type"] = "string" };
            case "string": return new() { ["type"] = "string", ["enum"] = new JsonArray(value.Text ?? "") };
            case "number": return new() { ["type"] = value.Number % 1 == 0 ? "integer" : "number", ["enum"] = new JsonArray(JsonValue.Create(value.Number)) };
            case "boolean": return new() { ["type"] = "boolean", ["enum"] = new JsonArray(JsonValue.Create(value.Boolean)) };
            case "null": return new() { ["type"] = "null" };
            default: throw new InvalidOperationException("Unresolved or unsupported grounded value: " + value.Kind);
        }
        return PlanningGraphValidation.AtPath(schema, value.Path);
    }
    private static PlanningValue Convert(GroundedValue value) => new() { Kind = value.Kind, Text = value.Text, Source = value.Source,
        Number = value.Number, Boolean = value.Boolean, Path = value.Path, Members = value.Members.Select(m => new PlanningMember(m.Name, Convert(m.Value))).ToList(), Items = value.Items.Select(Convert).ToList() };

    private static GroundedValue ToGrounded(PlanningValue value) => new() { Kind = value.Kind, Text = value.Text, Source = value.Source,
        Number = value.Number, Boolean = value.Boolean, Path = value.Path, Members = value.Members.Select(m => new GroundedMember(m.Name, ToGrounded(m.Value))).ToList(), Items = value.Items.Select(ToGrounded).ToList() };

    internal List<PlanningDiagnostic> Validate()
    {
        var findings = new List<PlanningDiagnostic>();
        var visitedFlows = new HashSet<string>(StringComparer.Ordinal); var activeFlows = new HashSet<string>(StringComparer.Ordinal);
        bool VisitFlow(Scope scope)
        {
            if (activeFlows.Contains(scope.Key)) return false;
            if (!visitedFlows.Add(scope.Key)) return true;
            activeFlows.Add(scope.Key);
            foreach (var call in GroundedTraversal.Operations(scope.Operations).OfType<CallGroundedOperation>())
                if (_scopes.TryGetValue(call.Flow, out var target) && !VisitFlow(target)) return false;
            activeFlows.Remove(scope.Key); return true;
        }
        foreach (var scope in _scopes.Values.Where(s => s.Parent is null))
            if (!VisitFlow(scope)) { findings.Add(new("GROUNDED_CALL_CYCLE", "/scopes/" + scope.Key, "Named subflows cannot recursively invoke themselves.")); break; }
        foreach (var scope in _scopes.Values)
        {
            var root = "/scopes/" + scope.Key;
            void Check(string path, Action action)
            {
                try { action(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or Acornima.ParseErrorException)
                { findings.Add(new("GROUNDED_CONTRACT_INVALID", path, ex.Message, ValidationStage: "grounded")); }
            }
            foreach (var input in scope.Inputs)
                Check(root + "/inputs/" + input.Name, () =>
                {
                    var contract = Input(scope, input.Name);
                    if (input.Default is not null)
                    {
                        var literal = PlanningGraphValidation.Literal(Convert(input.Default));
                        var errors = PlanningContractValidation.ValidateInstance(literal, contract);
                        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
                    }
                });
            foreach (var operation in scope.Direct.Values)
            {
                var location = root + "/operations/" + operation.Id;
                if (operation is InvokeGroundedOperation { Fallback: { } fallback } invocation)
                    Check(location + "/fallback", () =>
                    {
                        var contract = Capability(invocation).OutputSchema;
                        if (!PlanningContractCompatibility.Fits(Value(scope, fallback), contract.Count == 0 ? Opaque() : contract))
                            throw new InvalidOperationException("The fallback must satisfy the authoritative result contract; it cannot change the producer's declared fields or nullability.");
                    });
                Check(location, () =>
                {
                    var result = Result(scope, operation.Id);
                    foreach (var output in operation.BusinessOutputs) _ = PlanningGraphValidation.AtPath(result, output.Path);
                });
                Check(location, () =>
                {
                    foreach (var dependency in operation.After)
                        if (!scope.Direct.ContainsKey(dependency) || dependency == operation.Id || !scope.Finalizers.Contains(operation.Id) && scope.Finalizers.Contains(dependency))
                            throw new InvalidOperationException("Invalid dependency: " + dependency);
                    foreach (var reference in GroundedTraversal.OwnValues(operation, includeBlockResults: false).SelectMany(GroundedTraversal.Values).Where(v => v.Kind == "result"))
                    {
                        var producerScope = scope;
                        while (!producerScope.Direct.ContainsKey(reference.Source!) && producerScope.Parent is not null) producerScope = producerScope.Parent;
                        if (!producerScope.Direct.TryGetValue(reference.Source!, out var producer)) continue;
                        if (producer.When is not null && !scope.Finalizers.Contains(operation.Id) && !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(producer.When, PlanningJsonContext.Default.GroundedValue), JsonSerializer.SerializeToNode(operation.When, PlanningJsonContext.Default.GroundedValue)))
                            throw new InvalidOperationException("A conditional producer is unavailable outside its condition: " + producer.Id);
                    }
                    foreach (var value in GroundedTraversal.OwnValues(operation, includeBlockResults: false)) _ = Value(scope, value);
                    if (operation.When is not null) Boolean(scope, operation.When);
                    switch (operation)
                    {
                        case InvokeGroundedOperation invoke:
                            var cap = Capability(invoke);
                            var arguments = Object(invoke.Arguments.Select(a => (a.Name, Value(scope, a.Value))));
                            // Fixed producer bindings cannot be authored; materialize them before checking required fields.
                            var values = new PlanningValue { Kind = "object", Members = invoke.Arguments.Select(a => new PlanningMember(a.Name, Convert(a.Value))).ToList() };
                            PlanningGraphBuilder.ApplyBindings(values, cap);
                            arguments = Object(values.Members.Select(m => (m.Name, Value(scope, ToGrounded(m.Value)))));
                            if (!PlanningContractCompatibility.Fits(arguments, cap.InputSchema))
                            {
                                var fields = cap.InputSchema["properties"] as JsonObject ?? new();
                                var invalid = arguments["properties"]!.AsObject().Where(p => fields[p.Key] is JsonObject expected && !PlanningContractCompatibility.Fits(p.Value!.AsObject(), expected)).Select(p => p.Key).ToArray();
                                var missing = (cap.InputSchema["required"] as JsonArray ?? []).Select(p => p!.ToString()).Except(arguments["properties"]!.AsObject().Select(p => p.Key)).ToArray();
                                throw new InvalidOperationException("Arguments do not satisfy the authoritative input contract for " + invoke.Id + ". Incompatible members: " + string.Join(", ", invalid) + ". Missing required members: " + string.Join(", ", missing) + ".");
                            }
                            break;
                        case ChooseGroundedOperation choose: Boolean(scope, choose.Condition); break;
                        case EachGroundedOperation each: if (Value(scope, each.Items)["type"]?.ToString() != "array") throw new InvalidOperationException("Each requires an array."); break;
                        case CallGroundedOperation call:
                            if (!_scopes.TryGetValue(call.Flow, out var target)) throw new InvalidOperationException("Unknown subflow: " + call.Flow);
                            if (call.Arguments.Any(a => !target.Inputs.Any(i => i.Name == a.Name))) throw new InvalidOperationException("An undeclared subflow argument was supplied.");
                            foreach (var input in target.Inputs)
                            {
                                var arg = call.Arguments.FirstOrDefault(a => a.Name == input.Name);
                                if (arg is null && !input.Optional && input.Default is null) throw new InvalidOperationException("Missing subflow argument: " + input.Name);
                                if (arg is not null && !PlanningContractCompatibility.Fits(Value(scope, arg.Value), Input(target, input.Name))) throw new InvalidOperationException("Invalid subflow argument: " + input.Name);
                            }
                            break;
                    }
                });
            }
            foreach (var output in scope.Outputs) Check(root + "/outputs/" + output.Name, () =>
            {
                _ = Value(scope, output.Value);
                foreach (var reference in GroundedTraversal.Values(output.Value).Where(v => v.Kind == "result"))
                {
                    var owner = scope;
                    while (!owner.Direct.ContainsKey(reference.Source!) && owner.Parent is not null) owner = owner.Parent;
                    if (owner.Direct.TryGetValue(reference.Source!, out var producer) && producer.When is not null)
                        throw new InvalidOperationException("A conditional result cannot be exported unconditionally: " + producer.Id);
                }
            });
            Check(root, () =>
            {
                var done = new HashSet<string>(StringComparer.Ordinal); var visiting = new HashSet<string>(StringComparer.Ordinal);
                void Visit(GroundedOperation op)
                {
                    if (done.Contains(op.Id)) return;
                    if (!visiting.Add(op.Id)) throw new InvalidOperationException("Cyclic operation ordering: " + op.Id);
                    foreach (var id in op.After.Concat(GroundedTraversal.Values([op]).Where(v => v.Kind == "result").Select(v => v.Source!)).Distinct())
                        if (scope.Direct.TryGetValue(id, out var prior) && id != op.Id) Visit(prior);
                    visiting.Remove(op.Id); done.Add(op.Id);
                }
                foreach (var op in scope.Direct.Values) Visit(op);
            });
        }
        return findings.Distinct().ToList();
    }
    private void Boolean(Scope scope, GroundedValue value)
    { if (Value(scope, value)["type"]?.ToString() != "boolean") throw new InvalidOperationException("A condition must have a boolean contract."); }
}
