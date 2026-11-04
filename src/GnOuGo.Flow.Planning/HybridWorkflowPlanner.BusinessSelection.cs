using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    private static async Task SelectBusinessBlockAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        var construction = state.Construction!;
        if (state.PendingCall is null) construction.IssuedBaseHash = PlanningConstruction.Hash(state.Plan);
        else if (construction.IssuedBaseHash != PlanningConstruction.Hash(state.Plan))
            throw new PlanningConflictException("The issued business selection no longer matches its retained accepted work.");
        var response = await CompactCallAsync(state, runtime, PlanningCompactConstruction.SelectPurpose, () =>
            PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance, PlanningCompactConstruction.SelectionContext(state),
                PlanningCompactConstruction.SelectionSchema(state), PlanningCompactConstruction.SelectPurpose), ct);
        if (construction.IssuedBaseHash != PlanningConstruction.Hash(state.Plan))
            throw new PlanningConflictException("The completed business selection no longer matches its accepted baseline.");
        var action = response["action"] as JsonObject;
        var payload = action?["kind"]?.ToString() switch
        {
            "block" => "block", "completion" => "completionRequirementIds",
            "evidence" => "evidenceRequests", "clarification" => "clarifications", _ => null
        };
        if (response.AsObject().Count != 1 || action is null || action.Count != 2 || payload is null || action[payload] is null)
            PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/action", "Return exactly one offered business-selection action and its required payload.");
        if (action!["evidenceRequests"] is JsonArray evidence)
        {
            PlanningConstruction.LoadEvidence(state, evidence.Select(value => value!.ToString()).ToList(), followup: next =>
            {
                var request = PlanningCompactConstruction.Request(PlanningCompactConstruction.SelectGuidance, PlanningCompactConstruction.SelectionContext(next),
                    PlanningCompactConstruction.SelectionSchema(next), PlanningCompactConstruction.SelectPurpose);
                return PlanningModelCalls.PrepareRequest(next, PlanningCompactConstruction.SelectPurpose, request.Prompt,
                    request.StructuredOutputSchema!.AsObject(), next.Construction!.Limits.DraftOutputTokens);
            }, purpose: PlanningCompactConstruction.SelectPurpose);
            return;
        }
        if (action!["clarifications"] is JsonArray questions)
        {
            var parsed = questions.Deserialize(PlanningJsonContext.Default.ListPlanningQuestion)!;
            PlanningClarifications.Suspend(state, parsed); state.PendingQuestions = parsed; state.Status = PlanningStatus.Clarification;
            return;
        }
        if (action!["completionRequirementIds"] is JsonArray requirements)
        {
            PlanningCompactConstruction.SelectCompletion(state, requirements.Select(value => value!.ToString()).ToArray());
            return;
        }
        PlanningCompactConstruction.SelectBlock(state, action!["block"]!);
    }
}
