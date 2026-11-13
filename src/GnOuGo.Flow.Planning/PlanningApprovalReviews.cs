using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

internal static class PlanningApprovalReviews
{
    internal static async Task<PlanningApprovalReview?> ResolveAsync(PlanningSession state, PlanningCommand command,
        IPlanningRuntime runtime, CancellationToken ct)
    {
        if (command.ApprovalReviewId is null) return null;
        if (runtime is not IPlanningApprovalReviewProvider authority)
            throw new PlanningConflictException("This host does not authorize delegated test review.");
        var review = await authority.ResolveApprovalReviewAsync(state, command.ApprovalReviewId, ct);
        Validate(state, review, command.ExpectedRevision);
        if (review.Id != command.ApprovalReviewId || command.ReviewedRequirementIds is not { } ids ||
            !ids.Order(StringComparer.Ordinal).SequenceEqual(review.ReviewedRequirementIds.Order(StringComparer.Ordinal)))
            throw new PlanningConflictException("The command does not identify the retained review acknowledgments.");
        return review;
    }

    internal static void Validate(PlanningSession state, PlanningApprovalReview review, long revision)
    {
        var ids = state.Requirements?.Outcomes.Select(r => r.Id).ToArray() ?? [];
        if (review.Origin != "delegated_test" || string.IsNullOrWhiteSpace(review.Id) || string.IsNullOrWhiteSpace(review.Reviewer) ||
            !Hash(review.AuthorizationHash) || !Hash(review.EvidenceHash) || review.TenantId != state.Request.TenantId ||
            review.SessionId != state.Request.SessionId || review.ReviewedRevision != revision ||
            review.ArtifactHash != state.ComputeArtifactHash() || review.ReviewedAtUtc == default ||
            review.ReviewedAtUtc.Offset != TimeSpan.Zero || review.ReviewedRequirementIds is null ||
            review.ReviewedRequirementIds.Count != ids.Length ||
            !review.ReviewedRequirementIds.Order(StringComparer.Ordinal).SequenceEqual(ids.Order(StringComparer.Ordinal)))
            throw new PlanningConflictException("The delegated review is not bound to this artifact, revision, owner and requirements.");
    }

    internal static void VerifyRetained(PlanningSession state, PlanningApprovalReview? trustedReview)
    {
        if (state.ApprovalReview is not { } saved) return;
        if (trustedReview is null || JsonSerializer.Serialize(saved, PlanningJsonContext.Default.PlanningApprovalReview) !=
            JsonSerializer.Serialize(trustedReview, PlanningJsonContext.Default.PlanningApprovalReview))
            throw new PlanningConflictException("Delegated test approval is unavailable outside its authorized test host.");
        Validate(state, saved, saved.ReviewedRevision);
    }

    private static bool Hash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}
