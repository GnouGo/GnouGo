using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class PlanningLifecycleTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;
    [Fact]
    public async Task OneCallReachesReviewAndRestartDoesNotGenerateAgain()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.ModelCalls);
        Assert.Contains("not been observed", Assert.Single(state.ValidationResults).Description);
        state = PlannerFixture.Clone(state);
        var result = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(state.ComputeArtifactHash(), result.ComputeArtifactHash()); Assert.Single(runtime.Calls);
    }
    [Fact]
    public async Task OutputLimitStopsWithoutEscalation()
    {
        var runtime = new TestRuntime { Respond = (_, _) => new() { CompletionStatus = "output_limit" } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.Yaml);
    }
    [Fact]
    public async Task MalformedResponsesExhaustExistingAllowance()
    {
        var runtime = new TestRuntime(); runtime.Respond = (_, _) => new() { Json = new JsonObject { ["unknown"] = runtime.Calls.Count } };
        var state = PlannerFixture.Session(); state.Request.MaxModelCalls = 2;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Null(state.Yaml);
        var revised = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "revise", Text = "Keep the greeting", ExpectedRevision = state.Revision }, runtime, Ct);
        revised = await PlannerFixture.RunAsync(runtime, revised);
        Assert.Equal(2, revised.ModelCalls); Assert.Equal(2, runtime.Calls.Count);
    }
    [Fact]
    public async Task InterruptedDispatchRetainsIdentityAndReservation()
    {
        var runtime = new TestRuntime { Respond = (_, _) => throw new IOException("Interrupted") };
        var state = await PlannerFixture.RunAsync(runtime); var identity = state.PendingCall!.Id;
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(1, state.ModelCalls);
        runtime.Respond = (request, _) => TestRuntime.Response(request, runtime.Proposal);
        state.Status = PlanningStatus.Generating;
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.ModelCalls);
        Assert.All(runtime.Calls, c => Assert.Equal(identity, c.ClientRequestId));
    }
    [Theory]
    [InlineData(LLMClientFailureKind.InvalidRequest, false, 400, "MODEL_REQUEST_REJECTED")]
    [InlineData(LLMClientFailureKind.Authentication, false, 401, "MODEL_REQUEST_REJECTED")]
    [InlineData(LLMClientFailureKind.Authorization, false, 403, "MODEL_REQUEST_REJECTED")]
    [InlineData(LLMClientFailureKind.ModelUnavailable, false, 404, "MODEL_REQUEST_REJECTED")]
    [InlineData(LLMClientFailureKind.QuotaOrBilling, false, 429, "MODEL_REQUEST_REJECTED")]
    [InlineData(LLMClientFailureKind.RateLimited, true, 429, "MODEL_DISPATCH_UNVERIFIABLE")]
    [InlineData(LLMClientFailureKind.ServiceUnavailable, true, 503, "MODEL_DISPATCH_UNVERIFIABLE")]
    [InlineData(LLMClientFailureKind.Timeout, false, 408, "MODEL_DISPATCH_UNVERIFIABLE")]
    [InlineData(LLMClientFailureKind.Transport, false, 502, "MODEL_DISPATCH_UNVERIFIABLE")]
    [InlineData(LLMClientFailureKind.Unknown, false, 500, "MODEL_DISPATCH_UNVERIFIABLE")]
    public async Task ProviderFailureRetainsSafeClassificationWithoutRetryOrPrivateMessage(
        LLMClientFailureKind kind, bool retryable, int status, string code)
    {
        var runtime = new TestRuntime { Respond = (_, _) => throw new LLMClientException(kind,
            "PRIVATE_PROVIDER_BODY", retryable, status, "safe_error_code") };
        var state = await PlannerFixture.RunAsync(runtime);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Contains(kind.ToString(), diagnostic.Message);
        Assert.Contains("HTTP " + status, diagnostic.Message);
        Assert.Contains("safe_error_code", diagnostic.Message);
        Assert.DoesNotContain("PRIVATE_PROVIDER_BODY", diagnostic.Message);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Single(runtime.Calls);
        var pending = state.PendingCall!.Id;
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state),
            new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(pending, state.PendingCall!.Id); Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task ApprovalBindsGraphContractsAndExactRevision()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var planner = new HybridWorkflowPlanner();
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision - 1, ArtifactHash = state.ComputeArtifactHash() }, runtime, Ct));
        var tampered = PlannerFixture.Clone(state); tampered.Yaml += "\n# changed";
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(tampered, new() { Kind = "approve", ExpectedRevision = tampered.Revision, ArtifactHash = tampered.ComputeArtifactHash() }, runtime, Ct));
        runtime.CatalogChanges = [new("CONTRACT_CHANGED", "/", "Changed")];
        var rejected = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash() }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, rejected.Status); Assert.Null(rejected.ApprovedHash);
    }
    [Fact]
    public async Task ChoiceCompilesWithoutModelDispatchOrNewAllowance()
    {
        var runtime = new TestRuntime(); runtime.Proposal.Plan = GnOuGo.Planning.Examples.PlanningCorpus.Decision();
        var state = await PlannerFixture.RunAsync(runtime); var planner = new HybridWorkflowPlanner();
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => planner.AdvanceAsync(state, new() { Kind = "choose", ExpectedRevision = state.Revision, Selections = new() { ["tone"] = "unissued" } }, runtime, Ct));
        state = await planner.AdvanceAsync(state, new() { Kind = "choose", ExpectedRevision = state.Revision, Selections = new() { ["tone"] = "casual" } }, runtime, Ct);
        Assert.Equal(1, state.ModelCalls); Assert.Equal("casual", Assert.Single(state.GetChoices()).Selected);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Contains("Hi", state.Yaml);
    }
    [Fact]
    public async Task AutoModeSelectsTheValidatedRecommendationWithoutExtraCalls()
    {
        var runtime = new TestRuntime(); runtime.Proposal.Plan = GnOuGo.Planning.Examples.PlanningCorpus.Decision();
        var state = PlannerFixture.Session(); state.Request.Mode = PlanningMode.Auto;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal("formal", Assert.Single(state.GetChoices()).Selected); Assert.Single(runtime.Calls);
    }
    [Fact]
    public async Task SwitchingAPendingChoiceToAutoSelectsLocallyWithoutApproval()
    {
        var runtime = new TestRuntime(); runtime.Proposal.Plan = GnOuGo.Planning.Examples.PlanningCorpus.Decision();
        var state = await PlannerFixture.RunAsync(runtime); Assert.Equal(PlanningStatus.Clarification, state.Status);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "configure_mode", Mode = PlanningMode.Auto, ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal("formal", Assert.Single(state.GetChoices()).Selected);
        Assert.Equal(1, state.ModelCalls); Assert.Null(state.ApprovedHash);
    }
    [Fact]
    public async Task SchemaEightAndCancelledCallsCannotSpend()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.SchemaVersion = 8;
        var ex = await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct));
        Assert.Contains("Regenerate and approve", ex.Message); Assert.Empty(runtime.Calls);
        state.SchemaVersion = 10; using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, cancelled.Token));
        Assert.Empty(runtime.Calls);
    }
    [Fact]
    public async Task RevisionCannotReuseApprovalOrResetCumulativeCalls()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var planner = new HybridWorkflowPlanner();
        var hash = state.ComputeArtifactHash();
        state = await planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = hash }, runtime, Ct);
        Assert.Equal(hash, state.ApprovedHash);
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, Text = "Use a warmer greeting" }, runtime, Ct);
        Assert.Null(state.ApprovedHash); Assert.Null(state.Yaml); Assert.Equal(1, state.ModelCalls);
    }
}
