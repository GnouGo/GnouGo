using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.Agent.Server.Reviews;

public sealed class ReviewPublicationSettings
{
    // Explicit integration identities, interpreted only by this host adapter.
    public string GitHubServer { get; set; } = "github";
    public string CopilotServer { get; set; } = "GnOuGo.GithubCopilot.Mcp";
}

internal sealed record ReviewDraftRequest(string Owner, string Repository, int PullNumber, ReviewEvaluationRequest Evaluation);
internal sealed record ReviewDraft(string DraftId, string Owner, string Repository, int PullNumber, string HeadSha, ReviewEvaluationResult Evaluation);
internal sealed record ReviewPublishRequest(string DraftId);
internal sealed record ReviewPublishResult(string Status, string DraftId, string Message);
internal sealed record StoredReviewDraft(string TenantId, string RunId, ReviewDraft Draft, string Status = "prepared");

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true, NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(ReviewDraftRequest))]
[JsonSerializable(typeof(ReviewDraft))]
[JsonSerializable(typeof(ReviewPublishRequest))]
[JsonSerializable(typeof(ReviewPublishResult))]
[JsonSerializable(typeof(StoredReviewDraft))]
internal partial class ReviewPublicationJsonContext : JsonSerializerContext;
