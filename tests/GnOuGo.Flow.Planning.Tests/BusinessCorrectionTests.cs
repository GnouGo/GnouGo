using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class BusinessCorrectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(0, 1, PlanningStatus.Stopped)]
    [InlineData(1, 1, PlanningStatus.FinalReview)]
    [InlineData(2, 2, PlanningStatus.FinalReview)]
    public async Task ZeroOneAndMultipleCapabilityDomainsRespectCallBounds(int count, int calls, string status)
    {
        var factory = Factory(count); var plan = new WorkflowIntentPlan { Operations = [new InvokeIntentOperation { Id = "read", Purpose = "Read a value" }] };
        var runtime = new TestRuntime(plan, factory); var state = PlannerFixture.Session(); state.Request.MaxRepairAttempts = 0;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == status, string.Join(";", state.Diagnostics.Select(d => d.Message))); Assert.Equal(calls, state.ModelCalls);
        if (count > 0)
        {
            var invoke = Assert.IsType<InvokeIntentOperation>(Assert.Single(state.IntentPlan!.Operations)); Assert.NotNull(invoke.Capability);
            var rebuilt = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog!); Assert.Empty(PlanningHoleEligibility.Find(rebuilt, state.Catalog!));
        }
        else Assert.Contains(state.Diagnostics, d => d.Code == "HOLE_UNRESOLVED");
    }
    [Fact]
    public async Task SingletonArgumentResolutionUpdatesIntentAndUsesNoModelChoice()
    {
        var factory = Factory(1, """{"mode":{"type":"string","enum":["only"]}}""", ["mode"]);
        var runtime = new TestRuntime(mcp: factory); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        runtime.Plans.Clear(); runtime.Plans.Enqueue(new() { Operations = [new InvokeIntentOperation { Id = "read", Capability = catalog.Capabilities[0].Id }] });
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        Assert.Single(runtime.Calls); var invoke = Assert.IsType<InvokeIntentOperation>(Assert.Single(state.IntentPlan!.Operations));
        Assert.Equal("only", Assert.Single(invoke.Arguments).Value.Text);
    }
    [Fact]
    public async Task LocalCorrectionTargetsExactArgumentAndRejectsUnissuedOrDuplicateEditsAtomically()
    {
        var runtime = new TestRuntime(mcp: Factory(1, """{"count":{"type":"number"}}""", ["count"]));
        var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "read", Capability = state.Catalog.Capabilities[0].Id, Arguments = [new("count", new() { Kind = "string", Text = "wrong" })] }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog); state.ModelCalls = 1;
        state.Diagnostics = [new("CAPABILITY_ARGUMENT_INVALID", "/workflows/0/steps/0/input/request/count", "Expected number.")];
        var target = Assert.Single(PlanningCorrections.Targets(state)); Assert.Equal("/operations/0/arguments/0/value", target.Path); Assert.Equal("value", target.Shape);
        var before = PlanningJsonTransport.Intent(state.IntentPlan).ToJsonString();
        Assert.Throws<PlanningResponseException>(() => PlanningCorrections.Apply(state, [target], JsonNode.Parse("""{"changes":[{"target":"unissued","replacement":{"kind":"number","number":3}}]}""")!));
        var valid = new JsonObject { ["target"] = target.Id, ["replacement"] = new JsonObject { ["kind"] = "number", ["number"] = 3 } };
        Assert.Throws<PlanningResponseException>(() => PlanningCorrections.Apply(state, [target], new JsonObject { ["changes"] = new JsonArray(valid.DeepClone(), valid.DeepClone()) }));
        Assert.Equal(before, PlanningJsonTransport.Intent(state.IntentPlan).ToJsonString());
        runtime.Respond = _ => new() { Json = new JsonObject { ["changes"] = new JsonArray(valid.DeepClone()) } };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.RepairAttempts); Assert.Equal(2, state.ModelCalls);
        Assert.DoesNotContain("currentIntent", runtime.Calls.Single().Prompt);
    }
    [Fact]
    public async Task RetrievalIsStableBoundedAndCannotRemoveFullCatalogChoices()
    {
        var runtime = new TestRuntime(mcp: Factory(80)); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        catalog.Capabilities[79].Description = "Distinctive astronomy measurement";
        var selected = PlanningCapabilityCards.Shortlist(catalog, "astronomy", 12000);
        Assert.Equal(24, selected.Count); Assert.Equal(catalog.Capabilities[79].Id, selected[0].Id); Assert.Empty(runtime.Calls);
        Assert.Equal(selected.Select(c => c.Id), PlanningCapabilityCards.Shortlist(catalog, "astronomy", 12000).Select(c => c.Id));
        var graph = PlanningGraphBuilder.Build(new() { Operations = [new InvokeIntentOperation { Id = "find", Purpose = "A retrieval miss" }] }, catalog);
        Assert.Equal(80, PlanningHoleEligibility.Choices(graph, catalog, Assert.Single(PlanningHoleEligibility.Find(graph, catalog))).Count);
    }
    [Fact]
    public async Task RetrievalMissChoiceIncludesBusinessFragmentAndRanksAllEligibleCards()
    {
        var runtime = new TestRuntime(mcp: Factory(80)); var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var initiallyHidden = state.Catalog.Capabilities[79]; initiallyHidden.Description = "Distinctive astronomy measurement";
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "measure", Purpose = "astronomy" }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        var hole = Assert.Single(PlanningHoleEligibility.Find(state.Graph, state.Catalog)); var choices = PlanningHoleEligibility.Choices(state.Graph, state.Catalog, hole);
        var prompt = PlanningModelCalls.ChoicePrompt(state, [(hole, choices)]);
        var context = JsonNode.Parse(prompt[prompt.IndexOf('\n')..])!;
        var field = context["fields"]![0]!;
        Assert.Equal("measure", field["operation"]!["id"]!.ToString()); Assert.Equal("astronomy", field["operation"]!["purpose"]!.ToString());
        Assert.Equal(80, field["choices"]!.AsArray().Count); Assert.Equal(initiallyHidden.Id, field["choices"]![0]!["capability"]!["id"]!.ToString());
        Assert.Equal(choices.Select(c => c.Id).Order(), field["choices"]!.AsArray().Select(c => c!["id"]!.ToString()).Order());
    }
    [Fact]
    public async Task ChangedHostPolicyInvalidatesCatalogBeforeApproval()
    {
        var engine = new WorkflowEngine(); var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        engine.PlanningPolicy = new() { MaxStepsTotal = 5 };
        Assert.Contains(await runtime.ValidateCatalogAsync(catalog, Ct), d => d.Code == "POLICY_CHANGED");
    }
    [Fact]
    public async Task MissingRequiredArgumentMapsToExistingBusinessContainerAfterConfirmationInsertion()
    {
        var runtime = new TestRuntime(mcp: Factory(1, """{"count":{"type":"number"}}""", ["count"])); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.Catalog.Capabilities[0].EffectKind = "write";
        state.IntentPlan = new() { Operations = [new CalculateIntentOperation { Id = "later", After = ["read"], Value = new() { Kind = "number", Number = 1 } }, new InvokeIntentOperation { Id = "read", Capability = state.Catalog.Capabilities[0].Id }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog); PlanningConfirmationGuards.Apply(state.Graph, state.Catalog);
        state.Diagnostics = [new("HOLE_UNRESOLVED", "/workflows/1/steps/0/input/members/0/value/members/0/value", "Missing count.")];
        var mapped = Assert.Single(PlanningDiagnosticLocations.ForIntent(state)); Assert.Equal("/operations/1/arguments", mapped.Location); Assert.Contains("Missing argument 'count'", mapped.Message);
        Assert.StartsWith("/workflows/1/", state.Diagnostics[0].Location);
    }
    [Fact]
    public async Task FixturesAreRequestedOnlyWhenSamplingFailsAndUseLiteralCorrections()
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        server.Tools.Add(new() { Name = "read", EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"identifier":{"type":"string","pattern":"^REAL-[0-9]+$"}},"required":["identifier"]}""") });
        factory.RegisterServer("fixture", server);
        var runtime = new TestRuntime(mcp: factory); var state = PlannerFixture.Session();
        var catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var plan = new WorkflowIntentPlan { Operations = [new InvokeIntentOperation { Id = "read", Capability = catalog.Capabilities[0].Id }] };
        runtime.Respond = request =>
        {
            if (request.StructuredOutputSchema!["properties"]?["changes"] is null) return new() { Json = PlanningJsonTransport.Intent(plan) };
            var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
            Assert.True(context["targets"]![0]!["path"]!.ToString() == "/fixtures/observations/main/read", request.Prompt);
            return new() { Json = new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["target"] = context["targets"]![0]!["id"]!.DeepClone(),
                ["replacement"] = JsonNode.Parse("""{"kind":"array","items":[{"kind":"object","members":[{"name":"identifier","value":{"kind":"string","text":"REAL-1"}}]}]}""") }) } };
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts);
        Assert.Equal("REAL-1", Assert.Single(state.Fixtures!.Observations).Responses[0]!["identifier"]!.ToString());
        Assert.DoesNotContain("fixtures", PlanningJsonTransport.Intent(state.IntentPlan!).ToJsonString());
    }
    [Fact]
    public async Task InvalidChoiceIdsCannotBecomeExecutableAndUseBoundedLocalRepair()
    {
        var runtime = new TestRuntime(mcp: Factory(2)); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        var plan = new WorkflowIntentPlan { Operations = [new InvokeIntentOperation { Id = "read" }] }; var calls = 0;
        runtime.Respond = request =>
        {
            calls++;
            if (calls == 1) return new() { Json = PlanningJsonTransport.Intent(plan) };
            if (calls == 2) return new() { Json = new JsonObject { [request.StructuredOutputSchema!["properties"]!.AsObject().First().Key] = "unissued" } };
            var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
            ((InvokeIntentOperation)plan.Operations[0]).Capability = catalog.Capabilities[0].Id;
            return new() { Json = new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["target"] = context["targets"]![0]!["id"]!.DeepClone(), ["replacement"] = PlanningJsonTransport.Intent(plan)["operations"]!.DeepClone() }) } };
        };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message))); Assert.Equal(3, calls); Assert.Equal(1, state.RepairAttempts);
    }
    [Fact]
    public async Task UnchangedLocalCorrectionStopsEarlyWithoutCreatingFixtureState()
    {
        var plan = PlannerFixture.Greeting(); ((CalculateIntentOperation)plan.Operations[0]).Value = new() { Kind = "compute", Text = "missingParameter" };
        var state = await PlannerFixture.RunAsync(new TestRuntime(plan));
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts); Assert.Null(state.Fixtures);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPAIR_NO_PROGRESS");
    }
    [Theory]
    [InlineData("compute")]
    [InlineData("input")]
    [InlineData("missing")]
    [InlineData("result")]
    public void FixtureCorrectionSchemaRejectsNonliteralValues(string kind)
    {
        var target = new PlanningCorrections.Target("issued", "/fixtures/inputs", "fixture_inputs", null);
        var response = new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["target"] = "issued", ["replacement"] = new JsonObject { ["kind"] = "object", ["members"] = new JsonArray(new JsonObject { ["name"] = "data", ["value"] = new JsonObject { ["kind"] = kind } }) } }) };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, PlanningCorrections.Schema([target])));
    }
    [Fact]
    public async Task NestedScopesMapRepeatedIdentifiersToTheirOwnBusinessBlocks()
    {
        var state = PlannerFixture.Session(); state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, Ct);
        state.IntentPlan = new() { Operations = [new ChooseIntentOperation { Id = "choose", Condition = new() { Kind = "boolean", Boolean = true },
            Then = new([new CalculateIntentOperation { Id = "same", Value = new() { Kind = "number", Number = 1 } }], new() { Kind = "result", Source = "same" }),
            Otherwise = new([new CalculateIntentOperation { Id = "same", Value = new() { Kind = "number", Number = 2 } }], new() { Kind = "result", Source = "same" }) }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        Assert.Empty(PlanningGraphBuilder.ValidateIntent(state.IntentPlan));
        state.Diagnostics = [new("COMPUTATION_INVALID", "/workflows/2/steps/0/input/members/0/value", "Invalid value."), new("SCHEMA_INVALID", "/workflows/1/outputs/0/schema", "Missing result contract.")];
        var mapped = PlanningDiagnosticLocations.ForIntent(state);
        Assert.Equal("/operations/0/otherwise/operations/0/value", mapped[0].Location);
        Assert.Equal("/operations/0/then/result", mapped[1].Location);
    }
    [Fact]
    public async Task IndependentComputationAndFixtureFailuresAreBothReported()
    {
        var runtime = new TestRuntime(mcp: Factory(1)); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.ModelCalls = 1; state.Request.MaxRepairAttempts = 0;
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "read", Capability = state.Catalog.Capabilities[0].Id }, new CalculateIntentOperation { Id = "invalid", Value = new() { Kind = "compute", Text = "undeclared" } }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Fixtures = new() { Observations = [new("main", "read", [new JsonObject { ["value"] = "invalid" }])] };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Empty(runtime.Calls);
        Assert.Contains(state.Diagnostics, d => d.Code == "COMPUTATION_BINDING_INVALID");
        Assert.Contains(state.Diagnostics, d => d.Code == "SCENARIO_FIXTURE_INVALID" && d.Location == "/fixtures/observations/main/read");
    }
    [Fact]
    public async Task HostConfirmationDefectsNeverConsumeModelRepairs()
    {
        var runtime = new TestRuntime(mcp: Factory(1)); var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.Catalog.Capabilities[0].EffectKind = "write"; state.ModelCalls = 1;
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "read", Capability = state.Catalog.Capabilities[0].Id }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog); PlanningConfirmationGuards.Apply(state.Graph, state.Catalog);
        state.Diagnostics = [new("BOOLEAN_CONDITION_INVALID", "/workflows/0/steps/1/input", "Invalid host guard.")];
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls); Assert.Equal(0, state.RepairAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "PLANNING_HOST_CONTRACT");
    }
    internal static InMemoryMcpClientFactory Factory(int count, string fields = "{}", string[]? required = null)
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        for (var i = 0; i < count; i++) server.Tools.Add(new() { Name = "read_" + i, Description = "Read a value", EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object", ["properties"] = JsonNode.Parse(fields), ["required"] = new JsonArray((required ?? []).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), ["additionalProperties"] = false }, OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"number"}},"required":["value"]}""") });
        factory.RegisterServer("fixture", server); return factory;
    }
}
