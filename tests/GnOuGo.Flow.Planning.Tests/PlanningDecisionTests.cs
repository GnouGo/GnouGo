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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroundingDecisionRetainsCoverageAndAppliesSelectedCapabilities(bool auto)
    {
        var state = SemanticGroundingTests.State(); state.Request.Mode = auto ? PlanningMode.Auto : PlanningMode.Interactive;
        state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages)
            state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "matched", page.CapabilityIds.Select(id => new GroundingMatch(id, "Declared behavior")).ToList(), "Covered")).ToList()));
        var before = JsonSerializer.Serialize(state.Grounding.Results, PlanningJsonContext.Default.ListGroundingPageResult);
        var proposal = Proposal();
        for (var i = 0; i < 2; i++)
            proposal["decision"]!["options"]![i]!["result"] = new JsonObject { ["selections"] = new JsonObject(state.SemanticPlan!.Actions.Select(a => new KeyValuePair<string, JsonNode?>(a.Id,
                new JsonObject { ["capabilities"] = new JsonObject { ["cap_0"] = i == 0, ["cap_1"] = i == 1 }, ["reason"] = "Valid business strategy" }))) };
        var runtime = new TestRuntime { RawDecisionResponse = true, Respond = _ => new() { Json = proposal } };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningPhase.Grounding, state.Decisions.FirstOrDefault()?.Decision.Phase ?? state.PendingDecision!.Phase);
        if (!auto) state = await Advance(Restore(state), runtime, new() { Kind = "answer_decision", DecisionAnswer = new(state.PendingDecision!.Id, "casual") });
        state = await Advance(Restore(state), runtime);
        Assert.Equal(before, JsonSerializer.Serialize(state.Grounding!.Results, PlanningJsonContext.Default.ListGroundingPageResult));
        Assert.All(state.Grounding.Selections!, selection => Assert.Equal(new[] { auto ? "cap_0" : "cap_1" }, selection.CapabilityIds));
        Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BindingDecisionsRetainAcceptedWorkAcrossRestart(bool batch)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var first = PlannerFixture.Greeting("Hello"); first.Operations[0].SemanticAction = "greet";
        var second = PlannerFixture.Greeting("Welcome"); second.Operations[0].SemanticAction = "greet";
        state.SemanticPlan = GnOuGo.Planning.Examples.PlanningCorpus.Semantic(first);
        _ = GnOuGo.Planning.Examples.PlanningCorpus.Semantic(second);
        state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages) state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(id => new GroundingDecision(id, "none_of_the_above", [], "Native calculation")).ToList()));
        state.Grounding.Selections = [new(state.SemanticPlan.Actions[0].Id, [], "Native calculation")];
        if (batch) state.BindingProgress = new();
        var schema = PlanningSchemas.Grounded([]); var proposal = Proposal();
        proposal["decision"]!["options"]![0]!["result"] = PlanningJsonTransport.ModelGrounded(PlanningJsonTransport.Grounded(first), schema);
        proposal["decision"]!["options"]![1]!["result"] = PlanningJsonTransport.ModelGrounded(PlanningJsonTransport.Grounded(second), schema);
        runtime.RawDecisionResponse = true; runtime.Respond = _ => new() { Json = proposal };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Equal(PlanningStatus.WaitingForDecision, state.Status); Assert.Equal(PlanningPhase.Binding, state.PendingDecision!.Phase);
        state = await Advance(Restore(state), runtime, new() { Kind = "answer_decision", DecisionAnswer = new(state.PendingDecision.Id, "casual") });
        state = await Advance(Restore(state), runtime);
        Assert.Equal("Welcome", ((CalculateGroundedOperation)state.GroundedPlan!.Operations[0]).Value.Text);
        Assert.Single(runtime.Calls); Assert.Equal(PlanningStatus.FinalReview, state.Status);
    }

    [Fact]
    public async Task BusinessReplanDecisionResumesOnlyAffectedAction()
    {
        var state = SemanticGroundingTests.State(); state.Grounding = CapabilityGrounder.Create(state);
        foreach (var page in state.Grounding.Pages) state.Grounding.Results.Add(new(page.Id, page.ActionIds.Select(a => new GroundingDecision(a, "matched", [new("cap_0", "Declared")], "Covered")).ToList()));
        state.Diagnostics = [new("SEMANTIC_BINDING_BLOCKED", "/actions/collect", "Choose a supported business observation")];
        var original = JsonSerializer.Serialize(state.SemanticPlan!.Actions[1], PlanningJsonContext.Default.SemanticAction);
        var proposal = Proposal();
        for (var i = 0; i < 2; i++)
        {
            var action = JsonSerializer.SerializeToNode(state.SemanticPlan.Actions[0], PlanningJsonContext.Default.SemanticAction)!;
            action["purpose"] = i == 0 ? "Read a detailed observation" : "Read a concise observation";
            proposal["decision"]!["options"]![i]!["result"] = new JsonObject { ["actions"] = new JsonArray(action), ["questions"] = new JsonArray() };
        }
        var runtime = new TestRuntime { RawDecisionResponse = true, Respond = _ => new() { Json = proposal } };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningPhase.Replanning, state.PendingDecision!.Phase); Assert.Equal(new[] { "collect" }, state.PendingDecision.ActionIds);
        state = await Advance(Restore(state), runtime, new() { Kind = "answer_decision", DecisionAnswer = new(state.PendingDecision.Id, "formal") });
        state = await Advance(Restore(state), runtime);
        Assert.Equal("Read a detailed observation", state.SemanticPlan!.Actions[0].Purpose);
        Assert.Equal(original, JsonSerializer.Serialize(state.SemanticPlan.Actions[1], PlanningJsonContext.Default.SemanticAction));
        Assert.Single(runtime.Calls); Assert.Equal(1, state.ReplanAttempts);
    }

}
