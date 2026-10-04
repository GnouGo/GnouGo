using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class RequirementsReviewTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("partial")]
    [InlineData("stale")]
    public async Task ApprovalRequiresExactExplicitReviewWithoutChangingStateOrSpending(string variant)
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Requirements!.Outcomes.Add(new("visits", "For every item visit its resource, then extract its fields from a complete observation."));
        // A well-typed placeholder can compile, but unreviewed business work cannot be approved.
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var checkpoints = runtime.Checkpoints.Count;
        var command = new PlanningCommand { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            ReviewedRequirementIds = variant switch { "missing" => null, "empty" => [], "duplicate" => ["message", "message"],
                "unknown" => ["message", "other"], "partial" => ["message"], _ => ["message", "visits"] } };
        if (variant == "stale") command.ExpectedRevision--;
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, command, runtime, PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(checkpoints, runtime.Checkpoints.Count); Assert.Single(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task ExplicitReviewSurvivesRestartWithoutChangingArtifactIdentityAndRevisionClearsIt()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var hash = state.ComputeArtifactHash();
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { Kind = "approve", ExpectedRevision = state.Revision,
            ArtifactHash = hash, ReviewedRequirementIds = ["message"] }, runtime, PlannerFixture.Ct);
        state = PlannerFixture.Clone(state);
        Assert.Equal(hash, state.ComputeArtifactHash()); Assert.Equal(hash, state.ApprovedHash);
        Assert.Equal("human_reviewed", Assert.Single(state.ValidationResults, r => r.Id == "requirement:message").Outcome);
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, Text = "Use a warmer greeting." }, runtime, PlannerFixture.Ct);
        Assert.Null(state.ApprovedHash); Assert.Empty(state.ValidationResults); Assert.Single(runtime.Calls);
    }

    [Fact]
    public void AbsentReviewCommandFieldKeepsHistoricalSerialization()
    {
        Assert.DoesNotContain("reviewedRequirementIds", JsonSerializer.Serialize(new PlanningCommand(), PlanningJsonContext.Default.PlanningCommand));
    }
}
