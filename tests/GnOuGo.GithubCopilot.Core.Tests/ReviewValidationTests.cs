using System.Text.Json;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class ReviewValidationTests
{
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
    public void ExistingComments_DeduplicateByFingerprintOrEquivalentLocationAndBody()
    {
        var finding = new ReviewFinding(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ReviewSeverity.High,
            "correctness",
            .95,
            "src/Calculator.cs",
            ReviewDiffSide.Right,
            12,
            12,
            "return left / right",
            "Division by zero is not handled.");

        Assert.True(ReviewValidation.IsDuplicateOfExisting(finding,
        [
            new ExistingReviewComment("src/other.cs", ReviewDiffSide.Right, 1, 1, "Different", finding.Fingerprint)
        ]));
        Assert.True(ReviewValidation.IsDuplicateOfExisting(finding,
        [
            new ExistingReviewComment("src\\Calculator.cs", ReviewDiffSide.Right, 12, 12, "Already reported: Division by zero is not handled.")
        ]));
        Assert.False(ReviewValidation.IsDuplicateOfExisting(finding,
        [
            new ExistingReviewComment("src/Calculator.cs", ReviewDiffSide.Right, 12, 12, "This is a different problem.")
        ]));
    }

    [Fact]
    public void ExistingComments_AotJsonContextRoundTrips()
    {
        IReadOnlyList<ExistingReviewComment> comments =
        [
            new ExistingReviewComment("src/Calculator.cs", ReviewDiffSide.Right, 12, 12, "Existing finding.", "fingerprint")
        ];

        var json = System.Text.Json.JsonSerializer.Serialize(comments, CopilotCoreJsonContext.Default.IReadOnlyListExistingReviewComment);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize(json, CopilotCoreJsonContext.Default.IReadOnlyListExistingReviewComment);

        var comment = Assert.Single(roundTrip!);
        Assert.Equal("src/Calculator.cs", comment.Path);
        Assert.Equal(ReviewDiffSide.Right, comment.Side);
    }
}
