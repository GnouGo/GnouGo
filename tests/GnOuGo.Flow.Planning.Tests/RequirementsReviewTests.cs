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
        Assert.DoesNotContain("preserveRequirements", JsonSerializer.Serialize(new PlanningCommand(), PlanningJsonContext.Default.PlanningCommand));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewCorrectionRetainsIntentAndAccountingAcrossRestart(bool changeRequirements)
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        var accepted = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var oldPlan = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var discovery = JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState);
        var original = PlannerFixture.Clone(state); var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            PreserveRequirements = true, Text = "Correct the implementation; keep the accepted business interface." }, runtime, PlannerFixture.Ct);
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(oldPlan, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(discovery, JsonSerializer.Serialize(state.Discovery, PlanningJsonContext.Default.CapabilityDiscoveryState));
        Assert.Equal(original.ModelCalls, state.ModelCalls); Assert.Equal(original.ReplanAttempts, state.ReplanAttempts);
        Assert.Equal(original.Usage?.TotalTokens, state.Usage?.TotalTokens);
        Assert.Equal(original.Request.TenantId, state.Request.TenantId);
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash); Assert.Empty(state.ValidationResults);
        if (changeRequirements)
        {
            runtime.Proposal.Requirements!.Outcomes.Clear();
            runtime.Respond = (request, _) =>
            {
                var response = TestRuntime.Response(request, runtime.Proposal);
                response.Json!["requirements"] = JsonSerializer.SerializeToNode(runtime.Proposal.Requirements, PlanningJsonContext.Default.PlanningRequirements);
                return response;
            };
        }
        else runtime.Proposal.Plan!.Root.Tasks[0].Objective = "Return the greeting with corrected implementation";
        state = await planner.AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(2, state.ModelCalls);
        if (changeRequirements) { Assert.NotEqual(PlanningStatus.FinalReview, state.Status); Assert.Null(state.Yaml); }
        else { Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.NotEqual(original.ComputeArtifactHash(), state.ComputeArtifactHash()); }
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("pending")]
    [InlineData("exhausted")]
    [InlineData("no_intent")]
    public async Task ReviewCorrectionCannotBypassOwnershipOrAccounting(string variant)
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        var command = new PlanningCommand { Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            PreserveRequirements = true, Text = "Correct implementation" };
        if (variant == "stale") command.ArtifactHash = "old-artifact";
        if (variant == "exhausted") state.ModelCalls = state.Request.MaxModelCalls;
        if (variant == "no_intent") state.Requirements = null;
        if (variant == "pending") state.PendingCall = new() { Id = "uncertain" };
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession); var checkpoints = runtime.Checkpoints.Count;
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, command, runtime, PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Single(runtime.Calls); Assert.Equal(checkpoints, runtime.Checkpoints.Count);
    }
}
