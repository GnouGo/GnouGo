using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningComputationBoundaryTests
{
    private static WorkflowIntentPlan Calculation(string text) => new()
    {
        Inputs = [new("quantity", new() { Type = "number" }, false, new() { Kind = "number", Number = 4 })],
        Operations = [new CalculateIntentOperation { Id = "total", ResultType = new() { Type = "number" }, Value = new() { Kind = "compute", Text = text,
            Members = [new("quantity", new() { Kind = "input", Source = "quantity" })] } }],
        Outputs = [new("total", new() { Kind = "result", Source = "total" })]
    };

    [Theory]
    [InlineData("const doubled = quantity * 2; return doubled;")]
    [InlineData("if (quantity > 0) { return quantity * 2; } return 0;")]
    public async Task SupportedComputationBodyReachesDeclaredContractFallback(string text)
    {
        var runtime = new TestRuntime(Calculation(text));
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls);
        Assert.Equal(0, state.RepairAttempts);
        Assert.DoesNotContain(state.Diagnostics, d => d.Required);
        Assert.NotEmpty(state.Scenarios);
        Assert.Equal("number", state.Graph!.Workflows.Single(w => w.Key == "main").Outputs.Single().Schema.Type);
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("8", result.Outputs!["total"]!.ToJsonString());
    }

    [Fact]
    public async Task TypedCorrectionMaySupplyASupportedComputationBody()
    {
        var runtime = new TestRuntime(Calculation("quantity + unknown"));
        runtime.Respond = request => request.StructuredOutputSchema!["properties"]?["changes"] is null
            ? new() { Json = PlanningJsonTransport.Intent(Calculation("quantity + unknown")) }
            : new() { Json = new JsonObject { ["changes"] = new JsonArray(
                JsonNode.Parse(request.Prompt[request.Prompt.IndexOf("\n{", StringComparison.Ordinal)..])!["targets"]!.AsArray()
                    .Select(t => (JsonNode)new JsonObject { ["target"] = t!["id"]!.DeepClone(),
                        ["replacement"] = PlanningJsonTransport.Intent(Calculation("const doubled = quantity * 2; return doubled;"))["operations"]![0]!["value"]!.DeepClone() }).ToArray()) } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(1, state.RepairAttempts);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task UnexpectedHostFailureStopsWithoutRepeatingOrSpendingModelRepairs()
    {
        var runtime = new TestRuntime { ValidationFailure = new NullReferenceException("Injected host validator defect.") };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Single(runtime.Calls);
        Assert.Equal(0, state.RepairAttempts);
        Assert.Single(state.Diagnostics, d => d.Code == "PLANNING_INVALID");
        Assert.Null(state.Yaml);
        Assert.Null(state.ApprovedHash);
        var checkpoints = runtime.Checkpoints.Count;
        var next = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Same(state, next);
        Assert.Equal(checkpoints, runtime.Checkpoints.Count);
    }
}
