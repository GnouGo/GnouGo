using GnOuGo.Flow.Core.Planning;

internal static class ServerLiveReviewWait
{
    internal static bool CanOfferScopedRevision(PlanningSession state)
    {
        if (state.Status != PlanningStatus.Stopped || state.Plan is null || state.PendingCall is not null ||
            state.ApprovedHash is not null || state.ReplanAttempts >= state.Request.MaxReplanAttempts ||
            state.Diagnostics.Count == 0 || state.Construction is not { } construction) return false;
        if (state.Diagnostics.All(d => d.Code == "REPLAN_NO_PROGRESS"))
            return construction.RebuildTasks.Count > 0 && construction.Invalidations.Count > 0 &&
                state.StrategyAttempts?.LastOrDefault()?["identity"]?.GetValue<string>() is { Length: > 0 } last &&
                state.StrategyAttempts.Count(a => a["identity"]?.ToString() == last) >= 2;
        return state.Diagnostics.All(d => d.Code is "BUSINESS_REVIEW_REQUIRED" or "CONSTRUCTION_BUSINESS_REVIEW") &&
            construction.Candidate is { } candidate &&
            candidate.Reviews.LastOrDefault(review => review["outcome"]?.ToString() == "needs_change") is { } review &&
            review["defectWitness"] is System.Text.Json.Nodes.JsonObject && review["candidateHash"]?.ToString() == candidate.Hash;
    }

    internal static async Task<bool> AwaitNewRevisionAsync(PlanningSession stopped, Func<Task<PlanningSession?>> load, CancellationToken ct)
    {
        if (!CanOfferScopedRevision(stopped)) throw new PlanningConflictException("A stopped construction with an evidenced revision is required.");
        while (!ct.IsCancellationRequested)
        {
            var latest = await load() ?? throw new PlanningConflictException("The stopped session disappeared.");
            if (latest.Request.SessionId != stopped.Request.SessionId || latest.Request.TenantId != stopped.Request.TenantId)
                throw new PlanningConflictException("The revision belongs to a different session.");
            if (latest.Revision < stopped.Revision) throw new PlanningConflictException("The observed revision predates the stopped review.");
            if (latest.Revision > stopped.Revision) return true;
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
        return false;
    }

    internal static async Task<bool> WaitAsync(PlanningSession reviewed,
        Func<Task<PlanningSession?>> load, Func<bool> reviewStarted, Func<bool> reviewReady,
        Func<Task> requireNotCancelled, CancellationToken ct)
    {
        var artifact = reviewed.ComputeArtifactHash();
        if (reviewed.Status != PlanningStatus.FinalReview || artifact is null || reviewed.ApprovedHash is not null)
            throw new PlanningConflictException("An unapproved final-review artifact is required.");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var latest = await load() ?? throw new PlanningConflictException("The reviewed session disappeared.");
            if (latest.Request.TenantId != reviewed.Request.TenantId || latest.Request.SessionId != reviewed.Request.SessionId ||
                latest.Revision < reviewed.Revision)
                throw new PlanningConflictException("The reviewed session ownership or revision changed unexpectedly.");
            if (latest.Revision != reviewed.Revision)
            {
                if (reviewStarted()) throw new PlanningConflictException("Review files belong to an earlier artifact revision.");
                // The existing coordinator owns any revision. No approval or effect
                // is inferred; the caller observes it and awaits a fresh review.
                return false;
            }
            if (latest.Status != PlanningStatus.FinalReview || latest.ApprovedHash is not null || latest.ComputeArtifactHash() != artifact)
                throw new PlanningConflictException("The review-ready artifact changed without a new revision.");
            await requireNotCancelled();
            if (reviewReady()) return true;
            await Task.Delay(1000, ct);
        }
    }
}
