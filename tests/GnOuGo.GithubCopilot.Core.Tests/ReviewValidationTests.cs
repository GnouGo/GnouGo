using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class ReviewValidationTests
{
    [Theory]
    [InlineData(ReviewPublicationPolicy.DryRun, false, false)]
    [InlineData(ReviewPublicationPolicy.Interactive, false, false)]
    [InlineData(ReviewPublicationPolicy.Interactive, true, true)]
    [InlineData(ReviewPublicationPolicy.AutoComment, false, true)]
    public void PublicationGate_EnforcesPolicyBeforeGithubWrite(ReviewPublicationPolicy policy, bool humanApproved, bool expected)
    {
        var result = ReviewValidation.EvaluatePublication(new ReviewPublicationGateRequest(
            "abcdef123456", "abcdef123456", policy, 1, humanApproved, ReviewSubmitEvent.RequestChanges));

        Assert.Equal(expected, result.MayWrite);
        if (policy == ReviewPublicationPolicy.AutoComment && expected)
            Assert.Equal(ReviewSubmitEvent.Comment, result.SubmitEvent);
    }

    [Fact]
    public void PublicationGate_RejectsStaleHeadBeforeGithubWrite()
    {
        var result = ReviewValidation.EvaluatePublication(new ReviewPublicationGateRequest(
            "abcdef123456", "999999999999", ReviewPublicationPolicy.AutoComment, 3));

        Assert.False(result.MayWrite);
        Assert.Contains("head SHA changed", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicationGate_RejectsNoFindings()
    {
        var result = ReviewValidation.EvaluatePublication(new ReviewPublicationGateRequest(
            "abcdef123456", "abcdef123456", ReviewPublicationPolicy.Interactive, 0, true));

        Assert.False(result.MayWrite);
        Assert.Contains("no validated", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private const string Patch = """
        diff --git a/src/Calculator.cs b/src/Calculator.cs
        --- a/src/Calculator.cs
        +++ b/src/Calculator.cs
        @@ -10,3 +10,4 @@
         public int Divide(int left, int right)
         {
        +    return left / right;
         }
        """;

    [Fact]
    public void TryValidate_AcceptsExactRightDiffLineAndCreatesStableFingerprint()
    {
        var files = new Dictionary<string, ReviewFilePatch>(StringComparer.Ordinal)
        {
            ["src/Calculator.cs"] = new("src/Calculator.cs", "modified", Patch)
        };
        var candidate = new ReviewFindingCandidate(ReviewSeverity.High, "correctness", .98, "src\\Calculator.cs", ReviewDiffSide.Right, 12, 12, "return left / right", "Division by zero is not handled.");

        Assert.True(ReviewValidation.TryValidate(candidate, files, out var first, out var rejection));
        Assert.Null(rejection);
        Assert.True(ReviewValidation.TryValidate(candidate, files, out var second, out _));
        Assert.Equal(first!.Fingerprint, second!.Fingerprint);
        Assert.Equal("src/Calculator.cs", first.Path);
    }

    [Fact]
    public void TryValidate_RejectsLineOutsidePatch()
    {
        var files = new Dictionary<string, ReviewFilePatch>(StringComparer.Ordinal)
        {
            ["src/Calculator.cs"] = new("src/Calculator.cs", "modified", Patch)
        };
        var candidate = new ReviewFindingCandidate(ReviewSeverity.High, "correctness", .9, "src/Calculator.cs", ReviewDiffSide.Right, 99, 99, "bad", "bad");

        Assert.False(ReviewValidation.TryValidate(candidate, files, out _, out var rejection));
        Assert.Contains("not present", rejection);
    }

    [Fact]
    public void TryValidate_RejectsMalformedNullTextWithoutThrowing()
    {
        var files = new Dictionary<string, ReviewFilePatch>(StringComparer.Ordinal)
        {
            ["src/Calculator.cs"] = new("src/Calculator.cs", "modified", Patch)
        };
        var candidate = new ReviewFindingCandidate(
            ReviewSeverity.High,
            "correctness",
            .9,
            null!,
            ReviewDiffSide.Right,
            12,
            12,
            null!,
            "Division by zero is not handled.");

        Assert.False(ReviewValidation.TryValidate(candidate, files, out _, out var rejection));
        Assert.Contains("non-empty", rejection);
    }

    [Fact]
    public void BatchingAndCoverage_TrackBinarySubmoduleAndTruncation()
    {
        var files = new ReviewFilePatch[]
        {
            new("a.cs", "modified", Patch),
            new("large.cs", "modified", new string('+', 1_200), Truncated: true),
            new("image.png", "modified", string.Empty, IsBinary: true),
            new("vendor", "modified", string.Empty, IsSubmodule: true)
        };

        var batches = ReviewValidation.CreateBatches(files, 1_000);
        var coverage = ReviewValidation.CalculateCoverage(files);

        Assert.Equal(2, batches.Count);
        Assert.Equal(2, coverage.ReviewedFiles);
        Assert.Equal(2, coverage.SkippedFiles);
        Assert.Equal(["large.cs"], coverage.TruncatedPaths);
    }

    [Fact]
    public void Deduplicate_KeepsHighestConfidenceFinding()
    {
        const string fingerprint = "same";
        var low = new ReviewFinding(fingerprint, ReviewSeverity.Medium, "correctness", .7, "a.cs", ReviewDiffSide.Right, 1, 1, "e", "x");
        var high = low with { Confidence = .95 };

        var result = ReviewValidation.Deduplicate([low, high]);

        Assert.Single(result);
        Assert.Equal(.95, result[0].Confidence);
    }

    [Fact]
    public void ParseCandidates_AcceptsFencedJsonWithoutReasoningText()
    {
        var result = CopilotReviewManager.ParseCandidates("""
            ```json
            [{"severity":"high","category":"correctness","confidence":0.9,"path":"a.cs","side":"right","startLine":1,"endLine":1,"evidence":"x","explanation":"y"}]
            ```
            """);

        Assert.Single(result);
        Assert.Equal(ReviewSeverity.High, result[0].Severity);
    }
}
