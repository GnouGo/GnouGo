using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class GroundedContractReferenceTests
{
    private static async Task<PlanningCatalog> Catalog()
    {
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, TestContext.Current.CancellationToken);
        catalog.Capabilities.Add(new() { Id = "renamed_consumer", StepType = "mcp.call", InputSchema = JsonNode.Parse("""
            {"type":"object","properties":{"payload":{"type":"object","required":["count"],"additionalProperties":false,"properties":{"count":{"type":"integer","minimum":1,"maximum":3}}}},"additionalProperties":false}
            """)!.AsObject(), ExampleResponse = new JsonObject { ["invented"] = "not a contract" } });
        return catalog;
    }
    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    public async Task ReusedConsumerContractIsValidatedBeforeDownstreamExecution(int value, bool valid)
    {
        var catalog = await Catalog();
        var before = catalog.Capabilities.Single().InputSchema.DeepClone();
        var plan = new GroundedPlan { Operations = [new ValidateGroundedOperation { Id = "adapt",
            Value = new() { Kind = "object", Members = [new("count", new() { Kind = "number", Number = value })] },
            ResultContract = new("renamed_consumer", "input", ["payload"]) },
            new CalculateGroundedOperation { Id = "downstream", Value = new() { Kind = "result", Source = "adapt", Path = ["count"] } }],
            Outputs = [new("result", new() { Kind = "result", Source = "downstream" })] };
        var validated = GroundedPlanValidator.RequireValid(plan, catalog);
        Assert.True(JsonNode.DeepEquals(before["properties"]!["payload"], validated.Types.Result("main", "adapt")));
        Assert.True(JsonNode.DeepEquals(before, catalog.Capabilities.Single().InputSchema));
        var yaml = new PlanningGraphCompiler().Compile(PlanningGraphBuilder.Build(validated), catalog, "contract-reference");
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal(valid, result.Success);
        if (!valid) Assert.DoesNotContain(result.StepResults, s => s.StepId == "downstream" && s.Status == GnOuGo.Flow.Core.Models.StepStatus.Succeeded);
    }
    [Theory]
    [InlineData("unknown")]
    [InlineData("denied")]
    [InlineData("opaque")]
    [InlineData("both")]
    public async Task InvalidReferencesNeverCreateContracts(string defect)
    {
        var catalog = await Catalog();
        var op = new ValidateGroundedOperation { Id = "adapt", Value = new() { Kind = "number", Number = 2 },
            ResultContract = new(defect == "unknown" ? "unissued" : "renamed_consumer", defect == "opaque" ? "output" : "input", defect == "opaque" ? [] : ["payload"]) };
        if (defect == "denied") catalog.Policy.DeniedCapabilityIds.Add("renamed_consumer");
        if (defect == "both") op.ResultType = new() { Type = "integer" };
        Assert.Null(GroundedPlanValidator.Validate(new() { Operations = [op] }, catalog).Plan);
        Assert.Empty(catalog.Capabilities.Single().OutputSchema);
    }
    [Theory]
    [InlineData("{\"value\":{\"count\":2}}", true)]
    [InlineData("{\"value\":{\"count\":\"wrong\"}}", false)]
    [InlineData("{\"value\":{}}", false)]
    public async Task OptionalTransformationFieldsRetainExactRuntimeValidation(string response, bool success)
    {
        var catalog = await Catalog();
        var plan = new GroundedPlan { Operations = [new TransformGroundedOperation { Id = "model", Instruction = "Interpret supplied business data", ResultType = new() { Type = "object", Fields = [new("count", new() { Type = "integer" }), new("note", new() { Type = "string" }, Optional: true)] } }], Outputs = [new("result", new() { Kind = "result", Source = "model" })] };
        var graph = PlanningGraphBuilder.Build(GroundedPlanValidator.RequireValid(plan, catalog));
        Assert.False(graph.Workflows[0].Steps[0].StructuredOutput!.Strict);
        var schema = PlanningGraphCompiler.ToJsonSchema(graph.Workflows[0].Steps[0].StructuredOutput!.Schema, catalog);
        Assert.Equal(new[] { "count" }, schema["properties"]!["value"]!["required"]!.AsArray().Select(n => n!.ToString()));
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog, "optional-output");
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine { LlmDefaults = new() { Model = "fixture" }, LLMClient = new Reply(response) }.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.Equal(success, result.Success);
    }
    private sealed class Reply(string response) : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct) => Task.FromResult(new LLMResponse { Json = JsonNode.Parse(response) });
    }

}
