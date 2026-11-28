using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Agent.Server.Tests;

public sealed class ServerLiveReviewWaitTests
{
    [Theory]
    [InlineData("other_dialogs")]
    [InlineData("unchanged_revision")]
    [InlineData("changed_input")]
    [InlineData("cancelled")]
    [InlineData("revoked")]
    public async Task OneTimeAnswerSurvivesOnlyUnrelatedJournalUpdates(string scenario)
    {
        var dialog = new WorkflowInvocation { Id = "read", ParentInvocationId = "review", Status = "waiting_for_human",
            ResolvedInput = new JsonObject { ["path"] = "requested.txt" } };
        var run = new WorkflowRun { Revision = 10 };
        run.Invocations[dialog.Id] = dialog;
        var attempts = 0;
        Task Record(long revision, CancellationToken token)
        {
            Assert.Equal(run.Revision, revision);
            if (++attempts == 12) return Task.CompletedTask;
            if (scenario != "unchanged_revision") run.Revision++;
            if (scenario == "changed_input") run.Invocations[dialog.Id] = new()
            {
                Id = dialog.Id, ParentInvocationId = dialog.ParentInvocationId, Status = dialog.Status,
                ResolvedInput = new JsonObject { ["path"] = "different.txt" }
            };
            run.CancelRequested = scenario == "cancelled";
            throw new WorkflowRunConflictException("Concurrent journal write.");
        }

        var operation = ServerBusinessExecution.RecordReviewedChoiceAsync(dialog, _ => Task.FromResult<WorkflowRun?>(run),
            Record, () => scenario != "revoked" || attempts == 0, TestContext.Current.CancellationToken);
        if (scenario == "other_dialogs")
        {
            await operation;
            Assert.Equal(12, attempts);
        }
        else
        {
            if (scenario == "unchanged_revision") await Assert.ThrowsAsync<WorkflowRunConflictException>(() => operation);
            else await Assert.ThrowsAsync<PlanningConflictException>(() => operation);
            Assert.Equal(1, attempts);
        }
    }

    [Theory]
    [InlineData("server-live-plan", false)]
    [InlineData("server-live-execute", true)]
    [InlineData("server-live-inspect", false)]
    public void ClarificationWaitRequiresFreshInteractivePlanning(string mode, bool includeReview)
    {
        string[] args = ["--schema-portability", mode, "--await-planning-clarification"];
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.RequiresInteraction(includeReview ? [.. args, "--await-delegated-review"] : args));
        Assert.True(ServerLiveDiagnostics.RequiresInteraction(["--schema-portability", "server-live-plan", "--await-delegated-review", "--await-planning-clarification"]));
        Assert.Throws<ArgumentException>(() => ServerLiveDiagnostics.RequiresInteraction(["--schema-portability", mode, "--await-scoped-revision"]));
        Assert.True(ServerLiveDiagnostics.RequiresInteraction(["--schema-portability", "server-live-plan", "--await-delegated-review", "--await-scoped-revision"]));
    }

    [Fact]
    public async Task StoppedBusinessReviewWaitsOnlyForAnExplicitNewRevision()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("benchmark", "stopped-revision", null, TestContext.Current.CancellationToken);
        state.Status = PlanningStatus.Stopped;
        state.ReplanAttempts = 1;
        state.Request.MaxReplanAttempts = 2;
        state.Diagnostics = [new("BUSINESS_REVIEW_REQUIRED", "/construction/candidate", "The fallback loses the observed status.")];
        var candidate = state.Construction!.Candidate = new() { Hash = "current", Reviews = [new JsonObject
            { ["outcome"] = "needs_change", ["candidateHash"] = "current", ["defectWitness"] = new JsonObject() }] };
        Assert.True(ServerLiveReviewWait.CanOfferScopedRevision(state));
        var snapshot = Serialize(state);
        using var expired = new CancellationTokenSource();
        expired.Cancel();
        Assert.False(await ServerLiveReviewWait.AwaitNewRevisionAsync(state, () => throw new InvalidOperationException(), expired.Token));
        var latest = Clone(state);
        latest.Revision++; latest.Status = PlanningStatus.Generating;
        Assert.True(await ServerLiveReviewWait.AwaitNewRevisionAsync(state, () => Task.FromResult<PlanningSession?>(latest), TestContext.Current.CancellationToken));
        Assert.Equal(snapshot, Serialize(state));
        candidate.Hash = "newer";
        Assert.False(ServerLiveReviewWait.CanOfferScopedRevision(state));
        candidate.Hash = "current";
        state.ReplanAttempts = 2;
        Assert.False(ServerLiveReviewWait.CanOfferScopedRevision(state));
        state.ReplanAttempts = 1;
        state.Diagnostics = [new("LLM_BUDGET_EXCEEDED", "/budget", "Budget exhausted.")];
        Assert.False(ServerLiveReviewWait.CanOfferScopedRevision(state));
    }

    [Fact]
    public async Task StoppedScopedResumptionRequiresTheExactUnexecutedRevisionAndRepeatedStrategy()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("benchmark", "retained-scope", null, TestContext.Current.CancellationToken);
        state.Status = PlanningStatus.Stopped;
        state.Request.MaxReplanAttempts = 2;
        state.Diagnostics = [new("REPLAN_NO_PROGRESS", "/construction", "Repeated range-check strategy.")];
        var construction = state.Construction!;
        construction.RebuildTasks = [state.Plan!.Root.Tasks[0]];
        construction.Invalidations = [new() { Reason = "Rebuild the original range guard." }];
        state.StrategyAttempts = [new() { ["identity"] = "range" }, new() { ["identity"] = "range" }];
        var previous = new JsonObject { ["session_id"] = state.Request.SessionId, ["session"] = JsonNode.Parse(Serialize(state)) };
        var revision = state.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ServerLiveDiagnostics.RequireScopedResume(previous, state, revision);
        Assert.Throws<PlanningConflictException>(() => ServerLiveDiagnostics.RequireScopedResume(previous, state, "-1"));
        previous["execution_started"] = true;
        Assert.Throws<PlanningConflictException>(() => ServerLiveDiagnostics.RequireScopedResume(previous, state, revision));
        previous.Remove("execution_started");
        previous["session"]!["modelCalls"] = state.ModelCalls + 1;
        Assert.Throws<PlanningConflictException>(() => ServerLiveDiagnostics.RequireScopedResume(previous, state, revision));
        state.StrategyAttempts.RemoveAt(0);
        Assert.False(ServerLiveReviewWait.CanOfferScopedRevision(state));
    }

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
