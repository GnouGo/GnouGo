using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LiteralStringArrayTests
{
    private static IntentValue Labels() => new() { Kind = "array", Items = [new() { Kind = "string", Text = "north" }, new() { Kind = "string", Text = "south" }] };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctLiteralStringsEstablishTheirFullArrayContract(bool nested)
    {
        var value = nested ? new IntentValue { Kind = "object", Members = [new("labels", Labels())] } : Labels();
        var plan = new WorkflowIntentPlan { Operations = [new CalculateIntentOperation { Id = "labels", Value = value }],
            Outputs = [new("labels", new() { Kind = "result", Source = "labels", Path = nested ? ["labels"] : [] })] };
        var runtime = new TestRuntime(plan);
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Single(runtime.Calls);
        Assert.Equal(0, state.RepairAttempts);
        var output = state.Graph!.Workflows.Single(w => w.Key == "main").Outputs.Single().Schema;
        Assert.Equal("array", output.Type);
        Assert.Equal("string", output.Items!.Type);
        Assert.Equal(["north", "south"], output.Items.Enum);
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("[\"north\",\"south\"]", result.Outputs!["labels"]!.ToJsonString());
    }

    [Fact]
    public async Task InferredUnionCannotSatisfyANarrowerAuthoritativeEnum()
    {
        var runtime = new TestRuntime(mcp: BusinessCorrectionTests.Factory(1, """{"labels":{"type":"array","items":{"type":"string","enum":["north"]}}}""", ["labels"]));
        var state = PlannerFixture.Session(); state.Request.MaxRepairAttempts = 0;
        state.Catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        var plan = new WorkflowIntentPlan { Operations = [new CalculateIntentOperation { Id = "labels", Value = Labels() },
            new InvokeIntentOperation { Id = "consume", Capability = state.Catalog.Capabilities.Single().Id,
                Arguments = [new("labels", new() { Kind = "result", Source = "labels" })] }] };
        runtime.Plans.Clear(); runtime.Plans.Enqueue(plan);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.NotEmpty(state.Diagnostics);
        Assert.Null(state.Yaml);
        Assert.Null(state.ApprovedHash);
    }
}
