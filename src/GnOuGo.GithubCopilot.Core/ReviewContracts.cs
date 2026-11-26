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

public sealed record ObservedReview(IReadOnlyList<string> ObservedFiles, IReadOnlyList<string> UnobservedFiles,
    IReadOnlyList<ReviewFinding> Findings, IReadOnlyList<string> Errors);

public sealed record CopilotReviewBatch(
    int Index,
    IReadOnlyList<ReviewFilePatch> Files,
    int CharacterCount);
