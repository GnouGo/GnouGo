using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LoweringContractTests
{
    [Theory]
    [InlineData("anyOf")]
    [InlineData("oneOf")]
    public async Task NestedUnionProducerSurvivesLoweringAndConfirmation(string union)
    {
        var schema = JsonNode.Parse("""{"type":"object","properties":{"result":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":["result"],"additionalProperties":false}""")!.AsObject();
        if (union == "oneOf")
        {
            var result = schema["properties"]!["result"]!.AsObject();
            result["oneOf"] = result["anyOf"]!.DeepClone(); result.Remove("anyOf");
        }
        var factory = new InMemoryMcpClientFactory();
        var server = new MockMcpServerConfig();
        server.Tools.Add(new() { Name = "renamed_operation", EffectKind = "write",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""), OutputSchema = schema });
        factory.RegisterServer("arbitrary_provider", server);
        var runtime = new TestRuntime(mcp: factory);
        var request = PlannerFixture.Session().Request;
        var catalog = await runtime.DiscoverAsync(request, TestContext.Current.CancellationToken);
        var plan = new GroundedPlan
        {
            Operations = [new InvokeGroundedOperation { Id = "produce", Capability = catalog.Capabilities.Single().Id }],
            Outputs = [new("value", new() { Kind = "result", Source = "produce" })]
        };
        var graph = PlanningGraphBuilder.Build(GroundedPlanValidator.RequireValid(plan, catalog));
        PlanningConfirmationGuards.Apply(graph, catalog);
        Assert.Equal(2, graph.Workflows.Count);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog);
        _ = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        Assert.Empty(await runtime.ValidateAsync(new(yaml, request, catalog, PlanningGraphCompiler.CapabilityBindings(graph)), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"integer\"}]}", "{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}")]
    [InlineData("{\"type\":\"integer\",\"minimum\":0}", "{\"type\":\"integer\",\"minimum\":1}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}}", "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"required\":[\"value\"]}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"x-gnougo-opaque\":true}}", "{\"type\":\"array\",\"items\":{\"type\":\"string\"}}")]
    public void GraphCompatibilityCannotNarrowUnprovedContracts(string actual, string expected)
    {
        Assert.False(PlanningGraphValidation.TypesFit(JsonNode.Parse(actual)!.AsObject(), JsonNode.Parse(expected)!.AsObject()));
    }

    [Fact]
    public void UnknownValuesRequireAnExistingRuntimeAssertionAndCannotReplaceOpaqueEvidence()
    {
        var expected = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject();
        var actual = JsonNode.Parse("""{"type":"object","properties":{"value":{}},"required":["value"],"additionalProperties":false}""")!.AsObject();
        Assert.False(PlanningGraphValidation.TypesFit(actual, expected));
        Assert.True(PlanningGraphValidation.TypesFit(actual, expected, allowUnresolved: true));
        actual["properties"]!["value"] = GroundedTypes.Opaque();
        Assert.False(PlanningGraphValidation.TypesFit(actual, expected, allowUnresolved: true));
        actual["properties"]!["value"] = new JsonObject { ["type"] = "integer" };
        Assert.False(PlanningGraphValidation.TypesFit(actual, expected, allowUnresolved: true));
    }

    [Fact]
    public async Task InvalidLoweringStopsWithoutFixtureDispatchOrModelReplanning()
    {
        var runtime = new TestRuntime();
        var state = PlannerFixture.Session();
        state.Catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        state.GroundedPlan = PlannerFixture.Greeting();
        state.Graph = PlannerFixture.Build(state.GroundedPlan, state.Catalog);
        // Corrupt only the lowered artifact, after deterministic grounded validation.
        state.Graph.Workflows[0].Outputs[0].Schema = new() { Type = "integer" };
        state.ModelCalls = 2; state.ReplanAttempts = 0;
        state.Yaml = "stale executable"; state.ApprovedHash = "stale approval";
        await PlanningValidationPipeline.ValidateAsync(state, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        var diagnostic = Assert.Single(state.Diagnostics, d => d.Code == "PLANNING_HOST_CONTRACT");
        Assert.Contains("OUTPUT_TYPE_MISMATCH", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("/workflows/0/outputs/0/schema", diagnostic.Location);
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash); Assert.Empty(state.Scenarios);
        await PlannerFixture.RunAsync(runtime, state);
        Assert.Empty(runtime.Calls); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }
}
