using System.Text.Json.Serialization;

namespace GnOuGo.GithubCopilot.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ReviewSeverity>))]
public enum ReviewSeverity
{
    Low,
    Medium,
    High,
    Critical
}

[JsonConverter(typeof(JsonStringEnumConverter<ReviewDiffSide>))]
public enum ReviewDiffSide
{
    Left,
    Right
}

public sealed record ReviewFilePatch(
    string Path,
    string Status,
    string Patch,
    bool IsBinary = false,
    bool IsSubmodule = false,
    bool Truncated = false,
    string? PreviousPath = null);

public sealed record ReviewFinding(
    string Fingerprint,
    ReviewSeverity Severity,
    string Category,
    double Confidence,
    string Path,
    ReviewDiffSide Side,
    int StartLine,
    int EndLine,
    string Evidence,
    string Explanation,
    string? SuggestedPatch = null);

public sealed record ReviewFindingCandidate(
    ReviewSeverity Severity,
    string Category,
    double Confidence,
    string Path,
    ReviewDiffSide Side,
    int StartLine,
    int EndLine,
    string Evidence,
    string Explanation,
    string? SuggestedPatch = null);

public sealed record ReviewCoverage(
    int TotalFiles,
    int ReviewedFiles,
    int SkippedFiles,
    int TruncatedFiles,
    IReadOnlyList<string> SkippedPaths,
    IReadOnlyList<string> TruncatedPaths);

public sealed record CopilotReviewStartRequest(
    CopilotRequestContext Context,
    CopilotRuntimeConfiguration Configuration,
    string BaseSha,
    string HeadSha,
    IReadOnlyList<ReviewFilePatch> Files,
    int MaxBatchCharacters = 60_000,
    CopilotPermissionMode PermissionMode = CopilotPermissionMode.Deny);

public sealed record CopilotReviewSession(
    string ReviewHandle,
    string SessionHandle,
    string BaseSha,
    string HeadSha,
    int BatchCount,
    ReviewCoverage Coverage);

public sealed record CopilotReviewBatch(
    int Index,
    IReadOnlyList<ReviewFilePatch> Files,
    int CharacterCount);

public sealed record CopilotReviewAnalyzeResult(
    string ReviewHandle,
    int BatchIndex,
    IReadOnlyList<ReviewFinding> Findings,
    IReadOnlyList<string> RejectedFindings);

public sealed record CopilotReviewResult(
    string BaseSha,
    string HeadSha,
    IReadOnlyList<ReviewFinding> Findings,
    ReviewCoverage Coverage,
    IReadOnlyList<string> RejectedFindings,
    string Summary);

[JsonConverter(typeof(JsonStringEnumConverter<ReviewPublicationPolicy>))]
public enum ReviewPublicationPolicy
{
    DryRun,
    Interactive,
    AutoComment
}

[JsonConverter(typeof(JsonStringEnumConverter<ReviewSubmitEvent>))]
public enum ReviewSubmitEvent
{
    Comment,
    RequestChanges
}

public sealed record ReviewPublicationGateRequest(
    string ExpectedHeadSha,
    string CurrentHeadSha,
    ReviewPublicationPolicy Policy,
    int ValidatedFindingCount,
    bool HumanApproved = false,
    ReviewSubmitEvent ProposedEvent = ReviewSubmitEvent.Comment);

public sealed record ReviewPublicationGateResult(
    bool MayWrite,
    ReviewSubmitEvent? SubmitEvent,
    string Reason);
