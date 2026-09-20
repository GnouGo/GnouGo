using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class DefaultResponseSchemaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("missing")]
    [InlineData("input")]
    [InlineData("result")]
    [InlineData("item")]
    [InlineData("index")]
    [InlineData("compute")]
    [InlineData("template")]
    public void InterpretationAndRepairDefaultsRejectExecutableValuesRecursively(string kind)
    {
        var value = new IntentValue { Kind = kind };
        if (kind is "input" or "result" or "item" or "index") value.Source = "source";
        if (kind is "compute" or "template") value.Text = "1";
        foreach (var nested in new[] { false, true })
        {
            var candidate = nested ? new IntentValue { Kind = "object", Members = [new("items", new() { Kind = "array", Items = [value] })] } : value;
            var plan = Plan(null, candidate); var json = PlanningJsonTransport.Intent(plan);
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()));
            var target = new PlanningCorrections.Target("issued", "/inputs/0", "input", json["inputs"]![0]);
            var response = Replacement(json["inputs"]![0]!);
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(response, PlanningCorrections.Schema([target])));
            var subflow = new WorkflowIntentPlan { Subflows = [new("worker", plan.Inputs, plan.Operations, plan.Outputs)] };
            Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Intent(subflow), PlanningSchemas.Intent()));
        }
    }

    [Theory]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("integer")]
    [InlineData("boolean")]
    [InlineData("array")]
    [InlineData("object")]
    public void ExplicitNullDefaultRequiresNullableDeclarationInBothResponseShapes(string type)
    {
        var declaration = new IntentType { Type = type, Items = type == "array" ? new() { Type = "number" } : null,
            Fields = type == "object" ? [new("name", new() { Type = "string" })] : [] };
        foreach (var nullable in new[] { false, true })
        {
            declaration.Nullable = nullable;
            var json = PlanningJsonTransport.Intent(Plan(declaration, new() { Kind = "null" }));
            Assert.Equal(nullable, PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()).Count == 0);
            var target = new PlanningCorrections.Target("issued", "/inputs/0", "input", json["inputs"]![0]);
            Assert.Equal(nullable, PlanningContractValidation.ValidateInstanceFindings(Replacement(json["inputs"]![0]!), PlanningCorrections.Schema([target])).Count == 0);
        }
    }

    [Fact]
    public void LiteralAndAbsentDefaultsRoundTripWithoutChangingTheirMeaning()
    {
        foreach (var value in new IntentValue?[] { null, new() { Kind = "null" }, new() { Kind = "string", Text = "kept" },
            new() { Kind = "number", Number = 2 }, new() { Kind = "boolean", Boolean = false },
            new() { Kind = "object", Members = [new("values", new() { Kind = "array", Items = [new() { Kind = "null" }, new() { Kind = "number", Number = 3 }] })] } })
        {
            var json = PlanningJsonTransport.Intent(Plan(null, value));
            Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()));
            var roundTrip = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowIntentPlan)!;
            Assert.True(JsonNode.DeepEquals(json, PlanningJsonTransport.Intent(roundTrip)));
            Assert.Equal(value?.Kind, roundTrip.Inputs[0].Default?.Kind);
        }
        Assert.Empty(PlanningContractValidation.ValidateSchema(PlanningSchemas.Intent(), strict: true));
        Assert.Empty(PlanningContractValidation.ValidateSchema(PlanningCorrections.Schema([new("issued", "/inputs/0", "input", null)]), strict: true));
        Assert.Empty(PlanningContractValidation.ValidateSchema(PlanningCorrections.Schema([new("issued", "/fixtures/inputs", "fixture_inputs", null)]), strict: true));
    }

    [Fact]
    public async Task NewMalformedDefaultResponseIsRejectedWithoutRewritingAndUsesBoundedRepair()
    {
        var invalid = Plan(new() { Type = "string" }, new() { Kind = "missing" }); var original = PlanningJsonTransport.Intent(invalid);
        var runtime = new TestRuntime(invalid); runtime.Plans.Enqueue(Plan(new() { Type = "string" }, null));
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.RepairAttempts);
        Assert.True(JsonNode.DeepEquals(original, PlanningJsonTransport.Intent(invalid)));
        Assert.Contains(runtime.Checkpoints, s => s.Diagnostics.Any(d => d.Code == "INTENT_SCHEMA_INVALID" && d.Location == "/inputs/0"));
        Assert.Null(state.IntentPlan!.Inputs[0].Default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InferredContractRetainsAuthorityOverExplicitNull(bool nullable)
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        var type = nullable ? (JsonNode)new JsonArray("string", "null") : JsonValue.Create("string");
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["payload"] = new JsonObject { ["type"] = type } }, ["required"] = new JsonArray("payload") };
        server.Tools.Add(new() { Name = "accept", EffectKind = "read", InputSchema = schema, OutputSchema = schema.DeepClone(), ExampleResponse = new JsonObject { ["payload"] = "sample" } });
        server.ToolHandlers["accept"] = args => new() { Content = args?.DeepClone() }; factory.RegisterServer("fixture", server);
        var runtime = new TestRuntime(mcp: factory); var state = PlannerFixture.Session(); state.Request.MaxRepairAttempts = 0;
        var catalog = await runtime.DiscoverAsync(state.Request, Ct);
        runtime.Plans.Clear(); runtime.Plans.Enqueue(new() { Inputs = [new("payload", Default: new() { Kind = "null" })],
            Operations = [new InvokeIntentOperation { Id = "accept", Capability = catalog.Capabilities[0].Id, Arguments = [new("payload", new() { Kind = "input", Source = "payload" })] }] });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(nullable ? PlanningStatus.FinalReview : PlanningStatus.Stopped, state.Status);
        Assert.Null(state.IntentPlan!.Inputs[0].Type); Assert.Equal("null", state.IntentPlan.Inputs[0].Default!.Kind);
        if (!nullable) Assert.Contains(state.Diagnostics, d => d.Code == "INPUT_DEFAULT_INVALID");
    }

    [Fact]
    public async Task InvalidDefaultCorrectionLeavesWholeIntentAndFixturesUnchanged()
    {
        var state = PlannerFixture.Session(); state.IntentPlan = Plan(new() { Type = "string" }, null);
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, Ct); state.Graph = PlanningGraphBuilder.Build(state.IntentPlan, state.Catalog);
        var before = PlanningJsonTransport.Intent(state.IntentPlan); var graph = PlanningGraphCompiler.Fingerprint(state.Graph);
        var replacement = PlanningJsonTransport.Intent(Plan(new() { Type = "string" }, new() { Kind = "missing" }))["inputs"]![0]!;
        Assert.Throws<PlanningResponseException>(() => PlanningCorrections.Apply(state, [new("issued", "/inputs/0", "input", before["inputs"]![0])], Replacement(replacement)));
        Assert.True(JsonNode.DeepEquals(before, PlanningJsonTransport.Intent(state.IntentPlan))); Assert.Equal(graph, PlanningGraphCompiler.Fingerprint(state.Graph)); Assert.Null(state.Fixtures);
    }

    [Fact]
    public async Task RestartReplaysOriginalReservedSchemaBeforeApplyingCurrentGraphValidation()
    {
        var invalid = Plan(new() { Type = "string" }, new() { Kind = "missing" }); var runtime = new TestRuntime(invalid);
        var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.ModelCalls = 4; state.RepairAttempts = 1;
        var originalSchema = JsonNode.Parse("""{"type":"object"}""")!.AsObject();
        state.PendingCall = new() { Id = "reserved", Purpose = "intent", Request = new() { ClientRequestId = "reserved", Prompt = "Original request", StructuredOutputSchema = originalSchema } };
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.True(JsonNode.DeepEquals(originalSchema, Assert.Single(runtime.Calls).StructuredOutputSchema));
        Assert.Equal(4, state.ModelCalls); Assert.Equal(1, state.RepairAttempts); Assert.Null(state.PendingCall);
        Assert.NotNull(state.IntentPlan); Assert.Contains(state.Diagnostics, d => d.Code == "HOLE_UNRESOLVED" && d.Location == "/workflows/0/inputs/0/default");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "INTENT_SCHEMA_INVALID");
    }

    private static WorkflowIntentPlan Plan(IntentType? type, IntentValue? value) => new()
    {
        Inputs = [new("payload", type, Default: value)],
        Operations = [new CalculateIntentOperation { Id = "echo", Value = new() { Kind = "input", Source = "payload" } }],
        Outputs = [new("value", new() { Kind = "result", Source = "echo" })]
    };
    private static JsonObject Replacement(JsonNode value) => new() { ["changes"] = new JsonArray(new JsonObject { ["target"] = "issued", ["replacement"] = value.DeepClone() }) };
}
