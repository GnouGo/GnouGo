using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ScenarioInputTests
{
    [Fact]
    public void LaterScenarioFailureIsProgressOnlyWhenEveryPreviouslyPassedScenarioStillPasses()
    {
        static PlanningScenarioResult Case(string id, string outcome) => new(id, outcome, "Synthetic", []);
        var prior = new[] { Case("first", "passed"), Case("second", "inconclusive"), Case("third", "inconclusive") };
        Assert.True(TypedWorkflowPlanner.PreservesScenarioProgress(prior, [Case("first", "passed"), Case("second", "passed"), Case("third", "failed")]));
        Assert.False(TypedWorkflowPlanner.PreservesScenarioProgress(prior, [Case("first", "failed"), Case("second", "passed"), Case("third", "passed")]));
        Assert.False(TypedWorkflowPlanner.PreservesScenarioProgress(prior, [Case("first", "passed"), Case("second", "passed")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixtureRepairIsBoundedAndNeverChangesExecutableDefaults(bool exhausted)
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Graph.Workflows[0].Inputs.Add(new() { Name = "resource", Required = true, Schema = new() { Type = "string", Description = "Absolute resource URL" } });
        var initialGraph = PlanningGraphCompiler.Fingerprint(state.Graph);
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase == "semantic_review") return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray() } });
            Assert.Equal("scenario_inputs", phase); calls++;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["resource"] = calls == 1 || exhausted
                ? new JsonObject { ["kind"] = "boolean", ["boolean"] = true }
                : new JsonObject { ["kind"] = "string", ["text"] = "https://example.test/resources/42" } } });
        } };
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls); Assert.Equal(initialGraph, PlanningGraphCompiler.Fingerprint(state.Graph!));
        Assert.Null(state.Graph!.Workflows[0].Inputs[0].Default);
        Assert.Equal(exhausted ? PlanningStatus.Recovery : PlanningStatus.FinalReview, state.Status);
        if (exhausted) { Assert.Contains(state.Diagnostics, d => d.Code == "SCENARIO_INPUT_INVALID"); Assert.Null(state.ScenarioInputs); }
        else
        {
            Assert.Equal("https://example.test/resources/42", state.ScenarioInputs!["resource"]!.GetValue<string>());
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            state.Status = PlanningStatus.Validating;
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
            Assert.Equal(2, calls); Assert.Equal(PlanningStatus.FinalReview, state.Status);
        }
    }
}
