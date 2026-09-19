namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class ReviewEvaluationTests
{
    private static ReviewEvaluationRequest Request() => new(
        new(new string('a', 40), new string('b', 40), [], new(1, 1, 0, 0, [], []), [], "No findings") { Complete = true },
        Path.GetFullPath("review-workspace"), [new("Unit tests", true, ExpectedArgumentsJson: "{\"command\":\"test\"}")], [Check()]);
    private static ReviewCheckResult Check() => new("Unit tests", ReviewCheckStatus.Passed, "Original test execution", true,
        new("call1", null, "terminal", "{\"command\":\"test\"}", true, true, false, [new(Path.GetFullPath("review-workspace"), 0, "passed")], null));

    [Fact]
    public void CompleteReviewWithZeroFindingsApprovesAndIncludesEveryCheck()
    {
        var result = ReviewEvaluation.Evaluate(Request());
        Assert.Equal(ReviewSubmitEvent.Approve, result.SubmitEvent);
        Assert.Contains("Unit tests: Passed", result.Body); Assert.Contains("exit code: 0", result.Body);
    }
    [Fact]
    public void MissingCheckCannotDisappearFromTheReview()
    {
        var request = Request();
        var result = ReviewEvaluation.Evaluate(request with { RequiredChecks = [.. request.RequiredChecks, new("Lint", true)] });
        Assert.Equal(ReviewSubmitEvent.Comment, result.SubmitEvent);
        Assert.Equal(ReviewCheckStatus.Blocked, result.Checks[1].Status); Assert.Contains("Lint: Blocked", result.Body);
    }
    [Theory]
    [InlineData(0, ReviewCheckStatus.Failed, ReviewCheckStatus.Passed, ReviewSubmitEvent.Approve)]
    [InlineData(1, ReviewCheckStatus.Passed, ReviewCheckStatus.Failed, ReviewSubmitEvent.RequestChanges)]
    [InlineData(1, ReviewCheckStatus.Blocked, ReviewCheckStatus.Failed, ReviewSubmitEvent.RequestChanges)]
    public void ObservedExitCodeDeterminesCommandOutcome(int exit, ReviewCheckStatus claimed, ReviewCheckStatus expected, ReviewSubmitEvent verdict)
    {
        var check = Check();
        var result = ReviewEvaluation.Evaluate(Request() with { Checks = [check with { Status = claimed, RequiresExecution = false,
            Execution = check.Execution! with { Terminals = [new(Path.GetFullPath("review-workspace"), exit, "result")] } }] });
        Assert.Equal(expected, Assert.Single(result.Checks).Status); Assert.Equal(verdict, result.SubmitEvent);
    }
    [Fact]
    public void GroupedReusedEvidenceCannotPassSeveralRequestedChecks()
    {
        var request = Request();
        var result = ReviewEvaluation.Evaluate(request with {
            RequiredChecks = [.. request.RequiredChecks, new("Lint", true, ExpectedArgumentsJson: "{\"command\":\"test\"}")],
            Checks = [Check(), Check() with { Name = "Lint" }] });
        Assert.All(result.Checks, c => Assert.Equal(ReviewCheckStatus.Blocked, c.Status));
        Assert.Equal(ReviewSubmitEvent.Comment, result.SubmitEvent);
    }
    [Fact]
    public void MissingWrongOrIncompleteObservationsRemainBlocked()
    {
        var check = Check();
        var variants = new ReviewCheckResult[] { check with { Execution = null },
            check with { Execution = check.Execution! with { CompletionObserved = false } },
            check with { Execution = check.Execution! with { ConflictingCompletion = true } },
            check with { Execution = check.Execution! with { ArgumentsJson = "{\"command\":\"different\"}" } },
            check with { Execution = check.Execution! with { Terminals = [new(Path.GetFullPath("another-clone"), 0, "passed")] } },
            check with { Execution = check.Execution! with { Terminals = [new(Path.GetFullPath("review-workspace"), null, "passed")] } } };
        foreach (var candidate in variants)
            Assert.Equal(ReviewSubmitEvent.Comment, ReviewEvaluation.Evaluate(Request() with { Checks = [candidate] }).SubmitEvent);
    }
    [Fact]
    public void IncompleteCoverageCannotApproveAndExistingBlockersStillRequireChanges()
    {
        var request = Request();
        Assert.Equal(ReviewSubmitEvent.Comment, ReviewEvaluation.Evaluate(request with { Review = request.Review with { Complete = false } }).SubmitEvent);
        Assert.Equal(ReviewSubmitEvent.RequestChanges, ReviewEvaluation.Evaluate(request with { Review = request.Review with { BlockingFindingCount = 1 } }).SubmitEvent);
    }
    [Fact]
    public void EmptyModelResponseIsNotACompleteReview()
        => Assert.Throws<InvalidOperationException>(() => CopilotReviewManager.ParseCandidates(" "));
}
