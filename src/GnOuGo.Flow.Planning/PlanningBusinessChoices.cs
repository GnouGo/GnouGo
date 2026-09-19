using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Expressions;
namespace GnOuGo.Flow.Planning;

/// <summary>Only business assignments are reflected into intent; generated technical fields remain reproducible.</summary>
internal static class PlanningBusinessChoices
{
    internal static void Apply(PlanningSession state, IReadOnlyList<(PlanningHole Hole, JsonNode? Value)> assignments)
    {
        // Work on a private candidate: reflection and rebuilding must both succeed before publication.
        var candidate = new PlanningSession { Catalog = state.Catalog, Graph = state.Graph,
            IntentPlan = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.IntentPlan, PlanningJsonContext.Default.WorkflowIntentPlan), PlanningJsonContext.Default.WorkflowIntentPlan) };
        try
        {
            foreach (var (hole, value) in assignments)
            {
                candidate.Graph = PlanningHoleEligibility.Assign(candidate.Graph!, candidate.Catalog!, hole, value);
                Reflect(candidate, hole);
            }
            if (assignments.Any(a => a.Hole.Kind != "schema")) candidate.Graph = PlanningGraphBuilder.Build(candidate.IntentPlan!, candidate.Catalog!);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            throw new WorkflowRuntimeException("PLANNING_HOST_CONTRACT", "The engine could not reflect an issued choice into business intent; no assignments were applied.", inner: ex);
        }
        state.IntentPlan = candidate.IntentPlan; state.Graph = candidate.Graph;
    }
    private static void Reflect(PlanningSession state, PlanningHole hole)
    {
        if (hole.Kind == "schema") return;
        var workflow = state.Graph!.Workflows.Single(w => w.Key == hole.WorkflowKey);
        var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).FirstOrDefault(n => n.Key == hole.NodeKey);
        var located = IntentTraversal.Located(state.IntentPlan!).FirstOrDefault(o => o.Operation.Id == hole.NodeKey && IntentTraversal.GraphOwner(state.IntentPlan!, o.Path) == workflow.Key);
        if (located.Operation is InvokeIntentOperation invoke && node is not null)
        {
            invoke.Capability = node.CapabilityId;
            var capability = state.Catalog!.Capabilities.Single(c => c.Id == node.CapabilityId);
            var request = node.Type == "mcp.call" ? PlanningGraphValidation.Member(node.Input, "request")! : node.Input;
            invoke.Arguments = request.Members.Where(m => m.Value.Kind != PlanningValues.Omitted && !capability.RequestBindings.Any(b => b.Path == "/" + PlanningFieldPaths.Escape(m.Name)))
                .Select(m => new IntentMember(m.Name, Value(state, workflow, m.Value))).ToList();
            return;
        }
        var previous = state.Diagnostics; state.Diagnostics = [new("HOLE_UNRESOLVED", hole.Path, "Resolved business choice.")];
        var path = PlanningDiagnosticLocations.ForIntent(state).Single().Location; state.Diagnostics = previous;
        var json = PlanningJsonTransport.Intent(state.IntentPlan!);
        if (PlanningFieldPaths.ReadOptional(json, path) is not System.Text.Json.Nodes.JsonObject existing || !existing.ContainsKey("kind"))
            throw new InvalidOperationException("A resolved business choice has no editable intent value.");
        var assigned = JsonSerializer.Deserialize(PlanningFieldPaths.Read(PlanningFieldPaths.Json(state.Graph), hole.Path), PlanningJsonContext.Default.PlanningValue)!;
        PlanningFieldPaths.Replace(json, path, JsonSerializer.SerializeToNode(Value(state, workflow, assigned), PlanningJsonContext.Default.IntentValue));
        state.IntentPlan = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowIntentPlan)!;
    }
    internal static IntentValue Value(PlanningSession state, PlanningWorkflow workflow, PlanningValue value)
    {
        if (value.Kind == "input" && value.Source?.StartsWith("capture_", StringComparison.Ordinal) == true)
        {
            foreach (var parent in state.Graph!.Workflows)
                foreach (var call in PlanningGraphCompiler.Enumerate(parent.Steps.Concat(parent.Finally)).Where(n => n.Type == "workflow.call" && PlanningGraphValidation.Member(n.Input, "ref")?.Source == workflow.Key))
                    if (PlanningGraphValidation.Member(PlanningGraphValidation.Member(call.Input, "args")!, value.Source) is { } captured)
                    {
                        var business = Value(state, parent, captured); business.Path.AddRange(value.Path); return business;
                    }
            throw new InvalidOperationException("An internal capture has no business producer.");
        }
        var path = value.Path.ToList();
        if (value.Kind == "output")
        {
            var operation = IntentTraversal.Located(state.IntentPlan!).FirstOrDefault(o => o.Operation.Id == value.Source && IntentTraversal.GraphOwner(state.IntentPlan!, o.Path) == workflow.Key).Operation
                ?? throw new InvalidOperationException("Generated result envelopes are not business choices.");
            if (operation is CalculateIntentOperation or TransformIntentOperation or ChooseIntentOperation or EachIntentOperation or ParallelIntentOperation)
            {
                if (path.FirstOrDefault() != "value") throw new InvalidOperationException("A generated result envelope is not a business value.");
                path.RemoveAt(0);
            }
        }
        if (value.Kind is "expression" or "workflow" or "artifact_collection") throw new InvalidOperationException("Technical bindings are not editable business values.");
        return new() { Kind = value.Kind switch { "output" => "result", "loop_item" => "item", "loop_index" => "index", PlanningValues.Hole => "missing", _ => value.Kind },
            Source = value.Kind is "loop_item" or "loop_index" && value.Source?.StartsWith("__planning_each_", StringComparison.Ordinal) == true ? value.Source["__planning_each_".Length..] : value.Source,
            Path = path, Text = value.Text, Number = value.Number, Boolean = value.Boolean, Members = value.Members.Select(m => new IntentMember(m.Name, Value(state, workflow, m.Value))).ToList(), Items = value.Items.Select(v => Value(state, workflow, v)).ToList() };
    }
}
