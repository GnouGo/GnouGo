using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

/// <summary>Only uniquely evidenced bindings are selected; ambiguity remains an explicit hole.</summary>
internal static class PlanningBindingResolution
{
    internal static void Resolve(PlanningSnapshot state, PlanningWorkflow workflow)
    {
        bool changed;
        do
        {
            changed = PlanningSchemaPropagation.Resolve(state, workflow);
            workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key);
            foreach (var hole in state.Construction.Holes.Where(h => h.WorkflowKey == workflow.Key && !h.Resolved && h.Kind == "value").ToArray())
            {
                var currentNode = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SingleOrDefault(n => n.Key == hole.NodeKey);
                if (currentNode?.Type == "switch" && PlanningDecisionRouting.Contract(currentNode, state.Preparation!) is not null)
                {
                    try
                    {
                        var decision = PlanningDecisionRouting.Resolve(workflow, currentNode, state.Preparation!, state.Graph!);
                        Assign(state, hole, JsonSerializer.SerializeToNode(decision, PlanningJsonContext.Default.PlanningValue));
                        workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key); changed = true; continue;
                    }
                    catch (InvalidOperationException) { continue; } // Locked routing is never delegated to a model.
                }
                var expected = PlanningHoleRequests.Expected(state, workflow, hole);
                if (expected is null) continue;
                if (currentNode is not null && PlanningBaselineValues.Literal(state, workflow, currentNode, hole) is { } literal)
                {
                    Assign(state, hole, JsonSerializer.SerializeToNode(literal, PlanningJsonContext.Default.PlanningValue));
                    workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key); changed = true; continue;
                }
                if (Unique(state, workflow, hole) is { } selected)
                {
                    Assign(state, hole, JsonSerializer.SerializeToNode(selected.Value, PlanningJsonContext.Default.PlanningValue));
                    workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key); changed = true; continue;
                }
                var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SingleOrDefault(n => n.Key == hole.NodeKey);
                if (node?.Type != "set" || !hole.Path.EndsWith("/input", StringComparison.Ordinal) || expected["properties"] is not JsonObject fields) continue;
                var value = new PlanningValue { Kind = "object" };
                foreach (var (name, _) in fields) value.Members.Add(new(name, new() { Kind = PlanningGraphSkeleton.Unresolved }));
                Assign(state, hole, JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.PlanningValue));
                workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key);
                node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Single(n => n.Key == hole.NodeKey);
                foreach (var (name, schema) in fields)
                    PlanningGraphSkeleton.Add(state, workflow, node, hole.Path + "/members/" + value.Members.FindIndex(m => m.Name == name) + "/value", "value", hole.Purpose + "; result field " + name, schema as JsonObject);
                changed = true;
            }
        } while (changed);
    }
    internal static PlanningBinding? Unique(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        if (PlanningHoleRequests.Expected(state, workflow, hole) is not { } expected) return null;
        var candidates = PlanningHoleRequests.Catalog(state, workflow, hole)
            .Where(b => b.Availability != "conditional" && PlanningGraphValidation.TypesFit(b.Schema, expected) && Evidence(state, workflow, hole, b)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal static bool RepairStaged(PlanningSnapshot state, PlanningStagedAssignments staged, out PlanningGraph? graph)
    {
        graph = null;
        var scope = PlanningExactPatches.Scope(staged.Payload, staged.ResponseSchema, staged.Diagnostics);
        var patches = new JsonArray();
        foreach (var hole in staged.Targets.Where(h => h.Kind == "value"))
        {
            var root = "/assignments/" + hole.Id;
            var expression = scope.SingleOrDefault(t => t.Path == root + "/expression");
            if (expression is null || Unique(state, state.Graph!.Workflows.Single(w => w.Key == hole.WorkflowKey), hole) is not { } binding) continue;
            var parameter = staged.Bindings.FirstOrDefault(p => PlanningBindingIdentity.Id(p.Value) == binding.Id).Key;
            if (parameter is null) continue;
            var parameters = scope.SingleOrDefault(t => t.Path == root + "/bindings");
            if (!staged.Payload["assignments"]![hole.Id]!["bindings"]!.AsArray().Select(p => p!.ToString()).SequenceEqual([parameter], StringComparer.Ordinal))
            {
                if (parameters is null) continue;
                patches.Add((JsonNode)new JsonObject { ["target"] = parameters.Id, ["value"] = new JsonArray(parameter) });
            }
            patches.Add((JsonNode)new JsonObject { ["target"] = expression.Id, ["value"] = parameter });
        }
        if (patches.Count == 0) return false;
        var previous = staged.Payload;
        staged.Payload = PlanningExactPatches.Apply(previous, new JsonObject { ["patches"] = patches }, scope, PlanningExactPatches.Schema(scope, staged.ResponseSchema));
        var evaluated = PlanningHoleAssignments.Evaluate(state, staged);
        if (evaluated.Diagnostics.Count == 0) { graph = evaluated.Graph; staged.Diagnostics.Clear(); return true; }
        staged.Payload = previous; return false;
    }

    private static bool Evidence(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole, PlanningBinding binding)
    {
        if (hole.NodeKey is null)
            return binding.Value.Kind == "output";
        var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Single(n => n.Key == hole.NodeKey);
        var requiredInputs = state.Construction.Dataflow?.InputObligations.GetValueOrDefault(workflow.Key + "/" + node.Key) ?? [];
        var requiredOperations = PlanningOperationCompositions.RequiredInputs(workflow, node, state.Preparation!);
        if (node.Type is "loop.sequential" or "loop.parallel")
        {
            // Operations performed by the fixed body establish the loop result, not
            // the source collection evaluated before entering that body.
            var body = PlanningGraphCompiler.Enumerate(node.Steps).ToArray();
            var contained = body.SelectMany(n => n.OperationIds).Concat(body.Select(PlanningWorkflowProvenance.Target).OfType<string>()
                .SelectMany(key => state.Graph!.Workflows.Single(w => w.Key == key).OperationIds)).ToHashSet(StringComparer.Ordinal);
            requiredOperations = requiredOperations.Where(op => !contained.Contains(op)).ToArray();
        }
        if (requiredInputs.Count == 0 && requiredOperations.Count == 0) return false;
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        var operations = PlanningDataflow.OperationDependencies(workflow, node, state.Preparation!, state.Graph!, inputs, binding.Value).Operations;
        return (requiredInputs.Count == 0 || requiredInputs.Any(inputs.Contains)) && (requiredOperations.Count == 0 || requiredOperations.Any(operations.Contains));
    }
    internal static void Assign(PlanningSnapshot state, PlanningHole hole, JsonNode? value)
    {
        var json = PlanningFieldPaths.Json(state.Graph!); PlanningFieldPaths.Replace(json, hole.Path, value);
        state.Graph = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningGraph)!; hole.Resolved = true;
    }
}
