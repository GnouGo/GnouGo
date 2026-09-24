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

public sealed record ExistingReviewComment(
    string Path,
    ReviewDiffSide? Side,
    int? StartLine,
    int? EndLine,
    string Body,
    string? Fingerprint = null);

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
    CopilotPermissionMode PermissionMode = CopilotPermissionMode.Deny,
    string? ReviewInstructions = null,
    IReadOnlyList<ExistingReviewComment>? ExistingComments = null)
{
    /// <summary>Optional JSON object containing original upstream execution results, retained as untrusted review context.</summary>
    public string? RuntimeContextJson { get; init; }
}

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
    string Summary)
{
    public bool Complete { get; init; }
    // Includes validated findings suppressed because an existing comment already reports them.
    public int BlockingFindingCount { get; init; }
}
