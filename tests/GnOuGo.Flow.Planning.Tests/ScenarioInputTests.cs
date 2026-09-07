using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ScenarioInputTests
{
    [Fact]
    public void UnresolvedLoopComputationIsDiagnosedAtItsInputBeforeScenarioSetup()
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Steps.Add(new() { Key = "each", Type = "loop.sequential", Input = Obj(("items", new()
            { Kind = "compute", Text = "Array.isArray(value) ? value : []", Members = [new("value", Obj())] })) });
        var finding = Assert.Single(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "LOOP_ITEMS_CONTRACT_UNRESOLVED");
        Assert.Equal("/workflows/0/steps/1/input/members/0/value", finding.Location);
        Assert.Contains("typed array producer", finding.Message);
        workflow.Steps[1].Input = Obj(("items", new() { Kind = "array" }));
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "LOOP_ITEMS_CONTRACT_UNRESOLVED");
    }

    [Fact]
    public void LoopFixturesUseResolvedProducerItemSchemasWithoutChangingTheGraph()
    {
        var preparation = Preparation(); var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Steps.Add(new() { Key = "source", Type = "set", Input = Obj(("entries", new() { Kind = "array" })),
            OutputSchema = new() { Type = "object", Properties = [new() { Name = "entries", Required = true, Schema = new() { Type = "array", Items = new() { Type = "string" } } }] } });
        workflow.Steps.Add(new() { Key = "each", Type = "loop.sequential", Input = Obj(("items", new() { Kind = "output", Source = "source", Path = ["entries"] })) });
        var before = PlanningGraphCompiler.Fingerprint(graph);
        var schemas = TypedWorkflowPlanner.ScenarioLoopItemSchemas(graph, preparation);
        Assert.Equal("string", Assert.Single(schemas).Value!["type"]!.GetValue<string>());
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(graph));
    }

    [Fact]
    public void LaterScenarioFailureIsProgressOnlyWhenEveryPreviouslyPassedScenarioStillPasses()
    {
        static PlanningScenarioResult Case(string id, string outcome) => new(id, outcome, "Synthetic", []);
        var prior = new[] { Case("first", "passed"), Case("second", "inconclusive"), Case("third", "inconclusive") };
        Assert.True(TypedWorkflowPlanner.PreservesScenarioProgress(prior, [Case("first", "passed"), Case("second", "passed"), Case("third", "failed")]));
        Assert.False(TypedWorkflowPlanner.PreservesScenarioProgress(prior, [Case("first", "failed"), Case("second", "passed"), Case("third", "passed")]));
        Assert.False(TypedWorkflowPlanner.PreservesScenarioProgress(prior, [Case("first", "passed"), Case("second", "passed")]));
    }

    [Fact]
    public async Task NullDefaultsAndCachedNullsCannotBypassRequiredScenarioInputValidation()
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Graph.Workflows[0].Inputs.Add(new() { Name = "resource", Required = true, Schema = new() { Type = "string" }, Default = new() { Kind = "null" } });
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, _, _) =>
        {
            if (phase == "scenario_inputs") { calls++; return Task.FromResult(new LLMResponse { Json = new JsonObject { ["resource"] = new JsonObject { ["kind"] = "string", ["text"] = "provided" } } }); }
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray() } });
        } };
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, calls);
        state.ScenarioInputs!["resource"] = null; state.Status = PlanningStatus.Validating;
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, calls); Assert.Equal("provided", state.ScenarioInputs!["resource"]!.GetValue<string>());
        Assert.Equal("null", state.Graph!.Workflows[0].Inputs[0].Default!.Kind);
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
