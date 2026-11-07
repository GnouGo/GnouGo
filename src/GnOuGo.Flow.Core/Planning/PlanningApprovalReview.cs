namespace GnOuGo.Flow.Core.Planning;

/// <summary>An externally retained review, resolved by a trusted host rather than model output.</summary>
public sealed record PlanningApprovalReview(
    string Id, string Origin, string Reviewer, string AuthorizationHash, string EvidenceHash,
    string TenantId, string SessionId, long ReviewedRevision, string ArtifactHash,
    List<string> ReviewedRequirementIds, DateTimeOffset ReviewedAtUtc);

/// <summary>
/// Optional host authority. Production hosts do not provide delegated test review.
/// Implementations must verify retained authorization and evidence before returning a receipt.
/// </summary>
public interface IPlanningApprovalReviewProvider
{
    Task<PlanningApprovalReview> ResolveApprovalReviewAsync(
        PlanningSession session, string reviewId, CancellationToken ct);
}
