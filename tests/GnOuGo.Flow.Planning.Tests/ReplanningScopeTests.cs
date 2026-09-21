using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning.Tests;

public sealed class ReplanningScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplanningIncludesCapturedProducersAndTheirConsumers(bool captured)
    {
        var source = new CalculateGroundedOperation { Id = "source", Value = new() { Kind = "number", Number = 1 } };
        var consume = new CalculateGroundedOperation { Id = "consume", Value = new() { Kind = "result", Source = "source" } };
        var branch = new GroundedBlock(captured ? [consume] : [source, consume], new() { Kind = "result", Source = "consume" });
        var parallel = new ParallelGroundedOperation { Id = "parallel", Branches = [new("work", branch)] };
        var plan = new GroundedPlan { Operations = captured ? [source, parallel] : [parallel] };
        var owner = GroundedTraversal.Blocks(plan).Single().Workflow;
        var scopes = GroundedReplanning.AffectedOwners(plan, [new("GROUNDED_CONTRACT_INVALID", "/scopes/" + owner + "/operations/consume", "Invalid producer binding")]);
        Assert.Equal(new[] { captured ? "main" : owner }, scopes);
    }

    [Fact]
    public void ReplanningExpandsAcrossNamedSubflowCalls()
    {
        var plan = new GroundedPlan
        {
            Operations = [new CallGroundedOperation { Id = "call", Flow = "shared" }],
            Subflows = [new("shared", [], [new CalculateGroundedOperation { Id = "source", Value = new() }], []), new("unrelated", [], [], [])]
        };
        var scopes = GroundedReplanning.AffectedOwners(plan, [new("GROUNDED_CONTRACT_INVALID", "/scopes/shared/operations/source", "Invalid producer")]);
        Assert.Equal(new[] { "main", "shared" }, scopes.Order(StringComparer.Ordinal));
    }
}
