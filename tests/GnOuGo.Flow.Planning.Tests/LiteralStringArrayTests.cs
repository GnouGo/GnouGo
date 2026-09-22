using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LiteralStringArrayTests
{
    private static GroundedValue Labels() => new() { Kind = "array", Items = [new() { Kind = "string", Text = "north" }, new() { Kind = "string", Text = "south" }] };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctLiteralStringsEstablishTheirFullArrayContract(bool nested)
    {
        var value = nested ? new GroundedValue { Kind = "object", Members = [new("labels", Labels())] } : Labels();
        var plan = new GroundedPlan { Operations = [new CalculateGroundedOperation { Id = "labels", Value = value }],
            Outputs = [new("labels", new() { Kind = "result", Source = "labels", Path = nested ? ["labels"] : [] })] };
        var runtime = new TestRuntime(plan);
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts);
        var output = PlanningGraphCompiler.ToJsonSchema(state.Graph!.Workflows.Single(w => w.Key == "main").Outputs.Single().Schema, state.Catalog!);
        Assert.Equal("array", output["type"]!.ToString());
        Assert.Equal("string", output["items"]!["type"]!.ToString());
        Assert.Equal(["north", "south"], output["items"]!["enum"]!.AsArray().Select(v => v!.ToString()));
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("[\"north\",\"south\"]", result.Outputs!["labels"]!.ToJsonString());
    }

    [Fact]
    public async Task InferredUnionCannotSatisfyANarrowerAuthoritativeEnum()
    {
        var runtime = new TestRuntime(mcp: PlannerFixture.Factory(1, """{"labels":{"type":"array","items":{"type":"string","enum":["north"]}}}""", ["labels"]));
        var state = PlannerFixture.Session(); state.Request.MaxReplanAttempts = 0;
        state.Catalog = await runtime.DiscoverAsync(state.Request, TestContext.Current.CancellationToken);
        var plan = new GroundedPlan { Operations = [new CalculateGroundedOperation { Id = "labels", Value = Labels() },
            new InvokeGroundedOperation { Id = "consume", Capability = state.Catalog.Capabilities.Single().Id,
                Arguments = [new("labels", new() { Kind = "result", Source = "labels" })] }] };
        runtime.Plans.Clear(); runtime.Plans.Enqueue(plan);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.NotEmpty(state.Diagnostics);
        Assert.Null(state.Yaml);
        Assert.Null(state.ApprovedHash);
    }
}
