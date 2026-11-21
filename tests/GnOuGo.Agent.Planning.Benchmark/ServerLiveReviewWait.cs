using GnOuGo.Flow.Core.Planning;

internal static class ServerLiveReviewWait
{
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
