using GnOuGo.Flow.Core.Planning;
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
