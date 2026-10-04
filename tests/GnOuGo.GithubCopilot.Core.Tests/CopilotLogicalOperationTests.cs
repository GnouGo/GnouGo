using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotLogicalOperationTests
{
    [Theory]
    [InlineData("complete", true)]
    [InlineData("missing-completion", false)]
    [InlineData("idle-before-error", false)]
    [InlineData("aborted", false)]
    [InlineData("autopilot", false)]
    [InlineData("transport", false)]
    [InlineData("cancelled", false)]
    [InlineData("other-error", false)]
    [InlineData("bounded", false)]
    public void OnlyVerifiedContextLimitIdleAllowsInternalContinuation(string scenario, bool expected)
    {
        var events = new CopilotExecutionObservations();
        var budget = new CopilotInferenceBudget(32, 2000000, DateTimeOffset.UtcNow.AddMinutes(30), (_, _, _) => Task.CompletedTask);
        events.Observe(new ToolExecutionStartEvent { Data = new() { ToolCallId = "external", ToolName = "anything" } });
        if (scenario != "missing-completion") events.Observe(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "external", Success = true } });
        var idle = new SessionIdleEvent { Data = new() { Aborted = scenario == "aborted", Mode = scenario == "autopilot" ? SessionMode.Autopilot : SessionMode.Interactive } };
        if (scenario == "idle-before-error") events.Observe(idle);
        events.Observe(new SessionErrorEvent { Data = new() { ErrorType = scenario == "other-error" ? "query" : "context_limit", Message = "Private provider payload" } });
        if (scenario != "idle-before-error") events.Observe(idle);
        if (scenario == "transport") budget.RecordTransportFailure();
        using var cancel = new CancellationTokenSource(); if (scenario == "cancelled") cancel.Cancel();
        var bounds = scenario == "bounded" ? new CopilotExecutionBounds(1, 10000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>(), (_, _, _) => Task.CompletedTask) : null;
        var result = events.Interrupted("handle", "session", new InvalidOperationException(), bounds, cancel.Token, budget);
        Assert.Equal(expected, result.VerifiedContinuation); Assert.False(result.Snapshot.Completed);
    }

    [Fact]
    public async Task DenialSurvivesSuccessorEvenWhenItFindsBroadApproval()
    {
        var recorded = new List<string>();
        var permissions = new CopilotLogicalPermissions((key, _) => { recorded.Add(key); return Task.CompletedTask; });
        var request = new PermissionRequestRead { Path = "/workspace/private", Intention = "First intention", RequestSandboxBypass = false };
        var configuration = new CopilotRuntimeConfiguration("/workspace", "mock", EnableApproveAll: true) { LogicalPermissions = permissions };
        var refusing = GitHubCopilotSdkClient.BuildPermissionHandler(new(new(new("tenant"), configuration, PermissionMode: CopilotPermissionMode.Deny), null, null));
        Assert.IsType<PermissionDecisionReject>(await refusing(request, new()));
        var successor = GitHubCopilotSdkClient.BuildPermissionHandler(new(new(new("tenant"), configuration, PermissionMode: CopilotPermissionMode.ApproveAll), null, null));
        request.Intention = "Changed explanation"; request.RequestSandboxBypass = true;
        Assert.IsType<PermissionDecisionReject>(await successor(request, new()));
        Assert.Single(recorded); Assert.DoesNotContain("private", recorded[0]);
        Assert.IsType<PermissionDecisionApproveOnce>(await successor(new PermissionRequestRead { Path = "/workspace/other", Intention = "Other read" }, new()));
    }

    [Fact]
    public async Task RetainedBudgetCannotRenewCallsOrReservationsAndStopClosesAdmission()
    {
        var persisted = new List<(int, long)>();
        var budget = new CopilotInferenceBudget(2, 100000, DateTimeOffset.UtcNow.AddMinutes(1),
            (calls, tokens, _) => { persisted.Add((calls, tokens)); return Task.CompletedTask; }, initialCalls: 1, initialTokens: 20000);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/responses")
        { Content = new StringContent("{\"input\":\"hello\",\"max_output_tokens\":1}") };
        await budget.ReserveAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(2, budget.ModelCalls); Assert.True(budget.ChargedTokens > 20000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.ReserveAsync(request, TestContext.Current.CancellationToken));
        Assert.Single(persisted); await budget.StopAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.ReserveAsync(request, TestContext.Current.CancellationToken));
    }
}
