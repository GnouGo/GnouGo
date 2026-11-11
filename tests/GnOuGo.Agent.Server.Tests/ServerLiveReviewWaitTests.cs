using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ServerLiveReviewWaitTests
{
    [Fact]
    public async Task ActualScopedRevisionReturnsToRepairWithoutResettingAcceptedWorkOrUsage()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("benchmark", "review-scoped", null, TestContext.Current.CancellationToken);
        var before = Serialize(state); var task = state.Plan!.Root.Tasks.First(t => t.Outputs.Count > 0);
        var runtime = ServerLiveArtifactValidation.Runtime(null, state.Request.Policy, (_, _) => Task.CompletedTask);
        var revised = await new HybridWorkflowPlanner().AdvanceAsync(state, new()
        {
            Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(), PreserveRequirements = true,
            EditablePaths = [$"/tasks/{task.Id}/outputs/{task.Outputs[0].Name}"], Text = "Correct only this owned result binding."
        }, runtime, TestContext.Current.CancellationToken);
        Assert.False(await ServerLiveReviewWait.WaitAsync(state, () => Task.FromResult<PlanningSession?>(revised),
            () => false, () => false, () => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(before, Serialize(state));
        Assert.Equal(state.ModelCalls, revised.ModelCalls); Assert.Equal(state.ReplanAttempts, revised.ReplanAttempts);
        Assert.Equal(JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot),
            JsonSerializer.Serialize(revised.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan), JsonSerializer.Serialize(revised.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.NotNull(revised.Construction!.Candidate); Assert.Null(revised.ApprovedHash);
    }

    [Theory]
    [InlineData(PlanningStatus.Generating)]
    [InlineData(PlanningStatus.FinalReview)]
    [InlineData(PlanningStatus.Cancelled)]
    public async Task NewRevisionWithoutReviewReturnsToTheExistingCoordinator(string status)
    {
        var original = await CurrentPlanningFixture.LiteralAsync("benchmark", "review-wait", null, TestContext.Current.CancellationToken);
        var before = Serialize(original);
        var latest = Clone(original); latest.Revision++; latest.Status = status;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Assert.False(await ServerLiveReviewWait.WaitAsync(original, () => Task.FromResult<PlanningSession?>(latest),
            () => false, () => false, () => Task.CompletedTask, stop.Token));
        Assert.Equal(before, Serialize(original));
        Assert.Null(latest.ApprovedHash);
    }

    [Fact]
    public async Task ExactReviewStillChecksCancellationBeforeApproval()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("benchmark", "review-cancelled", null, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PlanningConflictException>(() => ServerLiveReviewWait.WaitAsync(state,
            () => Task.FromResult<PlanningSession?>(state), () => true, () => true,
            () => throw new PlanningConflictException("Cancellation is pending."), TestContext.Current.CancellationToken));
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevisedArtifactRejectsPartialOrCompleteStaleReviewFiles(bool complete)
    {
        var state = await CurrentPlanningFixture.LiteralAsync("benchmark", "review-stale", null, TestContext.Current.CancellationToken);
        var latest = Clone(state); latest.Revision++;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAsync<PlanningConflictException>(() => ServerLiveReviewWait.WaitAsync(state,
            () => Task.FromResult<PlanningSession?>(latest), () => true, () => complete,
            () => Task.CompletedTask, stop.Token));
    }

    [Fact]
    public async Task CompleteExactReviewBecomesReadyWithoutChangingTheArtifact()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("benchmark", "review-ready", null, TestContext.Current.CancellationToken);
        var before = Serialize(state); var checks = 0;
        Assert.True(await ServerLiveReviewWait.WaitAsync(state, () => Task.FromResult<PlanningSession?>(state),
            () => true, () => true, () => { checks++; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.Equal(1, checks); Assert.Equal(before, Serialize(state)); Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tenant")]
    [InlineData("session")]
    [InlineData("rollback")]
    [InlineData("artifact")]
    public async Task MissingOrInconsistentSessionCannotBecomeReviewReady(string change)
    {
        var state = await CurrentPlanningFixture.LiteralAsync("benchmark", "review-owner", null, TestContext.Current.CancellationToken);
        var latest = Clone(state);
        switch (change)
        {
            case "tenant": latest.Request.TenantId = "another"; break;
            case "session": latest.Request.SessionId = "another"; break;
            case "rollback": latest.Revision--; break;
            case "artifact": latest.Graph = null; break;
        }
        await Assert.ThrowsAsync<PlanningConflictException>(() => ServerLiveReviewWait.WaitAsync(state,
            () => Task.FromResult(change == "missing" ? null : latest), () => true, () => true,
            () => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    private static string Serialize(PlanningSession state) => JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
    private static PlanningSession Clone(PlanningSession state) => JsonSerializer.Deserialize(Serialize(state), PlanningJsonContext.Default.PlanningSession)!;
}
