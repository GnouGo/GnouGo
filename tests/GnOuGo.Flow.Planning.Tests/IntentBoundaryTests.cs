using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class IntentBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public void CanonicalGroundedPlanPreservesNullAndAbsentDefaultsAndRejectsMissingValues()
    {
        var intent = PlannerFixture.Greeting();
        intent.Inputs = [new("explicit", new() { Nullable = true }, true, new()), new("omitted", new(), true)];
        var json = PlanningJsonTransport.Grounded(intent);
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Grounded()));
        Assert.Equal(new[] { "kind", "text" }, json["operations"]![0]!["value"]!.AsObject().Select(p => p.Key));
        Assert.Equal("null", json["inputs"]![0]!["default"]!["kind"]!.ToString()); Assert.Null(json["inputs"]![1]!["default"]);
        var restored = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.GroundedPlan)!;
        Assert.True(JsonNode.DeepEquals(json, PlanningJsonTransport.Grounded(restored)));
        Assert.True(json.ToJsonString().Length < JsonSerializer.Serialize(intent, PlanningJsonContext.Default.GroundedPlan).Length);
        intent.Operations.Add(new InvokeGroundedOperation { Id = "missing", Capability = "issued", Arguments = [new("value", new() { Kind = "missing" })] });
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Grounded(intent), PlanningSchemas.Grounded()));
    }
    [Theory]
    [InlineData("transform")]
    [InlineData("validate")]
    public void AdaptersRequireExactlyOneContractInTheIssuedResponseSchema(string kind)
    {
        GroundedOperation operation = kind == "transform" ? new TransformGroundedOperation { Id = "adapt" } : new ValidateGroundedOperation { Id = "adapt" };
        var schema = PlanningSchemas.Grounded(["renamed_producer"]);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        var json = PlanningJsonTransport.ModelGrounded(PlanningJsonTransport.Grounded(new() { Operations = [operation] }), schema);
        var body = json["operations"]![0]!["implementation"]!;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(json, schema));
        body["resultType"] = new JsonObject { ["type"] = "string", ["nullable"] = false, ["enum"] = new JsonArray() };
        Assert.Empty(PlanningContractValidation.ValidateInstance(json, schema));
        body["resultContract"] = new JsonObject { ["capability"] = "renamed_producer", ["direction"] = "output", ["path"] = new JsonArray() };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(json, schema));
        body["resultType"] = null;
        Assert.Empty(PlanningContractValidation.ValidateInstance(json, schema));
    }
    [Theory]
    [InlineData("object")]
    [InlineData("array")]
    public void IncompleteNovelTypesFailTheResponseBoundary(string type)
    {
        var intent = PlannerFixture.Greeting(); intent.Inputs.Add(new("value", new() { Type = type }));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(PlanningJsonTransport.Grounded(intent), PlanningSchemas.Grounded()));
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
        var json = PlanningJsonTransport.Grounded(PlannerFixture.Greeting()); json["operations"]![0]![field] = "forbidden";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Grounded()));
    }
    [Fact]
    public void InitialIntentContainsNeitherFixturesNorSchemaCopies()
    {
        var schema = PlanningSchemas.Grounded(); Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.DoesNotContain("schemaPointer", schema.ToJsonString()); Assert.DoesNotContain("fixtures", schema.ToJsonString());
        var json = PlanningJsonTransport.Grounded(PlannerFixture.Greeting()); json["fixtures"] = new JsonObject();
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(json, schema));
    }
    [Fact]
    public async Task ReservedReceiptUsesItsOriginalResponseSchema()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Catalog = await runtime.DiscoverAsync(state.Request, Ct); state.ModelCalls = 1;
        var schema = PlanningSchemas.Grounded(); var strings = schema["$defs"]!["value"]!["anyOf"]![1]!;
        strings["properties"]!["number"] = new JsonObject { ["type"] = "null" }; strings["required"]!.AsArray().Add("number");
        var response = PlanningJsonTransport.Grounded(PlannerFixture.Greeting()); response["operations"]![0]!["value"]!["number"] = null;
        state.PendingCall = new() { Id = "reserved", Purpose = "binding", Request = new() { Prompt = "Original reserved prompt", StructuredOutputSchema = schema } };
        state.SemanticPlan = GnOuGo.Planning.Examples.PlanningCorpus.Semantic(PlannerFixture.Greeting());
        state.Grounding = CapabilityGrounder.Create(state); state.Grounding.Results = state.Grounding.Pages.Select(p => new GroundingPageResult(p.Id, p.ActionIds.Select(id => new GroundingDecision(id, "none_of_the_above", [], "Native implementation")).ToList())).ToList();
        state.Grounding.Selections = state.SemanticPlan.Actions.Select(a => new GroundingSelection(a.Id, [], "Native implementation")).ToList();
        response["operations"]![0]!["semanticAction"] = state.SemanticPlan.Actions[0].Id;
        response["operations"]![0]!["businessOutputs"] = new JsonArray(new JsonObject { ["name"] = "value", ["path"] = new JsonArray() });
        runtime.Respond = request => { Assert.Equal("Original reserved prompt", request.Prompt); Assert.True(JsonNode.DeepEquals(schema, request.StructuredOutputSchema)); return new() { Json = response }; };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }
    [Fact]
    public void InvalidActiveValuesRemainVisibleAsEvidence()
    {
        var intent = PlannerFixture.Greeting(); ((CalculateGroundedOperation)intent.Operations[0]).Value.Boolean = false;
        var json = PlanningJsonTransport.Grounded(intent); Assert.False(json["operations"]![0]!["value"]!["boolean"]!.GetValue<bool>());
        Assert.NotEmpty(PlanningContractValidation.ValidateInstanceFindings(json, PlanningSchemas.Grounded()));
    }
}
