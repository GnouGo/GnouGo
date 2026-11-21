using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FreshConstructionAuthorityTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("2")]
    [InlineData("true")]
    [InlineData("\"unknown\"")]
    [InlineData("1")]
    public async Task ExplicitMarkerCannotAcquireMissingBusinessMetadata(string marker)
    {
        var state = Fresh();
        state.Request.Options["business_selection_contract"] = JsonNode.Parse(marker);
        await RejectUnchanged(state);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitMarkerWithoutConstructionMetadataIsRejectedUnchanged(bool missingConstruction)
    {
        var state = Fresh();
        state.Request.Options["business_selection_contract"] = 1;
        if (missingConstruction) state.Construction = null;
        else state.Construction!.Compact = null;
        await RejectUnchanged(state);
    }

    [Theory]
    [InlineData("empty-blocks")]
    [InlineData("recorded-block")]
    [InlineData("active-block")]
    [InlineData("technical-cursor")]
    [InlineData("suspended-consumer")]
    [InlineData("candidate")]
    [InlineData("checkpoint")]
    public async Task ExistingConstructionWorkCannotAcquireMissingContract(string kind)
    {
        var state = Fresh();
        var construction = state.Construction!;
        var compact = construction.Compact!;
        switch (kind)
        {
            case "empty-blocks": compact.Blocks = []; break;
            case "recorded-block": compact.Blocks = [new() { Id = "block_1", Objective = "Retained responsibility" }]; break;
            case "active-block": compact.CurrentBlockId = "block_1"; break;
            case "technical-cursor": compact.CurrentStep = new() { Id = "step_1", Kind = "value", Objective = "Retained technical step" }; break;
            case "suspended-consumer": compact.SuspendedSteps = [new() { Id = "consumer_1" }]; break;
            case "candidate": construction.Candidate = new(); break;
            case "checkpoint": construction.Checkpoints.Add(new() { Sequence = 1 }); break;
        }
        await RejectUnchanged(state);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("pending")]
    [InlineData("plan")]
    [InlineData("calls")]
    public async Task FreshConstructorDoesNotBypassExistingCapturePreconditions(string condition)
    {
        var state = Fresh();
        switch (condition)
        {
            case "revision": state.Revision = 1; break;
            case "pending": state.PendingCall = new() { Id = "issued", Purpose = "construction_intent" }; break;
            case "plan": state.Plan = new(); break;
            case "calls": state.ModelCalls = 1; break;
        }
        await RejectUnchanged(state);
    }

    private static PlanningSession Fresh() => new()
    {
        Request = new() { TenantId = "fresh-authority", Prompt = "Return the required value.",
            Options = new() { ["construction_limits"] = JsonSerializer.SerializeToNode(new PlanningConstructionOptions(),
                PlanningJsonContext.Default.PlanningConstructionOptions) } },
        Construction = new() { Compact = new() }
    };

    private static async Task RejectUnchanged(PlanningSession state)
    {
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var runtime = new TestRuntime();
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(runtime.Calls);
        Assert.Empty(runtime.Checkpoints);
    }
}
