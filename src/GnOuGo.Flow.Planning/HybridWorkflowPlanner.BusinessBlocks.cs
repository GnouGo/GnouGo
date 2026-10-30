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
        if (!PlanningCompactConstruction.CandidateCompletesBlock(candidate)) return false;
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        // Retain the explicit claim pending mandatory global review when only
        // finalization remains. Technical acceptance does not close the block.
        if (construction.Requirements.All(requirement => PlanningCompactConstruction.RequirementCompletionClaimed(state, requirement.Id) ||
            step.CloseRequirementIds.Contains(requirement.Id, StringComparer.Ordinal))) return false;
        return true;
    }

    // Only exact technically accepted tasks still awaiting business review receive
    // repair authority. The existing typed patch verifier enforces every edit.
    private static void AuthorizePendingBusinessRepair(PlanningSession state, JsonObject review, string[] targets)
    {
        var construction = state.Construction!; var candidate = construction.Candidate!;
        if (state.Plan is null || candidate.BaseHash != PlanningConstruction.Hash(state.Plan)) return;
        var accepted = new TaskPlanSymbols(state.Plan); var current = new TaskPlanSymbols(candidate.Plan);
        var paths = new List<string>(); var tasks = new List<string>();
        foreach (var target in targets.Where(target => !candidate.TaskIds.Contains(target, StringComparer.Ordinal)))
        {
            if (!accepted.Tasks.TryGetValue(target, out var task) || !current.Tasks.TryGetValue(target, out var next) ||
                task.Scope.Path != next.Scope.Path || accepted.Phase(target) != current.Phase(target) ||
                !JsonNode.DeepEquals(PlanningConstruction.TaskNode(task.Task), PlanningConstruction.TaskNode(next.Task))) return;
            var owners = construction.Checkpoints.Where(checkpoint => checkpoint.TaskIds.Contains(target, StringComparer.Ordinal) &&
                !checkpoint.InvalidatedTaskIds.Contains(target, StringComparer.Ordinal)).ToArray();
            if (owners.Length == 0 || owners[^1].Fragment["businessReviewPending"]?.GetValue<bool>() != true ||
                owners[^1].Fragment["tasks"] is not JsonArray original || !original.Any(node => node?["id"]?.ToString() == target &&
                    JsonNode.DeepEquals(node, PlanningConstruction.Node(task.Task, PlanningJsonContext.Default.PlanTask)))) return;
            var offered = task.Task.Kind is "operation" or "transform" or "call"
                ? new[] { "/tasks/" + target + "/inputs" }
                : accepted.Values.Keys.Where(path => path.StartsWith("/tasks/" + target + "/outputs/", StringComparison.Ordinal)).ToArray();
            if (offered.Length == 0) return;
            try { TaskPlanRevisions.ValidateEditablePaths(state, offered); }
            catch (ArgumentException) { return; }
            paths.AddRange(offered); tasks.Add(target);
        }
        if (tasks.Count == 0) return;
        candidate.AuthorizedPaths = (candidate.AuthorizedPaths ?? []).Concat(paths).Distinct(StringComparer.Ordinal).ToList();
        candidate.TaskIds = candidate.TaskIds.Concat(tasks).Distinct(StringComparer.Ordinal).ToList();
        review["pendingRepairPaths"] = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
    }

    // Unsupported or already reviewed targets retain evidence-only authority.
    private static async Task RetrieveBusinessFindingEvidenceAsync(PlanningSession state, IPlanningRuntime runtime, CancellationToken ct)
    {
        await ReserveConstructionReviewAsync(state, runtime, ct);
        var context = PlanningBusinessProjection.Build(state, state.Construction!.Candidate!.Extension.Complete);
        var prior = state.Construction.Candidate.Reviews.Last(review => review["outcome"]?.ToString() == "needs_change");
        context["finding"] = prior["response"]!["decision"]!.DeepClone();
        var addresses = BusinessReviewReconsiderationEvidence(state);
        if (addresses.Length == 0) { Stop(state); return; }
        var schema = PlanningSchemas.Object(("patch", PlanningSchemas.Type("null")),
            ("evidenceRequests", PlanningSchemas.Nullable(PlanningSchemas.Array(PlanningSchemas.Enum(addresses), 1, PlanningOptimizationThresholds.Maximum(state, 8)))),
            ("clarifications", PlanningSchemas.Nullable(PlanningSchemas.Clarifications(PlanningTokenThresholds.OptimizationAdvisory(state.Request)))));
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
