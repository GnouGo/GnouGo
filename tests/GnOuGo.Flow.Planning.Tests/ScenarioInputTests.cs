using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ScenarioInputTests
{
    [Fact]
    public async Task ScenarioOutputLimitPublishesTheActivePhaseAndPausesWithoutAnIdenticalRetry()
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Graph.Workflows[0].Inputs.Add(new() { Name = "resource", Required = true, Schema = new() { Type = "string" } });
        var graph = PlanningGraphCompiler.Fingerprint(state.Graph); var calls = 0; string? phase = null;
        var runtime = new FakeRuntime
        {
            OnCheckpoint = snapshot =>
        {
            phase = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!.CurrentPhase;
            return Task.CompletedTask;
        },
            OnCall = (currentPhase, _, _) =>
        {
            calls++; Assert.Equal("scenario_inputs", currentPhase); Assert.Equal(currentPhase, phase);
            return Task.FromResult(new LLMResponse { CompletionStatus = "output_limit", Json = new JsonObject { ["partial"] = "must not be accepted" } });
        }
        };
        PlanningFixtures.Accept(state);
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("scenario_inputs", state.CurrentPhase);
        Assert.Equal(graph, PlanningGraphCompiler.Fingerprint(state.Graph!)); Assert.Null(state.Validation.Inputs);
        Assert.Contains(state.Diagnostics, d => d.Code == "DECISION_OUTPUT_LIMIT");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservationFixturesRepairOnceAndSurviveRestartWithoutChangingTheGraph(bool exhausted)
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Preparation.AllowedStepTypes.Add("loop.sequential");
        state.Preparation.Capabilities.Add(new()
        {
            Id = "cap",
            StepType = "mcp.call",
            Server = "renamed",
            Method = "observe",
            Kind = "tool",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"more":{"type":"boolean"}},"required":["more"]}""")!.AsObject()
        });
        state.Graph.Workflows[0].Steps.Add(new()
        {
            Key = "pages",
            Type = "loop.sequential",
            Input = Obj(("while", new()
            {
                Kind = "compute",
                Text = "previous == null || previous.more",
                Members = [new("previous", new() { Kind = "loop_previous", Source = "pages", Path = ["observe", "response"] })]
            })),
            Steps = [new() { Key = "observe", Type = "mcp.call", CapabilityId = "cap", Input = Obj(("request", Obj())) }]
        });
        var fingerprint = PlanningGraphCompiler.Fingerprint(state.Graph); var calls = 0;
        var runtime = new FakeRuntime
        {
            OnCall = (phase, request, _) =>
        {
            if (phase == "semantic_review") return Task.FromResult(new LLMResponse { Json = FakeRuntime.PassReview(request) });
            Assert.StartsWith("scenario_observations", phase);
            if (request.StructuredOutputSchema!["properties"]!.AsObject().Any(p => p.Key.StartsWith("observation_count_", StringComparison.Ordinal)))
                return Task.FromResult(new LLMResponse { Json = Assignment(request, JsonValue.Create("2")) });
            calls++;
            var value = calls == 1 || exhausted ? (JsonNode?)JsonValue.Create("invalid") : JsonValue.Create(!request.Prompt.Contains("\"final\":true", StringComparison.Ordinal));
            return Task.FromResult(new LLMResponse { Json = Assignment(request, value) });
        }
        };
        PlanningFixtures.Accept(state);
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.True(calls == (exhausted ? 2 : 3), string.Join("; ", state.Diagnostics.Select(d => d.Code + ": " + d.Message))); Assert.Equal(fingerprint, PlanningGraphCompiler.Fingerprint(state.Graph!));
        Assert.Equal(exhausted ? PlanningStatus.Stopped : PlanningStatus.FinalReview, state.Status);
        if (exhausted) Assert.Contains(state.Diagnostics, d => d.Code == "DECISION_CORRECTION_INVALID");
        else
        {
            Assert.Single(state.Validation.Observations);
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            state.Status = PlanningStatus.Validating;
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
            Assert.Equal(3, calls); Assert.Equal(PlanningStatus.FinalReview, state.Status);
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("otherwise")]
    [InlineData("défaut")]
    public void ErrorHandlerLabelsAreNotBooleanConditions(string label)
    {
        var graph = Graph(); var preparation = Preparation(); var node = graph.Workflows[0].Steps[0];
        node.OnError = [new(Str(label), "stop", null, null)];
        var finding = Assert.Single(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "BOOLEAN_CONDITION_INVALID");
        Assert.Equal("/workflows/0/steps/0/onError/0/if", finding.Location);
        node.OnError = [new(null, "stop", null, null)];
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "BOOLEAN_CONDITION_INVALID");
    }

    [Fact]
    public void UnresolvedLoopComputationIsDiagnosedAtItsInputBeforeScenarioSetup()
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Steps.Add(new()
        {
            Key = "each",
            Type = "loop.sequential",
            Input = Obj(("items", new()
            { Kind = "compute", Text = "Array.isArray(value) ? value : []", Members = [new("value", Obj())] }))
        });
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
        workflow.Steps.Add(new()
        {
            Key = "source",
            Type = "set",
            Input = Obj(("entries", new() { Kind = "array" })),
            OutputSchema = new() { Type = "object", Properties = [new() { Name = "entries", Required = true, Schema = new() { Type = "array", Items = new() { Type = "string" } } }] }
        });
        workflow.Steps.Add(new() { Key = "each", Type = "loop.sequential", Input = Obj(("items", new() { Kind = "output", Source = "source", Path = ["entries"] })) });
        var before = PlanningGraphCompiler.Fingerprint(graph);
        var schemas = PlanningScenarioFixtures.ScenarioLoopItemSchemas(graph, preparation);
        Assert.Equal("string", Assert.Single(schemas).Value!["type"]!.GetValue<string>());
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(graph));
    }

    [Fact]
    public async Task NullDefaultsAndCachedNullsCannotBypassRequiredScenarioInputValidation()
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Graph.Workflows[0].Inputs.Add(new() { Name = "resource", Required = true, Schema = new() { Type = "string" }, Default = new() { Kind = "null" } });
        var calls = 0;
        var runtime = new FakeRuntime
        {
            OnCall = (phase, request, _) =>
        {
            if (phase == "scenario_inputs") { calls++; return Task.FromResult(new LLMResponse { Json = Assignment(request, JsonValue.Create("provided")) }); }
            return Task.FromResult(new LLMResponse { Json = FakeRuntime.PassReview(request) });
        }
        };
        PlanningFixtures.Accept(state);
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, calls);
        state.Validation.Inputs!["resource"] = null; state.Status = PlanningStatus.Validating;
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningPhase.Repair, state.CurrentPhase); Assert.Equal(1, calls); Assert.Null(state.Validation.Inputs!["resource"]);
        Assert.Contains(state.Diagnostics, d => d.Code == "SCENARIO_FIXTURE_CONTRACT_CHANGED");
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
        var runtime = new FakeRuntime
        {
            OnCall = (phase, request, _) =>
        {
            if (phase == "semantic_review") return Task.FromResult(new LLMResponse { Json = FakeRuntime.PassReview(request) });
            Assert.StartsWith("scenario_inputs", phase); calls++;
            return Task.FromResult(new LLMResponse
            {
                Json = Assignment(request, calls == 1 || exhausted ? JsonValue.Create(true) : JsonValue.Create("https://example.test/resources/42"))
            });
        }
        };
        PlanningFixtures.Accept(state);
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls); Assert.Equal(initialGraph, PlanningGraphCompiler.Fingerprint(state.Graph!));
        Assert.Null(state.Graph!.Workflows[0].Inputs[0].Default);
        Assert.Equal(exhausted ? PlanningStatus.Stopped : PlanningStatus.FinalReview, state.Status);
        if (exhausted) { Assert.Contains(state.Diagnostics, d => d.Code == "DECISION_CORRECTION_INVALID"); Assert.Null(state.Validation.Inputs); }
        else
        {
            Assert.Equal("https://example.test/resources/42", state.Validation.Inputs!["resource"]!.GetValue<string>());
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            state.Status = PlanningStatus.Validating;
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
            Assert.Equal(2, calls); Assert.Equal(PlanningStatus.FinalReview, state.Status);
        }
    }
    private static JsonObject Assignment(LLMRequest request, JsonNode? value) => new(request.StructuredOutputSchema!["properties"]!.AsObject()
        .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, value?.DeepClone())));
}
