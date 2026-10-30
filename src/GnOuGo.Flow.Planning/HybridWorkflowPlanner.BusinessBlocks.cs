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
        if (candidate.Extension.ContinueInScope is not null) return false;
        if (PlanningCompactConstruction.ReviewedBoundaryAuthority(state, candidate)) return false;
        if (candidate.Extension.ContinuationBinding is not null || candidate.Extension.BoundaryTransitions is { Count: > 0 }) return true;
        var construction = state.Construction!;
        var step = construction.Compact!.CurrentStep!;
        if (construction.Requirements.All(requirement => PlanningCompactConstruction.RequirementCompletionClaimed(state, requirement.Id) ||
            step.CloseRequirementIds.Contains(requirement.Id, StringComparer.Ordinal))) return false;
        var symbols = new TaskPlanSymbols(candidate.Plan);
        var behavior = ExecutableBusinessTasks(candidate.Plan);
        // An explicitly continued block completes only at its final fragment.
        // Its earlier technical checkpoints participate in this one review.
        if (candidate.TaskIds.Any(behavior.Contains) || construction.Checkpoints.Any(checkpoint => checkpoint.Step?.Id == step.Id &&
            checkpoint.Fragment["continueInScope"] is not null && checkpoint.TaskIds.Any(behavior.Contains))) return true;
        if (step.CloseRequirementIds.Count > 0 && (candidate.TaskIds.Any(behavior.Contains) ||
            construction.Checkpoints.Any(checkpoint => checkpoint.Fragment["businessReviewPending"]?.GetValue<bool>() == true &&
                checkpoint.RequirementIds.Any(id => step.CloseRequirementIds.Contains(id, StringComparer.Ordinal)) &&
                checkpoint.TaskIds.Any(behavior.Contains)))) return true;
        foreach (var path in candidate.Extension.CloseScopes.Where(path => path != "/root" && !path.StartsWith("/groups/", StringComparison.Ordinal)))
        {
            var scope = symbols.Scopes.SingleOrDefault(scope => scope.Path == path);
            if (scope is null) continue;
            var tasks = TaskPlanRevisions.Tasks(scope.Source).Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            if (candidate.TaskIds.Any(id => tasks.Contains(id) && behavior.Contains(id)) || construction.Checkpoints.Any(checkpoint =>
                checkpoint.Fragment["businessReviewPending"]?.GetValue<bool>() == true &&
                checkpoint.TaskIds.Any(id => tasks.Contains(id) && behavior.Contains(id)))) return true;
        }
        return false;
    }
    private static HashSet<string> ExecutableBusinessTasks(TaskPlan plan)
    {
        var tasks = TaskPlanRevisions.Tasks(plan.Root).ToList();
        var called = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tasks.Count; index++)
            if (tasks[index].Group is { } id && called.Add(id) && plan.Groups.SingleOrDefault(group => group.Id == id) is { } group)
                tasks.AddRange(TaskPlanRevisions.Tasks(group.Body));
        // Definitions and empty control openings have no executed business block.
        // Real computations, calls and effects retain review, including cleanup.
        var behavior = tasks.Where(task => task.Kind is not ("sequence" or "conditional" or "parallel" or "foreach"))
            .Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var task in tasks.Where(task => task.Kind is "sequence" or "conditional" or "parallel" or "foreach"))
            if ((task.Body is { } body && TaskPlanRevisions.Tasks(body).Any(child => behavior.Contains(child.Id))) ||
                (task.Otherwise is { } otherwise && TaskPlanRevisions.Tasks(otherwise).Any(child => behavior.Contains(child.Id))) ||
                task.Branches.SelectMany(TaskPlanRevisions.Tasks).Any(child => behavior.Contains(child.Id))) behavior.Add(task.Id);
        return behavior;
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
