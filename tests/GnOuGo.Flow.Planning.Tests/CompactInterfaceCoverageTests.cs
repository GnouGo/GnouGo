using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactInterfaceCoverageTests
{
    [Theory]
    [InlineData("inputs")]
    [InlineData("outputs")]
    [InlineData("both")]
    public async Task AnOmittedDispositionBindingCannotCompleteExtraction(string omitted)
    {
        var (state, runtime) = await Ready();
        var before = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        runtime.Respond = (request, _) =>
        {
            var response = Interfaces(request);
            if (omitted is "inputs" or "both") response["interfaces"]!["inputs"] = new JsonArray();
            if (omitted is "outputs" or "both") response["interfaces"]!["outputs"] = new JsonArray();
            return new() { Json = response };
        };
        state = await Advance(state, runtime);
        Assert.NotEmpty(state.Diagnostics);
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.False(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal(before, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Null(state.Construction.Compact.CurrentStep); Assert.Null(state.Construction.Candidate);
        Assert.Empty(state.Construction.Checkpoints); Assert.Equal(0, runtime.Discoveries);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task CompleteExplicitDispositionsPreserveBothDirectionsAndUnboundedArraySemantics()
    {
        var (state, runtime) = await Ready();
        runtime.Respond = (request, _) => new() { Json = Interfaces(request) };
        state = await Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.True(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.True(PlanningCompactConstruction.SourcesComplete(state));
        Assert.Equal("values", Assert.Single(state.Requirements!.Inputs!).Name);
        Assert.Equal("result", Assert.Single(state.Requirements.Outputs!).Name);
        Assert.All(state.Requirements.Inputs!.Concat(state.Requirements.Outputs!), declaration =>
        {
            Assert.Equal("array", declaration.Type.Kind); Assert.Equal("number", declaration.Type.Items!.Kind);
            Assert.Null(declaration.Type.MinItems); Assert.Null(declaration.Type.MaxItems); Assert.Null(declaration.Default);
        });
        Assert.Equal(0, runtime.Discoveries); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task ExplicitNoPublicInterfaceDispositionCannotSmuggleDeclarations()
    {
        var (state, runtime) = await Ready();
        runtime.Respond = (request, _) =>
        {
            var response = Interfaces(request);
            response["interfaces"]!["dispositions"]![0]!["kind"] = "no_public_interface";
            return new() { Json = response };
        };
        state = await Advance(state, runtime);
        Assert.NotEmpty(state.Diagnostics);
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        Assert.Empty(state.Requirements!.Inputs!); Assert.Empty(state.Requirements.Outputs!);
        Assert.Empty(state.Construction.Checkpoints);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Ready()
    {
        const string prompt = "Accept a required numeric array input named values with no default. Return the ordered numeric array as result.";
        var runtime = new TestRuntime { Respond = (request, _) => new() { Json = new JsonObject
        {
            ["sourceId"] = Context(request)["sources"]![0]!["id"]!.DeepClone(),
            ["clauses"] = new JsonArray(new JsonObject { ["text"] = prompt, ["kind"] = "requirement", ["interfaces"] = "both" })
        } } };
        var state = await Advance(PlannerFixture.Capture(new() { Request = new() { TenantId = "interface-coverage", Prompt = prompt } }), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.False(Assert.Single(state.Construction!.Compact!.InterfaceExtractions!).Complete);
        return (state, runtime);
    }

    private static JsonObject Interfaces(LLMRequest request)
    {
        var source = Context(request)["sources"]![0]!;
        var response = new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["interfaces"] = new JsonObject
        {
            ["inputs"] = new JsonArray(Declaration("values")), ["outputs"] = new JsonArray(Declaration("result")),
            ["dispositions"] = new JsonArray(new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone(),
                ["kind"] = "interfaces", ["inputs"] = new JsonArray("values"), ["outputs"] = new JsonArray("result"),
                ["reason"] = "The exact original declares both arrays and their numeric item types.",
                ["evidence"] = new JsonArray(new JsonObject { ["sourceId"] = source["id"]!.DeepClone(), ["version"] = source["version"]!.DeepClone() }) })
        } };
        return PlanningCorpus.Transport(response, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())!.AsObject();
    }
    private static JsonObject Declaration(string name) => new() { ["name"] = name, ["required"] = true,
        ["type"] = new JsonObject { ["kind"] = "array", ["nullable"] = false, ["minItems"] = null, ["maxItems"] = null,
            ["items"] = new JsonObject { ["kind"] = "number", ["nullable"] = false } } };
    private static JsonObject Context(LLMRequest request) => JsonNode.Parse(request.Prompt[(request.Prompt.LastIndexOf('\n') + 1)..])!.AsObject();
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
}
