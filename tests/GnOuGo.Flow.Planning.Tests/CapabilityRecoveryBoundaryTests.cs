using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CapabilityRecoveryBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task UnspecifiedCleanupNeverSelectsStructurallyCompatibleReaders(int count)
    {
        var plan = new WorkflowIntentPlan { Operations = [new CleanupIntentOperation { Id = "release",
            Operations = [new InvokeIntentOperation { Id = "discard", Purpose = "Remove the temporary sample container",
                Arguments = [new("path", new() { Kind = "string", Text = "sample" })] }] }] };
        var runtime = new TestRuntime(plan, BusinessCorrectionTests.Factory(count, """{"path":{"type":"string"}}""", ["path"]));
        var state = PlannerFixture.Session(); state.Request.MaxRepairAttempts = 0;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Single(runtime.Calls);
        Assert.Null(Assert.IsType<InvokeIntentOperation>(Assert.IsType<CleanupIntentOperation>(state.IntentPlan!.Operations[0]).Operations[0]).Capability);
        var target = Assert.Single(PlanningCorrections.Targets(state));
        Assert.Equal("/operations/0/operations/0", target.Path); Assert.Equal("operation", target.Shape);
        Assert.Null(state.Yaml);
    }

    [Fact]
    public async Task UnspecifiedCapabilityUsesOperationCorrectionAndReachesReview()
    {
        var plan = new WorkflowIntentPlan { Operations = [new InvokeIntentOperation { Id = "observe", Purpose = "Observe a value" }] };
        var runtime = new TestRuntime(plan, BusinessCorrectionTests.Factory(2));
        var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        runtime.Plans.Enqueue(new() { Operations = [new InvokeIntentOperation { Id = "observe", Capability = catalog.Capabilities[0].Id }] });
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts);
        var correction = runtime.Calls[1];
        Assert.NotNull(correction.StructuredOutputSchema!["properties"]?["changes"]);
        var context = JsonNode.Parse(correction.Prompt[correction.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
        Assert.Equal("/operations/0", Assert.Single(context["targets"]!.AsArray())!["path"]!.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuedInterpretationAndRepairSchemasRejectUnknownNestedCapabilities(bool repair)
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(2));
        var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var plan = new WorkflowIntentPlan { Operations = [new CleanupIntentOperation { Id = "release",
            Operations = [new InvokeIntentOperation { Id = "observe" }] }] };
        if (repair)
        {
            state.IntentPlan = plan; state.Graph = PlanningGraphBuilder.Build(plan, state.Catalog); state.ModelCalls = 1;
            state.Diagnostics = [new("CAPABILITY_UNKNOWN", "/operations/0", "Invalid operation.", ValidationStage: "intent")];
        }
        runtime.Respond = _ => new() { Json = PlanningJsonTransport.Intent(PlannerFixture.Greeting()) };
        await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        var request = Assert.Single(runtime.Calls);
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!.AsObject(), strict: true));
        foreach (var id in new string?[] { null, state.Catalog.Capabilities[0].Id, state.Catalog.Capabilities[0].Method, "unissued" })
        {
            ((InvokeIntentOperation)((CleanupIntentOperation)plan.Operations[0]).Operations[0]).Capability = id;
            var json = PlanningJsonTransport.Intent(plan);
            JsonNode response = json;
            if (repair)
            {
                var context = JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!;
                response = new JsonObject { ["changes"] = new JsonArray(new JsonObject {
                    ["target"] = context["targets"]![0]!["id"]!.DeepClone(), ["replacement"] = json["operations"]![0]!.DeepClone() }) };
            }
            Assert.Equal(id is null || id == state.Catalog.Capabilities[0].Id,
                PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema.AsObject()).Count == 0);
        }
    }

    [Fact]
    public async Task InterpretationIncludesOnlyExposedAndValidRevisionIdsAcrossSubflows()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(40));
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var exposed = PlanningCapabilityCards.Shortlist(state.Catalog, state.Request.Prompt, 12_000).Select(c => c.Id).ToHashSet();
        var hidden = state.Catalog.Capabilities.Where(c => !exposed.Contains(c.Id)).ToArray();
        var invoke = new InvokeIntentOperation { Id = "observe", Capability = hidden[0].Id };
        var plan = new WorkflowIntentPlan { Subflows = [new("worker", [], [invoke], [])] };
        state.Request.Baseline = plan;
        var schema = PlanningModelCalls.IntentSchema(state);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Intent(plan), schema));
        invoke.Capability = hidden[1].Id;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Intent(plan), schema));
        state.Catalog.Policy.DeniedCapabilityIds.Add(hidden[1].Id);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Intent(plan), PlanningModelCalls.IntentSchema(state)));
        invoke.Capability = null;
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Intent(plan), schema));
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task CorrectionAllowsFullCatalogButDeferredUnknownIdsRemainBlocking()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(40));
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "first", Capability = "method_name" },
            new InvokeIntentOperation { Id = "deferred", Capability = "another_method_name" }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Diagnostics = PlanningExecutableValidation.Validate(state.Graph, state.Catalog).ToList();
        var target = PlanningCorrections.Targets(state).Single(t => t.Path == "/operations/0");
        var json = PlanningJsonTransport.Intent(state.IntentPlan); var replacement = json["operations"]![0]!.DeepClone();
        var hidden = state.Catalog.Capabilities.First(c => !PlanningCapabilityCards.Shortlist(state.Catalog, state.Request.Prompt, 12_000).Any(s => s.Id == c.Id));
        replacement["capability"] = hidden.Id;
        var response = new JsonObject { ["changes"] = new JsonArray(new JsonObject { ["target"] = target.Id, ["replacement"] = replacement }) };
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, PlanningCorrections.Schema([target], state.Catalog)));
        var corrected = PlanningCorrections.Apply(state, [target], response);
        Assert.Equal("method_name", ((InvokeIntentOperation)state.IntentPlan.Operations[0]).Capability);
        Assert.Equal(hidden.Id, ((InvokeIntentOperation)corrected.Operations[0]).Capability);
        Assert.Equal("another_method_name", ((InvokeIntentOperation)corrected.Operations[1]).Capability);
        Assert.Contains(PlanningExecutableValidation.Validate(PlanningGraphBuilder.Build(corrected, state.Catalog), state.Catalog), d => d.Code == "CAPABILITY_UNKNOWN");
        replacement["capability"] = hidden.Method;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, PlanningCorrections.Schema([target], state.Catalog)));
    }

    [Fact]
    public async Task ReservedCapabilityChoiceResumesWithOriginalSchemaIdentityAndAccounting()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(2));
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "observe" }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        var hole = Assert.Single(PlanningHoleEligibility.Find(state.Graph, state.Catalog));
        var choices = PlanningHoleEligibility.Choices(state.Graph, state.Catalog, hole);
        var schema = PlanningSchemas.Choices([(hole, choices)]);
        var prompt = PlanningModelCalls.ChoicePrompt(state, [(hole, choices)]);
        state.PendingCall = new() { Id = "reserved-choice", Purpose = "choices", Request = new() {
            ClientRequestId = "reserved-choice", Prompt = prompt, StructuredOutputSchema = schema, MaxTokens = 4096 } };
        state.ModelCalls = 4; state.RepairAttempts = 1;
        state.Usage = new() { Calls = 4, InputTokens = 120, OutputTokens = 30, TotalTokens = 150, EstimatedCost = 0.4m };
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var request = Assert.Single(runtime.Calls); Assert.Equal("reserved-choice", request.ClientRequestId);
        Assert.Equal(prompt, request.Prompt); Assert.Equal(4096, request.MaxTokens); Assert.True(JsonNode.DeepEquals(schema, request.StructuredOutputSchema));
        Assert.Equal(4, state.ModelCalls); Assert.Equal(1, state.RepairAttempts); Assert.Null(state.PendingCall);
        Assert.Equal(150, state.Usage!.TotalTokens); Assert.Equal(0.4m, state.Usage.EstimatedCost);
    }

    [Fact]
    public async Task ReservedLooseOperationRepairReplaysWithoutApplyingNewEnumRetroactively()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(1));
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "observe" }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        state.Diagnostics = [new("HOLE_UNRESOLVED", "/workflows/0/steps/0/capabilityId", "Unresolved.")];
        var targets = PlanningCorrections.Targets(state); var schema = PlanningCorrections.Schema(targets);
        state.PendingCall = new() { Id = "old-repair", Purpose = "repair", Request = new() { ClientRequestId = "old-repair",
            Prompt = PlanningCorrections.Prompt(state, targets), StructuredOutputSchema = schema, MaxTokens = 4096 } };
        state.ModelCalls = 2; state.RepairAttempts = 1;
        runtime.Plans.Clear(); runtime.Plans.Enqueue(new() { Operations = [new InvokeIntentOperation { Id = "observe", Capability = "read_0" }] });
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(JsonNode.DeepEquals(schema, Assert.Single(runtime.Calls).StructuredOutputSchema));
        Assert.Equal("read_0", ((InvokeIntentOperation)state.IntentPlan!.Operations[0]).Capability);
        Assert.Contains(state.Diagnostics, d => d.Code == "CAPABILITY_UNKNOWN");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "INTENT_SCHEMA_INVALID");
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts); Assert.Null(state.PendingCall); Assert.Null(state.Yaml);
    }

    [Fact]
    public async Task CapabilityEnumBytesCountTowardIndivisibleRepairLimitBeforeReservation()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(40));
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        foreach (var capability in state.Catalog.Capabilities) capability.Id += new string('x', 1500);
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "observe" }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog); state.ModelCalls = 1;
        state.Diagnostics = [new("HOLE_UNRESOLVED", "/workflows/0/steps/0/capabilityId", "Unresolved.")];
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls); Assert.Equal(0, state.RepairAttempts); Assert.Equal(1, state.ModelCalls);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT" && d.Message.Contains("/operations/0", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrUnchangedCapabilityCorrectionsStopWithinTwoAttempts(bool invalid)
    {
        var plan = new WorkflowIntentPlan { Operations = [new InvokeIntentOperation { Id = "observe" }] };
        var runtime = new TestRuntime(plan, BusinessCorrectionTests.Factory(2));
        if (invalid) runtime.Plans.Enqueue(new() { Operations = [new InvokeIntentOperation { Id = "observe", Capability = "read_0" }] });
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(invalid ? 2 : 1, state.RepairAttempts); Assert.Equal(1 + state.RepairAttempts, state.ModelCalls);
        Assert.Null(((InvokeIntentOperation)state.IntentPlan!.Operations[0]).Capability);
    }

    [Fact]
    public async Task ResolveValueSingletonsThenRepairCapabilitiesBeforeAmbiguousBindings()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(2));
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        foreach (var (capability, index) in state.Catalog.Capabilities.Select((c, i) => (c, i)))
            capability.InputSchema = JsonNode.Parse(index == 0
                ? """{"type":"object","properties":{"mode":{"type":"string","enum":["one"]}},"required":["mode"]}"""
                : """{"type":"object","properties":{"mode":{"type":"string","enum":["one","two"]}},"required":["mode"]}""")!.AsObject();
        state.IntentPlan = new() { Operations = [new InvokeIntentOperation { Id = "singleton", Capability = state.Catalog.Capabilities[0].Id },
            new InvokeIntentOperation { Id = "ambiguous", Capability = state.Catalog.Capabilities[1].Id }, new InvokeIntentOperation { Id = "unresolved" }] };
        state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog); state.ModelCalls = 1;
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Empty(runtime.Calls);
        Assert.Equal("one", ((InvokeIntentOperation)state.IntentPlan!.Operations[0]).Arguments[0].Value.Text);
        Assert.Contains(state.Diagnostics, d => d.Location.EndsWith("/capabilityId", StringComparison.Ordinal));
        runtime.Respond = _ => new() { Json = new JsonObject() };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(runtime.Calls.Count == 1, string.Join(";", state.Diagnostics.Select(d => d.ToString())));
        Assert.NotNull(Assert.Single(runtime.Calls).StructuredOutputSchema!["properties"]?["changes"]);
    }
}
