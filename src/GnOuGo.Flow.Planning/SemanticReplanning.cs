using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning;

internal static class SemanticReplanning
{
    internal static async Task ApplyAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (state.SemanticPlan is null)
        {
            var regenerated = await PlanningModelCalls.CallAsync(state, runtime, "replan", SemanticPlanning.Prompt(state) + "\nCorrect the previously invalid response using the required schema.", SemanticPlanning.Schema(), ct);
            state.SemanticPlan = JsonSerializer.Deserialize(regenerated, PlanningJsonContext.Default.SemanticPlan)!;
        }
        else
        {
            var before = SemanticPlanning.Hash(state.SemanticPlan);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var operations = state.GroundedPlan is null ? [] : GroundedTraversal.Located(state.GroundedPlan).Select(p => p.Operation).ToArray();
            foreach (var error in state.Diagnostics.Where(d => d.Required))
            {
                foreach (var action in SemanticPlanning.Actions(state.SemanticPlan))
                    if (error.Location == "/actions/" + action.Id) ids.Add(action.Id);
                foreach (var op in operations)
                    if (error.Location.Contains("/operations/" + op.Id, StringComparison.Ordinal)) ids.Add(op.SemanticAction);
            }
            var candidate = JsonSerializer.Deserialize(JsonSerializer.Serialize(state.SemanticPlan, PlanningJsonContext.Default.SemanticPlan), PlanningJsonContext.Default.SemanticPlan)!;
            var container = Find(candidate, ids);
            var single = ids.Count == 1 ? container.FindIndex(a => ids.Contains(a.Id)) : -1;
            var target = single >= 0 ? new List<SemanticAction> { container[single] } : container.ToList();
            var schema = SemanticPlanning.Schema();
            schema["properties"] = new JsonObject { ["actions"] = PlanningSchemas.Array(PlanningSchemas.Ref("semanticAction")), ["questions"] = PlanningSchemas.Array(PlanningSchemas.Ref("question")) };
            schema["required"] = new JsonArray("actions", "questions"); PlanningJsonTransport.PruneDefinitions(schema);
            var prompt = """
                Replan this business action/subgraph atomically to address the blocking diagnostics.
                Preserve every required business outcome and the exposed output names. You may decompose actions and add adapters during subsequent grounding.
                Return replacement semantic actions, not local JSON patches or technical capability bindings. Keep referenced action IDs and output names at the boundary.
                A none_of_the_above result means no catalog capability performs that action: decompose into supported business behavior or ask a business clarification.
                Approval is host-owned and happens at FinalReview after compilation and scenarios. Do not ask the user to approve execution as a business clarification.
                Never substitute a model assertion for a required external observation or silently omit requested work.
                """ + "\n" + new JsonObject { ["request"] = state.Request.Prompt, ["semanticPlan"] = SemanticPlanning.Json(state.SemanticPlan),
                    ["targetIds"] = new JsonArray(target.Select(a => (JsonNode?)JsonValue.Create(a.Id)).ToArray()),
                    ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
                    ["groundedPlan"] = state.GroundedPlan is null ? null : PlanningJsonTransport.Grounded(state.GroundedPlan) }.ToJsonString();
            var response = await PlanningModelCalls.CallAsync(state, runtime, "replan", prompt, schema, ct);
            var shell = new JsonObject { ["summary"] = "", ["inputs"] = new JsonArray(), ["actions"] = response["actions"]!.DeepClone(),
                ["outputs"] = new JsonArray(), ["subflows"] = new JsonArray(), ["questions"] = response["questions"]!.DeepClone() };
            var replacement = JsonSerializer.Deserialize(shell, PlanningJsonContext.Default.SemanticPlan)!;
            foreach (var action in target)
                if (!SemanticPlanning.Actions(replacement).Any(a => a.Id == action.Id && action.Outputs.All(o => a.Outputs.Any(p => p.Name == o.Name))))
                    throw new PlanningResponseException([new("REPLAN_BOUNDARY_INVALID", "/actions/" + action.Id, "The replacement must preserve the required action identity and output names at its boundary.")]);
            if (single >= 0) { container.RemoveAt(single); container.InsertRange(single, replacement.Actions); }
            else { container.Clear(); container.AddRange(replacement.Actions); }
            candidate.Questions = replacement.Questions;
            var findings = SemanticPlanning.Validate(candidate);
            if (findings.Count != 0) throw new PlanningResponseException(findings);
            if (SemanticPlanning.Hash(candidate) == before)
            { state.Diagnostics.Add(new("REPLAN_NO_PROGRESS", "/actions", "The replacement did not change the semantic plan.")); state.Status = PlanningStatus.Stopped; return; }
            state.SemanticPlan = candidate;
        }
        state.Grounding = null; state.GroundedPlan = null; state.Graph = null; state.Yaml = null; state.ApprovedHash = null; state.Fixtures = null; state.Scenarios.Clear();
        state.Diagnostics = SemanticPlanning.Validate(state.SemanticPlan);
        if (state.SemanticPlan.Questions.Count > 0 && state.Diagnostics.Count == 0)
        {
            if (++state.ClarificationRounds > 3) { state.Diagnostics.Add(new("CLARIFICATION_LIMIT", "/questions", "Clarification allowance exhausted.")); state.Status = PlanningStatus.Stopped; }
            else state.Status = PlanningStatus.Clarification;
        }
        state.Phase = PlanningPhase.Grounding;
    }
    private static List<SemanticAction> Find(SemanticPlan plan, HashSet<string> ids)
    {
        if (ids.Count == 0) return plan.Actions;
        List<SemanticAction>? candidate = null;
        bool Visit(List<SemanticAction> list)
        {
            var contained = list.SelectMany(a => SemanticPlanning.Actions(new() { Actions = [a] })).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            if (!ids.IsSubsetOf(contained)) return false;
            candidate = list;
            foreach (var block in list.SelectMany(a => a.Blocks)) if (Visit(block.Actions)) return true;
            return true;
        }
        if (!Visit(plan.Actions)) foreach (var flow in plan.Subflows) if (Visit(flow.Actions)) break;
        return candidate ?? plan.Actions;
    }
}
