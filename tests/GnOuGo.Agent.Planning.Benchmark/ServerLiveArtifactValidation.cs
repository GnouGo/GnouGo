using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;

// Isolated host recovery of a completed, unexecuted artifact. No model exists here.
internal static class ServerLiveArtifactValidation
{
    internal static WorkflowPlanningRuntime Runtime(IMcpClientFactory? metadata, PlanningPolicy policy,
        Func<PlanningSession, CancellationToken, Task> checkpoint)
        => new(new WorkflowEngine { McpClientFactory = metadata, PlanningPolicy = policy }, checkpoint);

    internal static async Task<PlanningSession> RevalidateAsync(PlanningSession current, PlanningSession reviewed,
        long expectedRevision, string expectedArtifactHash, IPlanningRuntime runtime, CancellationToken ct)
    {
        if (current.Revision != expectedRevision || current.Status != PlanningStatus.Stopped ||
            reviewed.Status != PlanningStatus.FinalReview || reviewed.ComputeArtifactHash() != expectedArtifactHash ||
            current.PendingCall is not null || current.PendingQuestions is not null || current.ApprovedHash is not null || current.ApprovalReview is not null ||
            current.Construction is not { Complete: true, Candidate: null } || current.Diagnostics.Count == 0 ||
            current.Diagnostics.Any(d => d.Code is not ("CATALOG_CHANGED" or "CATALOG_UNAVAILABLE")) ||
            !SameAuthority(current, reviewed))
            throw new PlanningConflictException("Only the exact completed, unapproved catalog-validation failure can be revalidated.");
        PlanningArtifactApproval.Verify(reviewed);
        var findings = await runtime.ValidateCatalogAsync(current.Catalog!, ct);
        if (findings.Count != 0)
            throw new PlanningConflictException("Selected contracts still require reconciliation: " + string.Join(", ", findings.Select(d => d.Code).Distinct()));

        // The existing coordinator permits this restart with the identical captured
        // generation values. This grants no new settings, calls or edit authority.
        var planner = new HybridWorkflowPlanner();
        var resumed = await planner.AdvanceAsync(current, new() { Kind = "configure_generation", ExpectedRevision = current.Revision,
            Generation = reviewed.Request.Generation }, runtime, ct);
        var restored = await planner.AdvanceAsync(resumed, new() { ExpectedRevision = resumed.Revision }, runtime, ct);
        if (restored.Status != PlanningStatus.FinalReview || restored.ComputeArtifactHash() != expectedArtifactHash ||
            !SameAuthority(restored, reviewed))
            throw new PlanningConflictException("Revalidation did not reproduce the exact reviewed artifact and accounting.");
        PlanningArtifactApproval.Verify(restored);
        return restored;
    }

    private static bool SameAuthority(PlanningSession current, PlanningSession reviewed)
        => current.SchemaVersion == reviewed.SchemaVersion && current.ModelCalls == reviewed.ModelCalls &&
           current.ReplanAttempts == reviewed.ReplanAttempts &&
           Same(current.Request, reviewed.Request, PlanningJsonContext.Default.PlanningRequest) &&
           Same(current.Requirements, reviewed.Requirements, PlanningJsonContext.Default.PlanningRequirements) &&
           Same(current.Plan, reviewed.Plan, PlanningJsonContext.Default.TaskPlan) &&
           Same(current.Graph, reviewed.Graph, PlanningJsonContext.Default.PlanningGraph) &&
           Same(current.Catalog, reviewed.Catalog, PlanningJsonContext.Default.PlanningCatalog) &&
           Same(current.Usage, reviewed.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot) &&
           current.Construction?.Complete == reviewed.Construction?.Complete &&
           JsonSerializer.Serialize(current.Construction, PlanningJsonContext.Default.PlanningConstructionState) ==
           JsonSerializer.Serialize(reviewed.Construction, PlanningJsonContext.Default.PlanningConstructionState);

    private static bool Same<T>(T? left, T? right, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (left is null || right is null) return left is null && right is null;
        return JsonSerializer.Serialize(left, type) == JsonSerializer.Serialize(right, type);
    }
}
