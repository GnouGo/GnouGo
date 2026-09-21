using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class ValidatedValueTests
{
    [Theory]
    [InlineData("json_value", "{\"count\":3}", true)]
    [InlineData("json_value", "{\"count\":\"3\"}", false)]
    [InlineData("json_value", "{\"other\":3}", false)]
    [InlineData("json_value", "null", false)]
    [InlineData("json_value", "\"{\\\"count\\\":3}\"", false)]
    [InlineData("json_text", "\"{\\\"count\\\":3}\"", true)]
    [InlineData("json_text", "\"invalid json\"", false)]
    [InlineData("json_text", "{\"count\":3}", false)]
    public async Task RuntimeBoundaryValidatesBeforeDownstreamUse(string format, string json, bool success)
    {
        var yaml = """
            version: 1
            name: validated-value
            skill: {description: Validate a supplied value}
            entrypoint: main
            workflows:
              main:
                inputs:
                  raw: {type: any, required: true, nullable: true}
                steps:
                  - id: boundary
                    type: value.validate
                    input:
                      value: ${data.inputs.raw}
                      format: FORMAT
                    output_schema:
                      type: object
                      required: [value]
                      additionalProperties: false
                      properties:
                        value:
                          type: object
                          required: [count]
                          additionalProperties: false
                          properties:
                            count: {type: integer}
                  - id: downstream
                    type: set
                    input:
                      count: ${data.steps.boundary.value.count + 1}
                outputs:
                  count: {type: integer, expr: '${data.steps.downstream.count}'}
            """.Replace("FORMAT", format, StringComparison.Ordinal);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject { ["raw"] = JsonNode.Parse(json) }, TestContext.Current.CancellationToken);
        Assert.Equal(success, result.Success);
        if (success) Assert.Equal("4", result.Outputs!["count"]!.ToJsonString());
        else Assert.DoesNotContain(result.StepResults, s => s.StepId == "downstream" && s.Status == GnOuGo.Flow.Core.Models.StepStatus.Succeeded);
    }
    [Fact]
    public async Task WholeOpaqueResultsCrossScopesButCannotAcquireFieldsFromExamples()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, TestContext.Current.CancellationToken);
        catalog.Capabilities.Add(new() { Id = "opaque", StepType = "mcp.call", InputSchema = new() { ["type"] = "object" },
            ExampleResponse = new JsonObject { ["count"] = 4 } });
        var block = new GroundedBlock([new InvokeGroundedOperation { Id = "raw", Capability = "opaque" }], new() { Kind = "result", Source = "raw" });
        var plan = new GroundedPlan { Operations = [new ParallelGroundedOperation { Id = "parallel", Branches = [new("evidence", block)] }],
            Outputs = [new("result", new() { Kind = "result", Source = "parallel" })] };
        var valid = GroundedPlanValidator.Validate(plan, catalog);
        Assert.NotNull(valid.Plan);
        var graph = PlanningGraphBuilder.Build(valid.Plan);
        Assert.True(GroundedTypes.IsOpaque(PlanningGraphCompiler.ToJsonSchema(graph.Workflows[0].Outputs[0].Schema, catalog)["properties"]!["evidence"]!.AsObject()));
        plan.Outputs = [new("result", new() { Kind = "result", Source = "parallel", Path = ["evidence", "count"] })];
        Assert.Null(GroundedPlanValidator.Validate(plan, catalog).Plan);
        Assert.Empty(catalog.Capabilities.Last().OutputSchema);
    }
    [Fact]
    public async Task AnAssertedCalculationTypeCannotLaunderOpaqueData()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, TestContext.Current.CancellationToken);
        catalog.Capabilities.Add(new() { Id = "opaque", StepType = "mcp.call", InputSchema = new() { ["type"] = "object" } });
        var plan = new GroundedPlan { Operations = [new InvokeGroundedOperation { Id = "raw", Capability = "opaque" },
            new CalculateGroundedOperation { Id = "cast", Value = new() { Kind = "result", Source = "raw" }, ResultType = new() { Type = "number" } }] };
        Assert.Null(GroundedPlanValidator.Validate(plan, catalog).Plan);
    }
    [Fact]
    public async Task OpaqueFixtureGenerationSharesTheCallBudgetAndReachesReviewThroughValidation()
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        server.Tools.Add(new() { Name = "renamed_source", EffectKind = "read", InputSchema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false } });
        factory.RegisterServer("renamed_provider", server);
        var runtime = new TestRuntime(mcp: factory); var state = PlannerFixture.Session();
        var catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        var plan = new GroundedPlan { Operations = [new InvokeGroundedOperation { Id = "raw", Capability = catalog.Capabilities.Single().Id },
            new ValidateGroundedOperation { Id = "validated", Value = new() { Kind = "result", Source = "raw" }, ResultType = new() { Type = "object", Fields = [new("count", new() { Type = "integer" })] } }],
            Outputs = [new("result", new() { Kind = "result", Source = "validated", Path = ["count"] })] };
        runtime.Respond = request => request.StructuredOutputSchema?["properties"]?["observations"] is not null
            ? new() { Json = new JsonObject { ["inputs"] = null, ["observations"] = new JsonArray(new JsonObject { ["workflow"] = "main", ["node"] = "raw",
                ["responses"] = new JsonArray(new JsonObject { ["kind"] = "object", ["members"] = new JsonArray(new JsonObject { ["name"] = "count", ["value"] = new JsonObject { ["kind"] = "number", ["number"] = 3 } }) }) }) } }
            : GnOuGo.Planning.Examples.PlanningCorpus.FixtureResponse(request, request.StructuredOutputSchema?["properties"]?["decisions"] is not null ? "grounding" : request.StructuredOutputSchema?["properties"]?["actions"] is not null ? "semantic" : "binding", plan);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, System.Text.Json.JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Equal(4, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.NotNull(state.Fixtures);
        Assert.Empty(state.Catalog!.Capabilities.Single().OutputSchema);
        PlanningArtifactApproval.Verify(state);
    }
}
