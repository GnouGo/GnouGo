using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class SyntheticArtifactOracleTests
{
    [Fact]
    public void AuthoredParallelReusableWorkflowPassesSupplementaryChecks()
        => Assert.Empty(SyntheticArtifactOracle.Validate("collections", Collections()));

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    public void CorrectValuesDoNotExcuseSerialItemWork(bool parallel, int concurrency)
    {
        var plan = Collections();
        plan.Root.Tasks[0].Parallel = parallel;
        plan.Root.Tasks[0].MaxConcurrency = concurrency;
        Assert.Contains("serial_item_iteration", SyntheticArtifactOracle.Validate("collections", plan));
    }

    [Fact]
    public void UnusedGroupAndParallelLoopDoNotEstablishRequestedComposition()
    {
        var plan = Collections();
        plan.Root.Outputs = [new("result", new() { Kind = "array",
            Items = [new() { Kind = "number", Number = 2 }, new() { Kind = "number", Number = 4 }, new() { Kind = "number", Number = 8 }] })];
        Assert.Contains("missing_parallel_reusable_item_calls", SyntheticArtifactOracle.Validate("collections", plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineDoublingDoesNotBecomeReusableThroughItsLabelOrAnUnusedGroup(bool keepUnusedGroup)
    {
        var plan = Collections();
        if (!keepUnusedGroup) plan.Groups.Clear();
        var loop = plan.Root.Tasks[0];
        var outputName = loop.Body!.Outputs[0].Name;
        loop.Objective = "Use this inline body as the reusable per-item subworkflow.";
        loop.Body.Tasks = [new() { Id = "inline_double", Kind = "value", Objective = "Produce the reusable doubled result.",
            Outputs = [new("doubled", new() { Kind = "arithmetic", Text = "multiply", Items = [new() { Kind = "item" }, new() { Kind = "number", Number = 2 }] })] }];
        loop.Body.Outputs = [new(outputName, new() { Kind = "output", Source = "inline_double", Port = "doubled" })];
        Assert.Contains("missing_parallel_reusable_item_calls", SyntheticArtifactOracle.Validate("collections", plan));
        var engine = new WorkflowEngine();
        var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask)
            .DiscoverAsync(new(), TestContext.Current.CancellationToken);
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compilation.Graph!, catalog);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        foreach (var variant in new[] { "nominal", "alternate" })
        {
            var sample = new PlanningBenchmarkCases.Environment("collections", variant);
            var result = await engine.ExecuteAsync(document.Workflows[document.Entrypoint!],
                PlanningBenchmarkCases.Inputs("collections", variant), TestContext.Current.CancellationToken);
            Assert.True(sample.Verify(result), result.Error?.Message);
            Assert.Empty(sample.Effects); Assert.Empty(sample.Violations);
        }
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("transform")]
    public void IsolatedRuntimeCannotAcquireExternalDependencies(string kind)
    {
        var plan = Collections();
        plan.Groups[0].Body.Tasks[0].Kind = kind;
        Assert.Contains("unavailable_runtime_dependency", SyntheticArtifactOracle.Validate("collections", plan));
    }

    [Fact]
    public void DataForwardingDoesNotRequireFixtureTaskNames()
    {
        var plan = Collections();
        plan.Root.Tasks[0].Id = "parallel_items";
        plan.Root.Outputs = [new("result", new() { Kind = "output", Source = "forward", Port = "value" })];
        plan.Root.Tasks.Add(new() { Id = "forward", Kind = "value", Outputs = [new("value",
            new() { Kind = "output", Source = "parallel_items", Port = "values" })] });
        Assert.Empty(SyntheticArtifactOracle.Validate("collections", plan));
    }

    private static TaskPlan Collections() => PlanningCorpus.Tasks("collections", new());
}
