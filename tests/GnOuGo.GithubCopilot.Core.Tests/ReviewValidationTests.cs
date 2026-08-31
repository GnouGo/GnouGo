using System.Text.Json;
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

    [Fact]
    public void PublicationGate_UsesSchemaVisibleWireEnumValues()
    {
        var request = new ReviewPublicationGateRequest(
            "abcdef123456",
            "abcdef123456",
            ReviewPublicationPolicy.AutoComment,
            1,
            ProposedEvent: ReviewSubmitEvent.RequestChanges);

        var json = JsonSerializer.Serialize(
            request,
            CopilotCoreJsonContext.Default.ReviewPublicationGateRequest);

        Assert.Contains("\"policy\":\"auto_comment\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"proposedEvent\":\"request_changes\"", json, StringComparison.Ordinal);
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

    [Fact]
    public void ParseCandidates_AcceptsStableFindingsEnvelope()
    {
        var result = CopilotReviewManager.ParseCandidates("""
            {"findings":[{"severity":"critical","category":"security","confidence":0.99,"path":"a.cs","side":"right","startLine":2,"endLine":2,"evidence":"x","explanation":"y"}]}
            """);

        Assert.Single(result);
        Assert.Equal(ReviewSeverity.Critical, result[0].Severity);
    }

    [Fact]
    public void ParseCandidates_FindsValidArrayAfterNonJsonBracketedProse()
    {
        var result = CopilotReviewManager.ParseCandidates("""
            Required fields are [severity, path, line].
            [{"severity":"medium","category":"correctness","confidence":0.8,"path":"a.cs","side":"left","startLine":4,"endLine":4,"evidence":"x","explanation":"y"}]
            """);

        Assert.Single(result);
        Assert.Equal(ReviewDiffSide.Left, result[0].Side);
    }

    [Fact]
    public void ParseCandidates_RejectsProseWithoutStructuredFindings()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CopilotReviewManager.ParseCandidates("No concrete issue was identified."));

        Assert.Contains("valid JSON array", exception.Message, StringComparison.Ordinal);
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
    public void BuildBatchPrompt_AppliesInstructionsAndOnlyRelevantExistingComments()
    {
        var request = new CopilotReviewStartRequest(
            new CopilotRequestContext("tenant", "correlation", "run", "step"),
            new CopilotRuntimeConfiguration(Path.GetTempPath(), "test-model"),
            new string('a', 40),
            new string('b', 40),
            [new ReviewFilePatch("src/Calculator.cs", "modified", Patch)],
            ReviewInstructions: "Check integer division and null handling.",
            ExistingComments:
            [
                new ExistingReviewComment("src/Calculator.cs", ReviewDiffSide.Right, 12, 12, "Existing division finding."),
                new ExistingReviewComment("src/Unrelated.cs", ReviewDiffSide.Right, 5, 5, "Unrelated finding.")
            ]);
        var batch = Assert.Single(ReviewValidation.CreateBatches(request.Files, 60_000));

        var prompt = CopilotReviewManager.BuildBatchPrompt(request, batch);

        Assert.Contains("Check integer division and null handling.", prompt, StringComparison.Ordinal);
        Assert.Contains("<review_instructions_json>", prompt, StringComparison.Ordinal);
        Assert.Contains("<untrusted_existing_comments_json>", prompt, StringComparison.Ordinal);
        Assert.Contains("Existing division finding.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated finding.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildBatchPrompt_BoundsSerializedExistingCommentsBlock()
    {
        var request = new CopilotReviewStartRequest(
            new CopilotRequestContext("tenant", "correlation", "run", "step"),
            new CopilotRuntimeConfiguration(Path.GetTempPath(), "test-model"),
            new string('a', 40),
            new string('b', 40),
            [new ReviewFilePatch("src/Calculator.cs", "modified", Patch)],
            ExistingComments:
            [
                new ExistingReviewComment(
                    "src/Calculator.cs",
                    ReviewDiffSide.Right,
                    12,
                    12,
                    string.Concat(Enumerable.Repeat("quoted \\\" text \\\\ ", 10_000)))
            ]);
        var batch = Assert.Single(ReviewValidation.CreateBatches(request.Files, 60_000));

        var prompt = CopilotReviewManager.BuildBatchPrompt(request, batch);
        var openingTag = $"<untrusted_existing_comments_json>{Environment.NewLine}";
        var start = prompt.IndexOf(openingTag, StringComparison.Ordinal) + openingTag.Length;
        var end = prompt.IndexOf($"{Environment.NewLine}</untrusted_existing_comments_json>", start, StringComparison.Ordinal);
        var serializedComments = prompt[start..end];

        Assert.InRange(serializedComments.Length, 1, 64_000);
        var comments = System.Text.Json.JsonSerializer.Deserialize(
            serializedComments,
            CopilotCoreJsonContext.Default.IReadOnlyListExistingReviewComment);
        Assert.NotEmpty(comments!);
    }

    [Fact]
    public async Task StartAsync_RejectsOversizedReviewInstructionsBeforeCreatingSession()
    {
        var manager = new CopilotReviewManager(null!);
        var request = new CopilotReviewStartRequest(
            new CopilotRequestContext("tenant", "correlation", "run", "step"),
            new CopilotRuntimeConfiguration(Path.GetTempPath(), "test-model"),
            new string('a', 40),
            new string('b', 40),
            [new ReviewFilePatch("src/Calculator.cs", "modified", Patch)],
            ReviewInstructions: new string('x', 32_001));

        await Assert.ThrowsAsync<ArgumentException>(() => manager.StartAsync(request, TestContext.Current.CancellationToken));
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
