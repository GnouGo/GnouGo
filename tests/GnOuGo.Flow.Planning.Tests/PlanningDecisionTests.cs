using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningDecisionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static JsonObject Proposal(string evidence = "Return a greeting")
    {
        var first = GnOuGo.Planning.Examples.PlanningCorpus.Semantic(PlannerFixture.Greeting());
        var second = GnOuGo.Planning.Examples.PlanningCorpus.Semantic(PlannerFixture.Greeting());
        first.Actions[0].Purpose = "Return a formal greeting";
        second.Actions[0].Purpose = "Return a casual greeting";
        JsonObject Option(string id, string label, bool preferred, SemanticPlan plan) => new()
        { ["id"] = id, ["label"] = label, ["preferred"] = preferred, ["reason"] = preferred ? "Suitable for a professional audience." : "More conversational.", ["result"] = SemanticPlanning.Json(plan) };
        return new() { ["result"] = null, ["decision"] = new JsonObject
        {
            ["question"] = "Which greeting tone?", ["context"] = "The audience is unspecified.", ["evidence"] = evidence, ["allowCustomAnswer"] = true,
            ["options"] = new JsonArray(Option("formal", "Formal", true, first), Option("casual", "Casual", false, second))
        } };
    }
    private static PlanningSession Restore(PlanningSession state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime, PlanningCommand? command = null)
    { command ??= new(); command.ExpectedRevision = state.Revision; return new TypedWorkflowPlanner().AdvanceAsync(state, command, runtime, Ct); }

    [Fact]
    public async Task AutoPersistsPreferredBeforeApplyingWithoutAnotherModelCall()
    {
        var runtime = new TestRuntime { RawDecisionResponse = true, Respond = _ => new() { Json = Proposal() } };
        var state = PlannerFixture.Session(); state.Request.Mode = PlanningMode.Auto;
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Null(state.PendingDecision); Assert.Null(state.SemanticPlan);
        var record = Assert.Single(state.Decisions); Assert.Equal("formal", record.Answer.OptionId); Assert.Equal("auto", record.Source); Assert.NotEmpty(record.Reason);
        state = await Advance(Restore(state), runtime);
        Assert.Equal("Return a formal greeting", state.SemanticPlan!.Actions[0].Purpose);
        Assert.Single(runtime.Calls); Assert.Null(state.DecisionContinuation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteractiveRestoresAndAnswersWithoutRestarting(bool custom)
    {
        var runtime = new TestRuntime { RawDecisionResponse = true, Respond = _ => new() { Json = Proposal() } };
        var state = await Advance(PlannerFixture.Session(), runtime);
        Assert.Equal(PlanningStatus.WaitingForDecision, state.Status); Assert.NotNull(state.WaitingSinceUtc);
        Assert.Same(state, await Advance(state, runtime)); Assert.Single(runtime.Calls);
        state = Restore(state);
        var answer = new PlanningDecisionAnswer(state.PendingDecision!.Id, custom ? null : "casual", custom ? "Use a warm professional tone" : null);
        state = await Advance(state, runtime, new() { Kind = "answer_decision", DecisionAnswer = answer });
        Assert.Null(state.SemanticPlan); Assert.Single(state.Decisions); Assert.Equal(1, state.ModelCalls);
        if (custom) runtime.Respond = request =>
        {
            Assert.Contains("Use a warm professional tone", request.Prompt);
            return new() { Json = new JsonObject { ["result"] = Proposal()["decision"]!["options"]![0]!["result"]!.DeepClone(), ["decision"] = null } };
        };
        state = await Advance(Restore(state), runtime);
        Assert.NotNull(state.SemanticPlan); Assert.Null(state.DecisionContinuation);
        Assert.Equal(custom ? 2 : 1, runtime.Calls.Count);
        Assert.Equal(custom ? "Return a formal greeting" : "Return a casual greeting", state.SemanticPlan.Actions[0].Purpose);
    }

    [Fact]
    public async Task InvalidAndStaleAnswersDoNotChangeWaitingSession()
    {
        var runtime = new TestRuntime { RawDecisionResponse = true, Respond = _ => new() { Json = Proposal() } };
        var state = await Advance(PlannerFixture.Session(), runtime);
        foreach (var answer in new[] { new PlanningDecisionAnswer(state.PendingDecision!.Id, "unknown"), new(state.PendingDecision.Id, "formal", "also text"), new(state.PendingDecision.Id, Text: " ") })
            await Assert.ThrowsAsync<ArgumentException>(() => Advance(state, runtime, new() { Kind = "answer_decision", DecisionAnswer = answer }));
        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(state, runtime, new() { Kind = "answer_decision", DecisionAnswer = new("old", "formal") }));
        state.Request.Prompt = "Changed intent";
        await Assert.ThrowsAsync<PlanningConflictException>(() => Advance(state, runtime, new() { Kind = "answer_decision", DecisionAnswer = new(state.PendingDecision.Id, "formal") }));
        Assert.Empty(state.Decisions); Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task SwitchingToAutoResolvesPendingAndCancellationRemainsTerminal()
    {
        var runtime = new TestRuntime { RawDecisionResponse = true, Respond = _ => new() { Json = Proposal() } };
        var state = await Advance(PlannerFixture.Session(), runtime);
        var cancelled = await Advance(state, runtime, new() { Kind = "cancel" });
        Assert.Equal(PlanningStatus.Cancelled, cancelled.Status); Assert.Empty(cancelled.Decisions);
        var automatic = await Advance(state, runtime, new() { Kind = "configure_mode", Mode = PlanningMode.Auto });
        Assert.Equal("auto", Assert.Single(automatic.Decisions).Source);
        Assert.Equal(PlanningStatus.Generating, automatic.Status);
    }

    [Fact]
    public async Task InvalidEvidenceOrPreferredFlagsNeverBecomeHumanQuestions()
    {
        foreach (var proposal in new[] { Proposal("unissued evidence"), Proposal() })
        {
            if (proposal["decision"]!["evidence"]!.ToString() != "unissued evidence") proposal["decision"]!["options"]![1]!["preferred"] = true;
            var state = await Advance(PlannerFixture.Session(), new() { RawDecisionResponse = true, Respond = _ => new() { Json = proposal } });
            Assert.Null(state.PendingDecision); Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_DECISION_INVALID");
        }
    }

    [Fact]
    public async Task SingleOptionIsDeterministicAndHostFailureNeverAsks()
    {
        var proposal = Proposal(); proposal["decision"]!["options"]!.AsArray().RemoveAt(1);
        var runtime = new TestRuntime { RawDecisionResponse = true, Respond = _ => new() { Json = proposal } };
        var state = await Advance(PlannerFixture.Session(), runtime);
        Assert.NotNull(state.SemanticPlan); Assert.Null(state.PendingDecision); Assert.Empty(state.Decisions);
        runtime.Respond = _ => throw new InvalidOperationException("host failure");
        state = await Advance(PlannerFixture.Session(), runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.PendingDecision);
    }
}
