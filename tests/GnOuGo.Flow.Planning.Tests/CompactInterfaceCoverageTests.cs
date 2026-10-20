using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactInterfaceCoverageTests
{
    [Theory]
    [InlineData("inputs")]
    [InlineData("outputs")]
    [InlineData("both")]
    public async Task AnOmittedDeclaredDirectionCannotCompleteExtractionOrFreezeTheOtherDirection(string omitted)
    {
        var (state, runtime) = await Ready();
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var sources = JsonSerializer.Serialize(state.Construction!.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(1, request.StructuredOutputSchema!["properties"]!["inputs"]!["minItems"]!.GetValue<int>());
            Assert.Equal(1, request.StructuredOutputSchema["properties"]!["outputs"]!["minItems"]!.GetValue<int>());
            Assert.Empty(PlanningContractValidation.ValidateSchema(request.StructuredOutputSchema, strict: true));
            var response = Interfaces(request);
            if (omitted is "inputs" or "both") response["inputs"] = new JsonArray();
            if (omitted is "outputs" or "both") response["outputs"] = new JsonArray();
            return new() { Json = response };
        };
        state = await Advance(state, runtime);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "PLANNING_RESPONSE_INVALID" &&
            diagnostic.Location == (omitted == "both" ? "/inputs" : "/" + omitted));
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.False(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Null(state.Construction.Compact.CurrentStep);
        Assert.Null(state.Construction.Candidate);
        Assert.Empty(state.Construction.Checkpoints);
        Assert.Equal(0, runtime.Discoveries);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);

        runtime.Respond = (request, _) => new() { Json = Interfaces(request) };
        state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal("values", Assert.Single(state.Requirements!.Inputs!).Name);
        Assert.Equal("result", Assert.Single(state.Requirements.Outputs!).Name);
        Assert.All(state.Requirements.Inputs!.Concat(state.Requirements.Outputs!), declaration =>
        {
            Assert.Equal("array", declaration.Type.Kind);
            Assert.Equal("number", declaration.Type.Items!.Kind);
            Assert.Null(declaration.Type.MinItems);
            Assert.Null(declaration.Type.MaxItems);
            Assert.Null(declaration.Default);
        });
        Assert.Equal(3, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(sources, JsonSerializer.Serialize(state.Construction.Compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));
        Assert.Equal(0, runtime.Discoveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderIssuedOptionalDirectionReceiptsKeepTheirExactSchemaAndAccounting(bool historicalPolicy)
    {
        var (state, _) = await Ready();
        if (historicalPolicy)
        {
            state.Request.Options["construction_limits"]!.AsObject().Remove("phasePolicy");
            state.Construction!.Limits.PhasePolicy = null;
        }
        var extraction = Assert.Single(state.Construction!.Compact!.InterfaceExtractions!);
        var original = PlanningCompactConstruction.Request(PlanningCompactConstruction.InterfaceInstructions,
            PlanningCompactConstruction.InterfaceContext(state, extraction), PlanningCompactConstruction.InterfaceSchema(extraction),
            PlanningCompactConstruction.IntentPurpose);
        var issued = PlanningModelCalls.PrepareRequest(state, PlanningCompactConstruction.IntentPurpose, original.Prompt,
            original.StructuredOutputSchema!.AsObject(), state.Construction.Limits.DraftOutputTokens);
        Assert.Equal(0, issued.StructuredOutputSchema!["properties"]!["outputs"]!["minItems"]!.GetValue<int>());
        var response = Interfaces(issued);
        response["outputs"] = new JsonArray();
        Assert.Empty(PlanningContractValidation.ValidateInstance(response, issued.StructuredOutputSchema));
        var receipt = new LLMResponse { Json = response };
        var exact = JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest);
        var originals = JsonSerializer.Serialize(state.Construction.Compact.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource);
        state.PendingCall = new() { Id = issued.ClientRequestId!, Purpose = PlanningCompactConstruction.IntentPurpose, Request = issued };
        state.ModelCalls++;
        var measurements = JsonSerializer.Serialize(state.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement);
        var calls = state.ModelCalls; var corrections = state.ReplanAttempts;
        var physicalCalls = 0;
        var recovery = new TestRuntime { Respond = (request, _) =>
        {
            Assert.Equal(exact, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            if (request.ClientRequestId == issued.ClientRequestId) return receipt;
            physicalCalls++;
            throw new InvalidOperationException("An issued receipt must not dispatch another request.");
        } };
        state = await Advance(PlannerFixture.Clone(state), recovery);
        Assert.Empty(state.Diagnostics);
        Assert.Equal(0, physicalCalls);
        Assert.Single(recovery.Calls);
        Assert.Null(state.PendingCall);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Equal(corrections, state.ReplanAttempts);
        Assert.Equal(measurements, JsonSerializer.Serialize(state.RequestMeasurements, PlanningJsonContext.Default.ListPlanningRequestMeasurement));
        Assert.Equal(originals, JsonSerializer.Serialize(state.Construction!.Compact!.Sources, PlanningJsonContext.Default.ListPlanningConstructionSource));
        Assert.True(Assert.Single(state.Construction.Compact.InterfaceExtractions!).Complete);
        Assert.Empty(state.Requirements!.Outputs!);
        Assert.Equal("values", Assert.Single(state.Requirements.Inputs!).Name);
    }

    [Theory]
    [InlineData("input", "outputs")]
    [InlineData("output", "inputs")]
    public async Task AnUndeclaredDirectionRemainsEmptyInTheActualFreshRequest(string declared, string empty)
    {
        var (state, runtime) = await Ready(declared);
        runtime.Respond = (request, _) =>
        {
            Assert.Equal(0, request.StructuredOutputSchema!["properties"]![empty]!["maxItems"]!.GetValue<int>());
            var response = Interfaces(request);
            response[empty] = new JsonArray();
            return new() { Json = response };
        };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.Equal(declared == "input" ? 1 : 0, state.Requirements!.Inputs!.Count);
        Assert.Equal(declared == "output" ? 1 : 0, state.Requirements.Outputs!.Count);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready(string direction = "both")
    {
        const string input = "Accept a numeric array input named values. ";
        const string output = "Return the ordered numeric array as result.";
        var prompt = direction == "input" ? input : direction == "output" ? output : input + output;
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = new JsonObject
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = prompt, ["kind"] = "requirement", ["interfaces"] = direction })
        } } };
        var state = await Advance(new() { Request = new() { TenantId = "interface-coverage", Prompt = prompt,
            Options = new() { ["automatic_correction"] = true } } }, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(PlanningConstructionPhasePolicies.IsFresh(state.Request));
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        return (state, runtime);
    }

    private static JsonObject Interfaces(LLMRequest request) => new()
    {
        ["sourceId"] = request.StructuredOutputSchema!["properties"]!["sourceId"]!["enum"]![0]!.DeepClone(),
        ["inputs"] = new JsonArray(Declaration("values")), ["outputs"] = new JsonArray(Declaration("result"))
    };
    private static JsonObject Declaration(string name) => new()
    {
        ["name"] = name, ["required"] = true, ["type"] = new JsonObject
        {
            ["kind"] = "array", ["nullable"] = false, ["minItems"] = null, ["maxItems"] = null,
            ["items"] = new JsonObject { ["kind"] = "number", ["nullable"] = false }
        }
    };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
