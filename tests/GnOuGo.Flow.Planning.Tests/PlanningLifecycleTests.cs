using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningLifecycleTests
{
    [Theory]
    [InlineData("configure_generation")]
    [InlineData("revise")]
    public async Task ExhaustedSessionsRejectRestartCommandsAtomically(string command)
    {
        var state = PlannerFixture.Session(); state.Status = PlanningStatus.Stopped; state.ModelCalls = state.Request.MaxModelCalls;
        state.Diagnostics = [new("LLM_BUDGET_EXCEEDED", "/", "The session model-call budget was exhausted.")];
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var runtime = new TestRuntime();
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state, new()
        { Kind = command, Text = "Requested change", Generation = new() { MaxInputTokensPerRequest = 64000 }, ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Empty(runtime.Calls); Assert.Empty(runtime.Checkpoints);
        var cancelled = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { Kind = "cancel" }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Cancelled, cancelled.Status);
        Assert.Equal(state.ModelCalls, cancelled.ModelCalls);
    }

    [Fact]
    public async Task FreshIncrementalPlanReachesReviewedArtifactAndRestartMakesNoModelCall()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("test", "current-lifecycle", null, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(5, state.ModelCalls);
        Assert.Single(state.Construction!.Checkpoints);
        var runtime = new TestRuntime { Respond = (_, _) => throw new InvalidOperationException("Final artifacts must not regenerate.") };
        var before = state.ComputeArtifactHash();
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(before, state.ComputeArtifactHash());
        Assert.Equal(5, state.ModelCalls);
        Assert.Empty(runtime.Calls);
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task OutputLimitStopsWithoutEscalationOrCorrection()
    {
        var runtime = new TestRuntime { Respond = (_, _) => new() { CompletionStatus = "output_limit" } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.Yaml);
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task InvalidReceivedResponseStopsAfterOneDiagnosticCorrection()
    {
        var runtime = new TestRuntime { Respond = (_, _) => new() { Json = new JsonObject { ["unknown"] = 1 } } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.Yaml); Assert.Equal(2, runtime.Calls.Count);
        Assert.NotEqual(runtime.Calls[0].ClientRequestId, runtime.Calls[1].ClientRequestId);
        Assert.NotEqual(runtime.Calls[0].Prompt, runtime.Calls[1].Prompt);
        Assert.Contains("diagnostics", runtime.Calls[1].Prompt, StringComparison.Ordinal);
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Fact]
    public async Task InterruptedDispatchRetainsItsIdentityAndCannotReplay()
    {
        var runtime = new TestRuntime { Respond = (_, _) => throw new IOException("Interrupted") };
        var state = await PlannerFixture.RunAsync(runtime);
        var identity = state.PendingCall!.Id;
        var calls = state.ModelCalls;
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal(identity, state.PendingCall!.Id);
        Assert.Equal(calls, state.ModelCalls);
        Assert.Single(runtime.Calls);
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
    public async Task ProviderFailureRetainsSafeClassificationWithoutPrivateMessageOrUncertainReplay(
        LLMClientFailureKind kind, bool retryable, int status, string code)
    {
        var runtime = new TestRuntime { Respond = (_, _) => throw new LLMClientException(kind, "PRIVATE_PROVIDER_BODY", retryable, status, "safe_error_code") };
        var state = await PlannerFixture.RunAsync(runtime);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Contains(kind.ToString(), diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP " + status, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("safe_error_code", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_PROVIDER_BODY", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(1, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Single(runtime.Calls);
        var pending = state.PendingCall!.Id;
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(pending, state.PendingCall!.Id); Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task ApprovalRejectsStaleRevisionAndChangedArtifact()
    {
        var state = await CurrentPlanningFixture.LiteralAsync("test", "approval-lifecycle", null, PlannerFixture.Ct);
        var runtime = new TestRuntime(); var planner = new HybridWorkflowPlanner();
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision - 1,
            ArtifactHash = state.ComputeArtifactHash(), ReviewedRequirementIds = ["req_0"] }, runtime, PlannerFixture.Ct));
        var tampered = PlannerFixture.Clone(state); tampered.Yaml += "\n# changed";
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(tampered, new() { Kind = "approve", ExpectedRevision = tampered.Revision,
            ArtifactHash = tampered.ComputeArtifactHash(), ReviewedRequirementIds = ["req_0"] }, runtime, PlannerFixture.Ct));
        Assert.Empty(runtime.Calls);
    }
}
