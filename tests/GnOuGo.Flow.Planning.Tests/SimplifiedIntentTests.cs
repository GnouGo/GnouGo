using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SimplifiedIntentTests
{
    [Fact]
    public async Task FreshBusinessResponseHasNoProofLayerAndNeedsOneCallWithoutRepair()
    {
        var state = PlannerFixture.Session(); var schema = PlanningSchemas.FullProposal(state, compact: false);
        Assert.Null(schema["properties"]!["outcomeBindings"]);
        var fields = schema["$defs"]!["requirements"]!["properties"]!["outcomes"]!["items"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "id", "description" }, fields.Select(p => p.Key));
        var runtime = new TestRuntime(); state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.IntentVersion);
        Assert.Null(state.OutcomeVersion); Assert.Null(state.OutcomeBindings);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.DoesNotContain("outcomeBindings", Assert.Single(runtime.Calls).StructuredOutputSchema!.ToJsonString());
        PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task HistoricalApprovalIsPreservedWhileAnUnapprovedCopyRequiresRevision(int? outcomeVersion)
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        state.IntentVersion = 1; state.OutcomeVersion = outcomeVersion;
        if (outcomeVersion is not null) state.OutcomeBindings = [new("message", ["greet"], ["message"])];
        var hash = state.ComputeArtifactHash();
        state.Status = PlanningStatus.Approved; state.ApprovedHash = hash;
        var stored = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(stored, JsonSerializer.Serialize(recovered, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(hash, recovered.ComputeArtifactHash()); PlanningArtifactApproval.Verify(recovered);
        state.Status = PlanningStatus.FinalReview; state.ApprovedHash = null;
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = hash }, runtime, PlannerFixture.Ct));
        var revised = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, Text = "Keep the greeting" }, runtime, PlannerFixture.Ct);
        Assert.Equal(2, revised.IntentVersion); Assert.Null(revised.Yaml); Assert.Null(revised.ApprovedHash);
        Assert.Equal(state.ModelCalls, revised.ModelCalls); Assert.NotNull(revised.Request.Baseline); Assert.Single(runtime.Calls);
        Assert.DoesNotContain("coverage", revised.Request.RevisionContext!); Assert.DoesNotContain("outcomeBindings", revised.Request.RevisionContext!);
    }
}
