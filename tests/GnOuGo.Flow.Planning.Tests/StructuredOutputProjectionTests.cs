using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class StructuredOutputProjectionTests
{
    internal const string WorkspacePattern = @"^workflows/(?!\.{1,2}(?:/|$))[^/\\*?\x00-\x1F\x7F-\x9F]+(?:/(?!\.{1,2}(?:/|$))[^/\\*?\x00-\x1F\x7F-\x9F]+)*(?![\s\S])";

    [Fact]
    public void SavedDiscoveryExpansionReproducesTheFailureAndProjectsBothIssuedSchemas()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SchemaPortability", "retained-schema-rejection.json")))!;
        Assert.Equal(2, fixture["modelCalls"]!.GetValue<int>()); Assert.Equal(0, fixture["repairs"]!.GetValue<int>());
        var schemas = fixture["schemas"]!.AsArray(); var original = fixture.ToJsonString();
        Assert.Empty(PlanningContractValidation.ValidateSchema(schemas[0]!, true));
        var error = Assert.Single(PlanningContractValidation.ValidateSchema(schemas[1]!, true));
        Assert.Contains("$.$defs.d10.anyOf[6].properties.inputs.items.anyOf[4]", error);
        foreach (var schema in schemas)
            Assert.Empty(PlanningContractValidation.ValidateSchema(PlanningContractValidation.ProjectStructuredOutputSchema(schema!.AsObject()), true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(fixture["discoveryResponse"], schemas[0]!));
        Assert.Equal(original, fixture.ToJsonString());
    }

    [Theory]
    [InlineData("keyword", "$.properties.value.unsupported")]
    [InlineData("reference", "$.properties.value.$ref")]
    [InlineData("data_reference", "$.properties.value.$ref")]
    [InlineData("nested", "$.$defs.payload.items.anyOf[1].pattern")]
    public void StrictPreflightCoversDefinitionsReferencesAndNestedPositions(string fault, string path)
    {
        var schema = Schema("^valid$");
        switch (fault)
        {
            case "keyword": schema["properties"]!["value"]!["unsupported"] = true; break;
            case "reference": schema["properties"]!["value"] = new JsonObject { ["$ref"] = "#/$defs/missing" }; break;
            case "data_reference": schema["default"] = new JsonObject { ["type"] = "string" }; schema["properties"]!["value"] = new JsonObject { ["$ref"] = "#/default" }; break;
            case "nested": schema["$defs"] = JsonNode.Parse("""{"payload":{"type":"array","items":{"anyOf":[{"type":"null"},{"type":"string","pattern":"(?=bad)"}]}}}"""); break;
        }
        Assert.Contains(PlanningContractValidation.ValidateSchema(schema, true), e => e.StartsWith(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("^[a-z]+$", true)]
    [InlineData(@"\S", true)]
    [InlineData(@"^(?!bad)[a-z]+$", false)]
    [InlineData(@"(?<=prefix)value", false)]
    [InlineData(@"^(a)\1$", false)]
    [InlineData(@"^[a-z]+\z", false)]
    [InlineData(@"\Avalue", false)]
    [InlineData(WorkspacePattern, false)]
    public void ProjectsOnlyNonportablePatternsAndPreservesAuthoritativeValues(string pattern, bool portable)
    {
        var schema = Schema(pattern);
        schema["$defs"] = new JsonObject { ["nested"] = Schema(pattern) };
        schema["properties"]!["entry"] = new JsonObject { ["$ref"] = "#/$defs/nested" };
        schema["required"] = new JsonArray("value", "entry");
        schema["default"] = new JsonObject { ["pattern"] = pattern, ["properties"] = new JsonObject { ["pattern"] = pattern } };
        var original = schema.ToJsonString();
        var projected = PlanningContractValidation.ProjectStructuredOutputSchema(schema);
        Assert.Equal(original, schema.ToJsonString());
        Assert.Equal(portable ? pattern : null, projected["properties"]!["value"]!["pattern"]?.ToString());
        Assert.Equal(portable ? pattern : null, projected["$defs"]!["nested"]!["properties"]!["value"]!["pattern"]?.ToString());
        Assert.True(JsonNode.DeepEquals(schema["default"], projected["default"]));
        Assert.Empty(PlanningContractValidation.ValidateSchema(projected, strict: true));
        Assert.Equal(portable, PlanningContractValidation.ValidateSchema(schema, strict: true).Count == 0);
        projected["properties"]!["value"]!["minLength"] = 8;
        Assert.Equal(1, schema["properties"]!["value"]!["minLength"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("workflows/../escape")]
    [InlineData("workflows/./escape")]
    [InlineData("/workflows/target")]
    [InlineData("forbidden/target")]
    [InlineData("workflows/target\n")]
    [InlineData("workflows/target\0")]
    public async Task ProjectionNeverWeakensAuthoritativePaths(string path)
    {
        var state = await State();
        var authority = state.Catalog!.Capabilities.Single(c => c.Id == "external").InputSchema;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["value"] = path }, authority));
        state.Plan!.Root.Tasks[0].Inputs = [new("value", PlanningCorpus.String(path))];
        var request = new PlanningPrompt(state).Request();
        Assert.DoesNotContain(WorkspacePattern, request.StructuredOutputSchema!.ToJsonString());
        Assert.Contains("pattern", request.Prompt);
        Assert.NotEmpty(new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics);
    }

    [Fact]
    public async Task GenerationAndRepairProjectCompletedSchemasWithoutChangingAuthority()
    {
        var state = await State();
        var catalog = JsonSerializer.Serialize(state.Catalog, PlanningJsonContext.Default.PlanningCatalog);
        Assert.Contains(PlanningContractValidation.ValidateSchema(PlanningSchemas.Proposal(state), true), e => e.Contains("pattern"));
        var request = new PlanningPrompt(state).Request();
        Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema!, true));
        state.Diagnostics = [new("TASK_INPUT_TYPE", "/tasks/perform/inputs/value", "Invalid value")];
        state.RevisionScope = ["/tasks/perform/inputs/value"];
        var authority = PlanningRepairPatch.Authority(state, 5);
        var repair = new PlanningPrompt(state).Request();
        Assert.Empty(PlanningContractValidation.ValidateSchema(repair.StructuredOutputSchema!, true));
        Assert.Equal(authority, PlanningRepairPatch.Authority(state, 5));
        PlanningRepairPatch.Verify(state, repair);
        Assert.Equal(catalog, JsonSerializer.Serialize(state.Catalog, PlanningJsonContext.Default.PlanningCatalog));
        Assert.Contains("pattern", PlanningRepairPatch.RequestContext(repair).ToJsonString());
    }

    [Fact]
    public async Task InvalidRemainingSchemaStopsBeforeReservationOrDispatch()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime();
        var schema = Schema("[");
        var projected = PlanningContractValidation.ProjectStructuredOutputSchema(schema);
        Assert.Equal("[", projected["properties"]!["value"]!["pattern"]!.ToString());
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelCalls.CallAsync(state, runtime, "replan", "prompt", projected, PlannerFixture.Ct));
        Assert.Equal("MODEL_SCHEMA_INVALID", error.Code);
        Assert.Contains("$.properties.value.pattern", error.Message);
        Assert.Equal(0, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall); Assert.Empty(runtime.Calls); Assert.Empty(runtime.Checkpoints);
    }

    [Fact]
    public async Task RecoveryPreservesAlreadyIssuedNonportableSchemaExactly()
    {
        var state = PlannerFixture.Session(); state.ModelCalls = 1;
        var request = new LLMRequest { ClientRequestId = "original", Prompt = "original", StructuredOutputSchema = Schema(WorkspacePattern) };
        state.PendingCall = new() { Id = "original", Purpose = "tasks", Request = request };
        state = PlannerFixture.Clone(state);
        var issued = JsonSerializer.Serialize(state.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject { ["value"] = "workflows/valid" } } };
        await PlanningModelCalls.CallAsync(state, runtime, "tasks", "changed", Schema("^portable$"), PlannerFixture.Ct);
        Assert.Equal(issued, JsonSerializer.Serialize(Assert.Single(runtime.Calls), PlanningJsonContext.Default.LLMRequest));
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    private static JsonObject Schema(string pattern) => new()
    {
        ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("value"),
        ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["pattern"] = pattern } }
    };
    private static async Task<PlanningSession> State()
    {
        var state = await ComposedOutcomeTests.State();
        state.Catalog!.Capabilities.Single(c => c.Id == "external").InputSchema = Schema(WorkspacePattern);
        state.Plan!.Root.Tasks[0].Inputs = [new("value", PlanningCorpus.String("workflows/valid"))];
        return state;
    }
}
