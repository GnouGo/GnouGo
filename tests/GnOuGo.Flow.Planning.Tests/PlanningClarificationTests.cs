using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningClarificationTests
{
    internal static PlanningQuestion Question(string id = "interface") => new(id, "Which presentation should be used?",
        [new("compact", "Compact numeric result"), new("detailed", "Detailed numeric result")], "compact");

    [Theory]
    [InlineData(PlanningMode.Auto)]
    [InlineData(PlanningMode.Interactive)]
    public async Task MaterialQuestionPausesWithoutAPlanOrImplicitAnswer(string mode)
    {
        var (state, runtime) = await Asking(mode);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        Assert.Single(state.PendingQuestions!); Assert.Null(state.Plan); Assert.Null(state.ApprovedHash);
        Assert.Equal(1, state.ModelCalls);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "configure_mode", ExpectedRevision = state.Revision,
            Mode = mode == PlanningMode.Auto ? PlanningMode.Interactive : PlanningMode.Auto }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        Assert.Equal(1, state.ModelCalls); Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("alternative")]
    [InlineData("empty")]
    [InlineData("both")]
    public async Task MalformedAnswersRejectAtomically(string problem)
    {
        var (state, runtime) = await Asking();
        var answers = problem switch
        {
            "missing" => new List<PlanningAnswer>(), "unknown" => [new("unissued", "compact")],
            "duplicate" => [new("interface", "compact"), new("interface", "compact")],
            "alternative" => [new("interface", "unissued")], "empty" => [new("interface", Text: " ")],
            _ => [new("interface", "compact", "Custom")]
        };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        await Assert.ThrowsAsync<ArgumentException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = answers }, runtime, PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswerPreservesOriginalRequirementsAndIsCheckpointedBeforeAnotherCall(bool custom)
    {
        var (state, runtime) = await Asking();
        var original = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var calls = state.ModelCalls;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = [custom ? new("interface", Text: "Custom presentation") : new("interface", "compact")] }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal(original, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(calls, runtime.Calls.Count);
        Assert.Single(state.AnswerHistory!); Assert.Null(state.PendingQuestions);
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(state.Requirements!.Outcomes);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(runtime.Calls.Skip(calls), request => request.Prompt.Contains(custom ? "Custom presentation" : "Compact numeric result", StringComparison.Ordinal));
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task CancellationAcceptsNeitherAnAnswerNorAnApproval()
    {
        var (state, runtime) = await Asking();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "cancel", ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Cancelled, state.Status);
        Assert.Null(state.ApprovedHash); Assert.Null(state.AnswerHistory);
        Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData("recommendation")]
    [InlineData("one-option")]
    [InlineData("duplicate")]
    public void InvalidQuestionContractsCannotAcquireClarificationAuthority(string defect)
    {
        var question = Question();
        var questions = defect switch
        {
            "recommendation" => new List<PlanningQuestion> { question with { Recommended = "unissued" } },
            "one-option" => [question with { Alternatives = [new("compact", "Only")] }],
            _ => [question, question]
        };
        var state = PlannerFixture.Session();
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Throws<PlanningResponseException>(() => PlanningClarifications.Suspend(state, questions));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Asking(string mode = PlanningMode.Interactive)
    {
        var model = new CurrentPlanningFixture.LiteralModel(questions: [Question()]);
        var runtime = new TestRuntime { Respond = (request, _) => model.CallAsync(request, PlannerFixture.Ct).GetAwaiter().GetResult() };
        var state = PlannerFixture.Capture(new() { Request = new() { TenantId = "clarification", Mode = mode,
            Prompt = "Return the required public numeric result named result with value 42 using the requested presentation." } });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        return (state, runtime);
    }
}
