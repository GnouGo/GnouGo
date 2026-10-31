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
        if (new[] { "block", "completionRequirementIds", "evidenceRequests", "clarifications" }.Count(name => response[name] is not null) != 1)
            PlanningConstruction.Fail("PROPOSAL_ACTION_INVALID", "/", "Return exactly one business-selection action.");
        if (response["evidenceRequests"] is JsonArray evidence)
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
        if (response["clarifications"] is JsonArray questions)
        {
            var parsed = questions.Deserialize(PlanningJsonContext.Default.ListPlanningQuestion)!;
            PlanningClarifications.Suspend(state, parsed); state.PendingQuestions = parsed; state.Status = PlanningStatus.Clarification;
            return;
        }
        if (response["completionRequirementIds"] is JsonArray requirements)
        {
            PlanningCompactConstruction.SelectCompletion(state, requirements.Select(value => value!.ToString()).ToArray());
            return;
        }
        PlanningCompactConstruction.SelectBlock(state, response["block"]!);
    }
}
