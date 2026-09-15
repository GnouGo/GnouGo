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
            changed = PlanningContractPropagation.Resolve(state);
            workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key);
            changed |= PlanningSchemaPropagation.Resolve(state, workflow);
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
                var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
                if (ForcedLiteral(hole, domain, out var fixedValue))
                {
                    Assign(state, hole, JsonSerializer.SerializeToNode(PlanningSkeletonInputs.Literal(fixedValue), PlanningJsonContext.Default.PlanningValue));
                    workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key); changed = true; continue;
                }
                hole.DirectCandidateCount = domain.Direct.Count; hole.ComputationParameterCount = domain.Parameters.Count;
                if (Unique(domain) is { } selected)
                {
                    Assign(state, hole, JsonSerializer.SerializeToNode(selected.Value, PlanningJsonContext.Default.PlanningValue));
                    workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key); changed = true; continue;
                }
                var node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).SingleOrDefault(n => n.Key == hole.NodeKey);
                if (node is null || domain.Omission || expected["type"]?.ToString() != "object" || expected["properties"] is not JsonObject properties || PlanningHoleEligibility.ArtifactKinds(state, workflow, hole).Any()) continue;
                var required = (expected["required"] as JsonArray ?? []).Select(n => n!.ToString()).ToHashSet(StringComparer.Ordinal);
                var fields = properties.Where(p => required.Contains(p.Key)).ToArray();
                if (fields.Length == 0) continue;
                var value = new PlanningValue { Kind = "object" };
                foreach (var (name, _) in fields) value.Members.Add(new(name, new() { Kind = PlanningGraphSkeleton.Unresolved }));
                Assign(state, hole, JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.PlanningValue));
                hole.Superseded = true;
                workflow = state.Graph!.Workflows.Single(w => w.Key == workflow.Key);
                node = PlanningGraphCompiler.Enumerate(workflow.Steps.Concat(workflow.Finally)).Single(n => n.Key == hole.NodeKey);
                foreach (var (name, schema) in fields)
                    PlanningGraphSkeleton.Add(state, workflow, node, hole.Path + "/members/" + value.Members.FindIndex(m => m.Name == name) + "/value", "value", hole.Purpose + "; result field " + name, schema as JsonObject);
                changed = true;
            }
        } while (changed);
        PlanningConvergence.Refresh(state);
    }
    internal static bool Fixed(JsonObject contract, out JsonNode? value)
    {
        if (contract.TryGetPropertyValue("const", out value) || contract["enum"] is JsonArray { Count: 1 } values && Set(values[0], out value) || contract.TryGetPropertyValue("default", out value))
            return PlanningContractValidation.ValidateInstance(value, contract).Count == 0;
        value = null; return false;
        static bool Set(JsonNode? item, out JsonNode? selected) { selected = item; return true; }
    }
    internal static bool ForcedLiteral(PlanningHole hole, PlanningHoleDomain domain, out JsonNode? value)
    {
        value = null;
        if (!domain.Literal || domain.Contract is not { } contract) return false;
        var fixedDomain = contract.ContainsKey("const") || contract["enum"] is JsonArray { Count: 1 };
        // A default is a fallback, not proof that a dynamic optional argument is
        // irrelevant. Choosing a default over eligible business data is semantic.
        return (fixedDomain || domain.Direct.Count == 0 && domain.Parameters.Count == 0 && (!hole.Optional || domain.Outstanding.Count == 0)) && Fixed(contract, out value);
    }
    internal static PlanningBinding? Unique(PlanningSnapshot state, PlanningWorkflow workflow, PlanningHole hole)
    {
        var domain = PlanningHoleEligibility.Analyze(state, workflow, hole);
        hole.DirectCandidateCount = domain.Direct.Count; hole.ComputationParameterCount = domain.Parameters.Count;
        return Unique(domain);
    }
    internal static PlanningBinding? Unique(PlanningHoleDomain domain) => domain.ProvenTransfer && !domain.Omission && domain.Direct.Count == 1 ? domain.Direct[0] : null;

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
            if (staged.Payload["assignments"]![hole.Id]!["bindings"] is JsonArray retainedParameters && !retainedParameters.Select(p => p!.ToString()).SequenceEqual([parameter], StringComparer.Ordinal))
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

    internal static void Assign(PlanningSnapshot state, PlanningHole hole, JsonNode? value)
    {
        var json = PlanningFieldPaths.Json(state.Graph!); PlanningFieldPaths.Replace(json, hole.Path, value);
        state.Graph = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningGraph)!; hole.Resolved = true; hole.ResolutionOrigin = "deterministic"; hole.ModelRequiredReason = null;
    }
}
