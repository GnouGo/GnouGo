using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class IntentBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public void CanonicalIntentPreservesNullMissingAndAbsentDefaults()
    {
        var intent = PlannerFixture.Greeting();
        intent.Inputs = [new("explicit", new() { Nullable = true }, true, new()), new("omitted", new(), true)];
        intent.Operations.Add(new InvokeIntentOperation { Id = "missing", Arguments = [new("value", new() { Kind = "missing" })] });
        var json = PlanningJsonTransport.Intent(intent);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()));
        Assert.Equal(new[] { "kind", "text" }, json["operations"]![0]!["value"]!.AsObject().Select(p => p.Key));
        Assert.Equal("null", json["inputs"]![0]!["default"]!["kind"]!.ToString()); Assert.Null(json["inputs"]![1]!["default"]);
        Assert.Equal("missing", json["operations"]![1]!["arguments"]![0]!["value"]!["kind"]!.ToString());
        var restored = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowIntentPlan)!;
        Assert.True(JsonNode.DeepEquals(json, PlanningJsonTransport.Intent(restored)));
        Assert.True(json.ToJsonString().Length < JsonSerializer.Serialize(intent, PlanningJsonContext.Default.WorkflowIntentPlan).Length);
    }
    [Theory]
    [InlineData("object")]
    [InlineData("array")]
    public void IncompleteNovelTypesFailTheResponseBoundary(string type)
    {
        var intent = PlannerFixture.Greeting(); intent.Inputs.Add(new("value", new() { Type = type }));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Intent(intent), PlanningSchemas.Intent()));
    }
    [Theory]
    [InlineData("resultChannel")]
    [InlineData("schemaPointer")]
    [InlineData("retry")]
    [InlineData("structuredOutput")]
    [InlineData("transport")]
    [InlineData("inputSchema")]
    public void TechnicalFieldsCannotEnterBusinessOperations(string field)
    {
        var json = PlanningJsonTransport.Intent(PlannerFixture.Greeting()); json["operations"]![0]![field] = "forbidden";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()));
    }
    [Fact]
    public void InitialIntentContainsNeitherFixturesNorSchemaCopies()
    {
        var schema = PlanningSchemas.Intent(); Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.DoesNotContain("schemaPointer", schema.ToJsonString()); Assert.DoesNotContain("fixtures", schema.ToJsonString());
        var json = PlanningJsonTransport.Intent(PlannerFixture.Greeting()); json["fixtures"] = new JsonObject();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(json, schema));
    }
    [Fact]
    public async Task ReservedReceiptUsesItsOriginalResponseSchema()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.ModelCalls = 1;
        var schema = PlanningSchemas.Intent(); var strings = schema["$defs"]!["value"]!["anyOf"]![1]!;
        strings["properties"]!["number"] = new JsonObject { ["type"] = "null" }; strings["required"]!.AsArray().Add("number");
        var response = PlanningJsonTransport.Intent(PlannerFixture.Greeting()); response["operations"]![0]!["value"]!["number"] = null;
        state.PendingCall = new() { Id = "reserved", Purpose = "intent", Request = new() { Prompt = "Original reserved prompt", StructuredOutputSchema = schema } };
        runtime.Respond = request => { Assert.Equal("Original reserved prompt", request.Prompt); Assert.True(JsonNode.DeepEquals(schema, request.StructuredOutputSchema)); return new() { Json = response }; };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.RepairAttempts);
    }
    [Fact]
    public void InvalidActiveValuesRemainVisibleAsEvidence()
    {
        var intent = PlannerFixture.Greeting(); ((CalculateIntentOperation)intent.Operations[0]).Value.Boolean = false;
        var json = PlanningJsonTransport.Intent(intent); Assert.False(json["operations"]![0]!["value"]!["boolean"]!.GetValue<bool>());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Intent()));
    }
}
