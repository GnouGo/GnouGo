using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ScenarioInputTests
{
    [Theory]
    [InlineData("accepted", "rejected")]
    [InlineData("autorisé", "refusé")]
    public void ConfirmationBooleanMustBeMappedToTheAcceptedDecisionLabels(string accepted, string rejected)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Steps.Add(new() { Key = "consent", Type = "human.input", Input = PlanningConstruction.Literal(HumanInputContract.ConfirmationInput("Allow the action?")) });
        var route = new PlanningNode { Key = "route", Type = "switch", Expr = new() { Kind = "output", Source = "consent", Path = ["response"] },
            Cases = [new(accepted, null, []), new(rejected, null, [])] }; workflow.Steps.Add(route);
        var finding = Assert.Single(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "SWITCH_OUTCOME_UNREACHABLE");
        Assert.Equal("/workflows/0/steps/2/expr", finding.Location);
        route.Expr = new() { Kind = "compute", Text = "response === true ? allowed : denied", Members =
            [new("response", new() { Kind = "output", Source = "consent", Path = ["response"] }), new("allowed", Str(accepted)), new("denied", Str(rejected))] };
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "SWITCH_OUTCOME_UNREACHABLE");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObservationFixturesRepairOnceAndSurviveRestartWithoutChangingTheGraph(bool exhausted)
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Preparation.AllowedStepTypes.Add("loop.sequential");
        state.Preparation.Capabilities.Add(new() { Id = "cap", StepType = "mcp.call", Server = "renamed", Method = "observe", Kind = "tool",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}""")!.AsObject(),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"more":{"type":"boolean"}},"required":["more"]}""")!.AsObject() });
        state.Graph.Workflows[0].Steps.Add(new() { Key = "pages", Type = "loop.sequential", Input = Obj(("while", new() { Kind = "boolean", Boolean = true })),
            Steps = [new() { Key = "observe", Type = "mcp.call", CapabilityId = "cap", Input = Obj(("request", Obj())) }] });
        var fingerprint = PlanningGraphCompiler.Fingerprint(state.Graph); var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, _, _) =>
        {
            if (phase == "semantic_review") return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray() } });
            Assert.Equal("scenario_observations", phase); calls++;
            JsonNode Literal(bool value) => PlanningModelValues.Compact(JsonSerializer.SerializeToNode(calls == 1 || exhausted
                ? new PlanningValue { Kind = "boolean", Boolean = value }
                : Obj(("response", Obj(("more", new() { Kind = "boolean", Boolean = value })))), PlanningJsonContext.Default.PlanningValue))!;
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["responses"] = new JsonArray(Literal(true), Literal(false)) } });
        } };
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls); Assert.Equal(fingerprint, PlanningGraphCompiler.Fingerprint(state.Graph!));
        Assert.Equal(exhausted ? PlanningStatus.Recovery : PlanningStatus.FinalReview, state.Status);
        if (exhausted) Assert.Contains(state.Diagnostics, d => d.Code == "SCENARIO_OBSERVATION_INVALID");
        else
        {
            Assert.Single(state.ScenarioObservations);
            state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
            state.Status = PlanningStatus.Validating;
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
            Assert.Equal(2, calls); Assert.Equal(PlanningStatus.FinalReview, state.Status);
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
        workflow.Steps.Add(new() { Key = "each", Type = "loop.sequential", Input = Obj(("items", new()
            { Kind = "compute", Text = "Array.isArray(value) ? value : []", Members = [new("value", Obj())] })) });
        var finding = Assert.Single(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "LOOP_ITEMS_CONTRACT_UNRESOLVED");
        Assert.Equal("/workflows/0/steps/1/input/members/0/value", finding.Location);
        Assert.Contains("typed array producer", finding.Message);
        workflow.Steps[1].Input = Obj(("items", new() { Kind = "array" }));
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(graph, preparation), d => d.Code == "LOOP_ITEMS_CONTRACT_UNRESOLVED");
    }

    [Fact]
    public void LoopItemRepairOffersOnlyAvailableBindingsWithDeclaredArrayItems()
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0];
        workflow.Inputs.Add(new() { Name = "entries", Schema = new() { Type = "array", Items = new() { Type = "string" } } });
        var node = new PlanningNode { Key = "each", Type = "loop.sequential", Input = Obj(("items", new()
            { Kind = "compute", Text = "[]", Members = [] })) }; workflow.Steps.Add(node);
        var unit = new PlanningConstructionUnit { Key = "unit", Kind = "implementation", WorkflowKey = workflow.Key, NodeKeys = [node.Key], ContractVersion = PlanningDataflow.ContractVersion };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        unit.Diagnostics = PlanningExecutableValidation.Validate(graph, preparation).Where(d => d.Code == "LOOP_ITEMS_CONTRACT_UNRESOLVED").ToList();
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation);
        var field = Assert.Single(patch.Schema["properties"]!["changes"]!["properties"]!.AsObject()); var shape = field.Value!;
        Assert.Equal("binding", shape["properties"]!["kind"]!["enum"]![0]!.GetValue<string>());
        var allowed = Assert.Single(shape["properties"]!["reference"]!["enum"]!.AsArray())!.GetValue<string>();
        Assert.Equal("entries", PlanningDataflow.Index(workflow, preparation, graph, node.Key)[allowed].Value.Source);
        var response = new JsonObject { ["changes"] = new JsonObject { [field.Key] = new JsonObject { ["kind"] = "binding", ["reference"] = allowed } }, ["remove"] = new JsonArray() };
        var repaired = PlanningConstruction.Apply(graph, unit, patch.Apply(unit.Candidate, response), preparation);
        Assert.DoesNotContain(PlanningExecutableValidation.Validate(repaired, preparation), d => d.Code == "LOOP_ITEMS_CONTRACT_UNRESOLVED");
        Assert.Equal("string", Assert.Single(TypedWorkflowPlanner.ScenarioLoopItemSchemas(repaired, preparation)).Value!["type"]!.GetValue<string>());
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
