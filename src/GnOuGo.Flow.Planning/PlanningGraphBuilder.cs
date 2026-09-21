using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning;

/// <summary>Business operations become executable nodes here. No transport or result-envelope choices come from intent.</summary>
public static class PlanningGraphBuilder
{
    internal const string GuardedCondition = "available && condition";
    public static PlanningGraph Build(ValidatedGroundedPlan validated)
    {
        var intent = validated.Plan; var catalog = validated.Catalog; var types = validated.Types;
        var graph = new PlanningGraph { Summary = intent.Summary };
        AddFlow("main", intent.Inputs, intent.Operations, intent.Outputs);
        foreach (var flow in intent.Subflows) { CheckKey(flow.Name); AddFlow(flow.Name, flow.Inputs, flow.Operations, flow.Outputs); }
        foreach (var workflow in graph.Workflows)
        {
            if (workflow.Steps.Count == 0) workflow.Steps.Add(new() { Key = Generated("empty", workflow.Key), Type = "set" });
            Normalize(workflow.Steps); Normalize(workflow.Finally);
            foreach (var node in PlanningGraphCompiler.Enumerate(workflow.Finally))
            {
                var main = PlanningGraphCompiler.Enumerate(workflow.Steps).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
                // Dependencies order execution; only consumed values require completed main results.
                var required = References(node).Where(v => v.Kind == "output").Select(v => v.Source!).Where(main.Contains).Distinct().ToArray();
                if (required.Length > 0)
                {
                    var guard = AvailabilityGuard(required);
                    node.If = node.If is null ? guard : Compute(GuardedCondition, new("available", guard), new("condition", node.If));
                }
            }
        }
        return graph;

        void AddFlow(string key, List<GroundedInput> inputs, List<GroundedOperation> operations, List<GroundedOutput> outputs)
        {
            var workflow = new PlanningWorkflow { Key = key }; graph.Workflows.Add(workflow);
            var scope = new BuilderScope(graph, catalog, workflow, operations, types);
            foreach (var input in inputs)
            {
                var schema = types.Input(key, input.Name);
                var port = new PlanningPort { Name = input.Name, Required = !input.Optional, Schema = GroundedTypes.Schema(schema), Default = input.Default is null ? null : scope.Value(input.Default) };
                if (port.Default is null && schema.TryGetPropertyValue("default", out var value)) port.Default = PlanningJsonTransport.Literal(value);
                workflow.Inputs.Add(port);
            }
            scope.Build(operations, workflow.Steps);
            foreach (var output in outputs)
            {
                var port = new PlanningOutput { Name = output.Name, Value = scope.Value(output.Value), Schema = GroundedTypes.Schema(types.Value(key, output.Value)) };
                workflow.Outputs.Add(port);
            }
        }
        void Normalize(List<PlanningNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (catalog.Capabilities.FirstOrDefault(c => c.Id == node.CapabilityId) is { } capability)
                {
                    var arguments = node.Type == "mcp.call" ? PlanningGraphValidation.Member(node.Input, "request")! : node.Input;
                    ApplyBindings(arguments, capability);
                }
                Normalize(node.Steps); Normalize(node.Default);
                foreach (var branch in node.Branches) Normalize(branch.Steps);
                foreach (var branch in node.Cases) Normalize(branch.Steps);
            }
            var pending = nodes.ToList(); var ordered = new List<PlanningNode>(); var local = pending.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var ready = pending.FirstOrDefault(n => n.Dependencies.Concat(References(n).Where(v => v.Kind == "output").Select(v => v.Source!)).Where(local.Contains).All(d => ordered.Any(p => p.Key == d)));
                if (ready is null) return; // Located cycle diagnostics remain the validator's responsibility.
                pending.Remove(ready); ordered.Add(ready);
            }
            nodes.Clear(); nodes.AddRange(ordered);
        }
    }

    internal static List<PlanningDiagnostic> ValidateStructure(GroundedPlan intent)
    {
        var diagnostics = new List<PlanningDiagnostic>();
        foreach (var group in GroundedTraversal.Located(intent).GroupBy(o => GroundedTraversal.GraphOwner(intent, o.Path)))
            foreach (var duplicate in group.GroupBy(o => o.Operation.Id, StringComparer.Ordinal))
                if (string.IsNullOrWhiteSpace(duplicate.Key) || duplicate.Key.StartsWith("__planning_", StringComparison.Ordinal) || duplicate.Count() > 1)
                    foreach (var item in duplicate) diagnostics.Add(new("INTENT_IDENTIFIER_INVALID", item.Path, "Operation identifiers must be unique in their scope, nonempty and outside the reserved __planning_ namespace.", ValidationStage: "intent"));
        foreach (var flow in intent.Subflows.Select((f, i) => (Flow: f, Index: i)))
            if (flow.Flow.Name == "main" || string.IsNullOrWhiteSpace(flow.Flow.Name) || flow.Flow.Name.StartsWith("__planning_", StringComparison.Ordinal) || intent.Subflows.Count(f => f.Name == flow.Flow.Name) > 1)
                diagnostics.Add(new("INTENT_IDENTIFIER_INVALID", "/subflows/" + flow.Index, "Subflow names must be distinct, nonempty and outside main and the reserved namespace.", ValidationStage: "intent"));
        return diagnostics;
    }
    public static PlanningSchema Schema(BusinessType type) => new()
    {
        Type = type.Type, Nullable = type.Nullable, Enum = [.. type.Enum], Items = type.Items is null ? null : Schema(type.Items),
        Properties = type.Fields.Select(f => new PlanningPort { Name = f.Name, Required = !f.Optional, Schema = Schema(f.Type) }).ToList()
    };
    private static PlanningValue Object(params PlanningMember[] fields) => new() { Kind = "object", Members = fields.ToList() };
    private static PlanningValue Text(string text) => new() { Kind = "string", Text = text };
    private static PlanningValue Compute(string text, params PlanningMember[] args) => new() { Kind = "compute", Text = text, Members = args.ToList() };
    private static PlanningSchema Wrapped(PlanningSchema schema) => new() { Type = "object", Properties = [new() { Name = "value", Schema = schema }] };
    private static string Generated(string role, string id) => "__planning_" + role + "_" + id;
    internal static string BlockKey(string owner, params string[] parts) => "__planning_flow_" + string.Join("_", parts.Prepend(owner).Select(p => p.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + p));

    private sealed class BuilderScope(PlanningGraph graph, PlanningCatalog catalog, PlanningWorkflow workflow, List<GroundedOperation> operations, GroundedTypes types)
    {
        private readonly Dictionary<string, PlanningValue> _captures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GroundedOperation> _operations = Direct(operations).ToDictionary(o => o.Id, StringComparer.Ordinal);
        private static IEnumerable<GroundedOperation> Direct(IEnumerable<GroundedOperation> items) => items.SelectMany(o => o is CleanupGroundedOperation cleanup ? Direct(cleanup.Operations) : [o]);
        public PlanningValue Value(GroundedValue value)
        {
            if (_captures.TryGetValue(value.Kind + ":" + value.Source, out var capture))
                return new() { Kind = capture.Kind, Source = capture.Source, ResultChannel = capture.ResultChannel, Path = capture.Path.Concat(value.Path).ToList() };
            if (value.Kind == "result")
            {
                _operations.TryGetValue(value.Source ?? "", out var operation);
                return new() { Kind = "output", Source = value.Source,
                    ResultChannel = operation is TransformGroundedOperation ? "structured" : null,
                    Path = (operation is CalculateGroundedOperation or TransformGroundedOperation or ChooseGroundedOperation or EachGroundedOperation or ParallelGroundedOperation or ValidateGroundedOperation ? new[] { "value" } : []).Concat(value.Path).ToList() };
            }
            return new() { Kind = value.Kind switch { "item" => "loop_item", "index" => "loop_index", _ => value.Kind },
                Text = value.Text, Number = value.Number, Boolean = value.Boolean, Source = value.Source, Path = [.. value.Path],
                Members = value.Members.Select(m => new PlanningMember(m.Name, Value(m.Value))).ToList(), Items = value.Items.Select(Value).ToList() };
        }
        public void Build(List<GroundedOperation> items, List<PlanningNode> destination)
        {
            foreach (var operation in items)
            {
                CheckKey(operation.Id);
                if (operation is CleanupGroundedOperation cleanup)
                {
                    var start = workflow.Finally.Count; Build(cleanup.Operations, workflow.Finally);
                    foreach (var child in workflow.Finally.Skip(start))
                    {
                        child.Dependencies = child.Dependencies.Concat(cleanup.After).Distinct(StringComparer.Ordinal).ToList();
                        if (cleanup.When is not null) child.If = child.If is null ? Value(cleanup.When) : Compute("group && condition", new("group", Value(cleanup.When)), new("condition", child.If));
                    }
                    continue;
                }
                var node = new PlanningNode { Key = operation.Id, Purpose = operation.Purpose, Dependencies = [.. operation.After], If = operation.When is null ? null : Value(operation.When) };
                switch (operation)
                {
                    case InvokeGroundedOperation invoke:
                        var capability = catalog.Capabilities.FirstOrDefault(c => c.Id == invoke.Capability);
                        node.CapabilityId = invoke.Capability; node.Type = capability!.StepType;
                        var arguments = Object(invoke.Arguments.Select(a => new PlanningMember(a.Name, Value(a.Value))).ToArray());
                        node.Input = node.Type == "mcp.call" ? Object(new PlanningMember("request", arguments)) : arguments;
                        if (invoke.Fallback is not null) node.OnError.Add(new(null, "continue", node.Type == "mcp.call" ? Object(new PlanningMember("response", Value(invoke.Fallback))) : Value(invoke.Fallback), null));
                        break;
                    case CalculateGroundedOperation calculation:
                        node.Type = "set"; node.Input = Object(new PlanningMember("value", Value(calculation.Value)));
                        node.OutputSchema = Wrapped(GroundedTypes.Schema(types.Result(workflow.Key, operation.Id)));
                        break;
                    case TransformGroundedOperation transform:
                        node.Type = "llm.call";
                        node.Input = Object(new PlanningMember("prompt", Compute("instruction + '\\nBusiness data: ' + JSON.stringify(values)", new("instruction", Text(transform.Instruction)), new("values", Object(transform.Data.Select(d => new PlanningMember(d.Name, Value(d.Value))).ToArray())))));
                        node.StructuredOutput = new(Wrapped(GroundedTypes.Schema(types.Result(workflow.Key, operation.Id))));
                        break;
                    case ValidateGroundedOperation validate:
                        node.Type = "value.validate";
                        node.Input = Object(new("value", Value(validate.Value)), new("format", Text(validate.Format)));
                        node.OutputSchema = Wrapped(GroundedTypes.Schema(types.Result(workflow.Key, operation.Id)));
                        break;
                    case CallGroundedOperation call:
                        node.Type = "workflow.call"; node.Input = Object(new PlanningMember("ref", new() { Kind = "workflow", Source = call.Flow }), new("args", Object(call.Arguments.Select(a => new PlanningMember(a.Name, Value(a.Value))).ToArray())));
                        break;
                    case EachGroundedOperation each:
                        var loopKey = Generated("each", operation.Id);
                        var body = Block(each.Body, ["body", operation.Id], operation.Id, each.Items);
                        var loop = new PlanningNode { Key = loopKey, Type = each.Parallel ? "loop.parallel" : "loop.sequential", If = node.If, Dependencies = node.Dependencies,
                            Input = Object(new PlanningMember("items", Value(each.Items))), Steps = [body.Call] };
                        destination.Add(loop);
                        node.Type = "set"; node.Dependencies = [loopKey]; node.OutputSchema = Wrapped(GroundedTypes.Schema(types.Result(workflow.Key, operation.Id)));
                        node.Input = Object(new PlanningMember("value", Compute("items.map(item => item[" + Quote(body.Call.Key) + "].outputs.value)", new PlanningMember("items", new() { Kind = "output", Source = loopKey, Path = ["results"] }))));
                        break;
                    case ChooseGroundedOperation choice:
                        var switchKey = Generated("choose", operation.Id);
                        var yes = Block(choice.Then, ["then", operation.Id]); var no = Block(choice.Otherwise, ["otherwise", operation.Id]);
                        destination.Add(new() { Key = switchKey, Type = "switch", If = node.If, Dependencies = node.Dependencies, Expr = Value(choice.Condition), Cases = [new("true", null, [yes.Call])], Default = [no.Call] });
                        node.Type = "set"; node.Dependencies = [switchKey]; node.OutputSchema = Wrapped(GroundedTypes.Schema(types.Result(workflow.Key, operation.Id)));
                        node.Input = Object(new PlanningMember("value", Compute("branch[" + Quote(yes.Call.Key) + "] != null ? branch[" + Quote(yes.Call.Key) + "].outputs.value : branch[" + Quote(no.Call.Key) + "].outputs.value", new PlanningMember("branch", new() { Kind = "output", Source = switchKey }))));
                        break;
                    case ParallelGroundedOperation parallel:
                        var parallelKey = Generated("parallel", operation.Id);
                        var branches = parallel.Branches.Select(b => (b.Name, Block: Block(b.Body, ["branch", operation.Id, b.Name]))).ToArray();
                        destination.Add(new() { Key = parallelKey, Type = "parallel", If = node.If, Dependencies = node.Dependencies, Branches = branches.Select(b => new PlanningBranch([b.Block.Call])).ToList() });
                        node.Type = "set"; node.Dependencies = [parallelKey]; node.OutputSchema = Wrapped(GroundedTypes.Schema(types.Result(workflow.Key, operation.Id)));
                        node.Input = Object(new PlanningMember("value", Object(branches.Select((b, i) => new PlanningMember(b.Name, new() { Kind = "output", Source = parallelKey, Path = ["branches", i.ToString(System.Globalization.CultureInfo.InvariantCulture), b.Block.Call.Key, "outputs", "value"] })).ToArray())));
                        break;
                    default: throw new InvalidOperationException("Unsupported business operation.");
                }
                destination.Add(node);
            }
        }
        private (PlanningWorkflow Workflow, PlanningNode Call) Block(GroundedBlock block, string[] identity, string? iteration = null, GroundedValue? items = null)
        {
            var key = BlockKey(workflow.Key, identity);
            var nested = new PlanningWorkflow { Key = key }; graph.Workflows.Add(nested);
            var scope = new BuilderScope(graph, catalog, nested, block.Operations, types);
            var arguments = new List<PlanningMember>();
            var local = GroundedTraversal.Operations(block.Operations).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
            var references = GroundedTraversal.Values(block.Operations).Concat(GroundedTraversal.Values(block.Result)).Where(v => v.Kind is "input" or "result" or "item" or "index")
                .Where(v => v.Kind == "input" || !local.Contains(v.Source ?? "")).DistinctBy(v => v.Kind + ":" + v.Source).ToArray();
            foreach (var reference in references)
            {
                var name = "capture_" + arguments.Count;
                var port = new PlanningPort { Name = name, Schema = GroundedTypes.Schema(types.Value(key, new() { Kind = reference.Kind, Source = reference.Source })) }; nested.Inputs.Add(port);
                PlanningValue value;
                if (reference.Source == iteration && reference.Kind is "item" or "index")
                {
                    value = new() { Kind = reference.Kind == "item" ? "loop_item" : "loop_index", Source = Generated("each", iteration!) };
                }
                else
                {
                    value = Value(new() { Kind = reference.Kind, Source = reference.Source });
                }
                arguments.Add(new(name, value)); scope._captures[reference.Kind + ":" + reference.Source] = new() { Kind = "input", Source = name };
            }
            scope.Build(block.Operations, nested.Steps);
            var result = new PlanningOutput { Name = "value", Value = scope.Value(block.Result), Schema = GroundedTypes.Schema(types.Value(key, block.Result)) }; nested.Outputs.Add(result);
            return (nested, new() { Key = key, Type = "workflow.call", Input = Object(new PlanningMember("ref", new() { Kind = "workflow", Source = key }), new("args", Object(arguments.ToArray()))) });
        }
    }
    private static string Quote(string text) => JsonSerializer.Serialize(text, PlanningJsonContext.Default.String);
    internal static void ApplyBindings(PlanningValue arguments, PlanningCapability capability)
    {
        foreach (var binding in capability.RequestBindings)
        {
            var parts = binding.Path.Split('/').Skip(1).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
            if (parts.Length == 0) throw new InvalidOperationException("Catalog argument bindings require a property path.");
            var parent = arguments;
            for (var i = 0; i < parts.Length; i++)
            {
                if (parent.Kind != "object") throw new InvalidOperationException("Catalog argument bindings require object arguments.");
                var index = parent.Members.FindIndex(m => m.Name == parts[i]);
                if (i == parts.Length - 1)
                {
                    var member = new PlanningMember(parts[i], PlanningJsonTransport.Literal(binding.Value));
                    if (index < 0) parent.Members.Add(member); else parent.Members[index] = member;
                }
                else
                {
                    if (index < 0) { parent.Members.Add(new(parts[i], new() { Kind = "object" })); index = parent.Members.Count - 1; }
                    parent = parent.Members[index].Value;
                }
            }
        }
    }

    internal static IEnumerable<PlanningValue> References(PlanningNode node) => PlanningDataflow.References(node.Input)
        .Concat(node.If is null ? [] : PlanningDataflow.References(node.If)).Concat(node.Expr is null ? [] : PlanningDataflow.References(node.Expr))
        .Concat(node.Cases.Where(c => c.When is not null).SelectMany(c => PlanningDataflow.References(c.When!)))
        .Concat(node.OnError.SelectMany(e => (e.If is null ? Enumerable.Empty<PlanningValue>() : PlanningDataflow.References(e.If)).Concat(e.SetOutput is null ? [] : PlanningDataflow.References(e.SetOutput))));
    private static void CheckKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("__planning_", StringComparison.Ordinal))
            throw new InvalidOperationException("Graph identifiers must be nonempty and cannot use the reserved __planning_ prefix.");
    }
    private static PlanningValue AvailabilityGuard(IEnumerable<string> sources) => new()
    {
        Kind = "expression", Text = string.Join(" && ", sources.Order(StringComparer.Ordinal).Select(s => "data.steps[" + JsonSerializer.Serialize(s, PlanningJsonContext.Default.String) + "] != null"))
    };
    internal static bool GuardsFinalizerSource(PlanningNode node, string source)
    {
        var guard = node.If?.Kind == "compute" ? node.If.Members.FirstOrDefault(m => m.Name == "available")?.Value : node.If;
        return guard?.Kind == "expression" && guard.Text?.Split(" && ", StringSplitOptions.None)
            .Contains(AvailabilityGuard([source]).Text, StringComparer.Ordinal) == true;
    }
}
