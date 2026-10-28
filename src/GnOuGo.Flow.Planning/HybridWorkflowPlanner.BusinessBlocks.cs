using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class HybridWorkflowPlanner
{
    // Construction checkpoints are technical transactions, not a Judge schedule.
    // Review a completed business boundary; prefer one global review when the
    // remaining work is only finalization. No scenario or checkpoint-count rule.
    internal static bool NeedsBusinessReview(PlanningSession state, PlanningConstructionCandidate candidate)
    {
        if (candidate.Extension.Complete || candidate.AuthorizedPaths is { Count: > 0 }) return true;
        if (PlanningCompactConstruction.ReviewedBoundaryAuthority(state, candidate)) return false;
        if (candidate.Extension.ContinuationBinding is not null || candidate.Extension.BoundaryTransitions is { Count: > 0 }) return true;
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        if (construction.Requirements.All(requirement => requirement.Status == "closed" ||
            step.CloseRequirementIds.Contains(requirement.Id, StringComparer.Ordinal))) return false;
        if (step.CloseRequirementIds.Count > 0) return true;
        var symbols = new TaskPlanSymbols(candidate.Plan);
        foreach (var path in candidate.Extension.CloseScopes.Where(path => path != "/root" && !path.StartsWith("/groups/", StringComparison.Ordinal)))
        {
            var scope = symbols.Scopes.SingleOrDefault(scope => scope.Path == path);
            if (scope is null) continue;
            var tasks = TaskPlanRevisions.Tasks(scope.Source).Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            if (candidate.TaskIds.Any(tasks.Contains) || construction.Checkpoints.Any(checkpoint =>
                checkpoint.Fragment["businessReviewPending"]?.GetValue<bool>() == true && checkpoint.TaskIds.Any(tasks.Contains))) return true;
        }
        return false;
    }
    // Evidence-only use of the existing correction flow cannot edit accepted work.
    private static async Task RetrieveBusinessFindingEvidenceAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await ReserveConstructionReviewAsync(state, runtime, ct);
        var context = PlanningBusinessProjection.Build(state, state.Construction!.Candidate!.Extension.Complete);
        var prior = state.Construction.Candidate.Reviews.Last(review => review["outcome"]?.ToString() == "needs_change");
        context["finding"] = prior["rawResponse"]!["decision"]!.DeepClone();
        var addresses = BusinessReviewReconsiderationEvidence(state);
        if (addresses.Length == 0) { Stop(state); return; }
        var schema = PlanningSchemas.Object(("patch", PlanningSchemas.Type("null")),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Enum(addresses), 1, 8))),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications())));
        var request = state.PendingCall?.Request ?? PlanningCompactConstruction.Request(
            "Use the existing original-evidence action to examine this business finding. Accepted work is immutable here: patch is null. Request exact evidence addressing the cited obligation or implementation when the finding needs reconsideration on the unchanged facts; otherwise clarify the missing business fact. No evidence grants editing or execution authority.",
            context, schema, "replan");
        using var accounting = new PlanningCorrectionAttempt(state, correcting: false);
        var response = await PlanningModelCalls.CallAsync(state, runtime, "replan", request.Prompt,
            request.StructuredOutputSchema!.AsObject(), ct, correction: accounting);
        if (response["evidenceRequests"] is JsonArray evidence && response["clarifications"] is null &&
            TryPrepareBusinessReviewReconsideration(state, evidence.Select(address => address!.ToString()).ToArray()))
        { accounting.EvidenceAccepted(); return; }
        if (response["clarifications"] is JsonArray questions && response["evidenceRequests"] is null)
        {
            var parsed = questions.Deserialize(PlanningJsonContext.Default.ListPlanningQuestion)!;
            PlanningClarifications.Suspend(state, parsed); state.PendingQuestions = parsed; state.Status = PlanningStatus.Clarification;
            accounting.EvidenceAccepted(); return;
        }
        state.Diagnostics = state.Construction.Candidate.Diagnostics;
        Stop(state);
    }

}
