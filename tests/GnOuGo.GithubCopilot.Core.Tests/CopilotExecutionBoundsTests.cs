using System.Text.Json.Nodes;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotExecutionBoundsTests
{
    [Fact]
    public async Task ClosingAnInterruptedTaskWaitsForItsReservationAndPreventsLateInference()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bounds = new CopilotExecutionBounds(4, 100000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>(), async (_, _, _) => { entered.SetResult(); await release.Task; });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions") { Content = new StringContent("{\"messages\":[]}") };
        var reserving = bounds.ReserveAsync(request, TestContext.Current.CancellationToken);
        await entered.Task;
        var stopped = bounds.StopAsync(TestContext.Current.CancellationToken); Assert.False(stopped.IsCompleted); Assert.True(bounds.Stopped);
        release.SetResult(); await reserving; await stopped;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bounds.ReserveAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, bounds.ModelCalls);
    }
    [Fact]
    public async Task ReservationsAreDurableBeforeDispatchAndNeverReplenished()
    {
        var reservations = new List<(int, long)>();
        var bounds = new CopilotExecutionBounds(1, 10000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>(),
            (calls, tokens, _) => { reservations.Add((calls, tokens)); return Task.CompletedTask; });
        using var first = Request("{\"input\":\"Hello\",\"max_output_tokens\":9000}");
        await bounds.ReserveAsync(first, TestContext.Current.CancellationToken);
        Assert.Single(reservations); Assert.Equal((1, 10000L), reservations[0]);
        var wire = JsonNode.Parse(await first.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.InRange(wire!["max_output_tokens"]!.GetValue<long>(), 1, 5904);
        using var retry = Request("{\"input\":\"Retry\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => bounds.ReserveAsync(retry, TestContext.Current.CancellationToken));
        Assert.Single(reservations); Assert.True(bounds.Exhausted);
    }
    [Theory]
    [InlineData("{\"previous_response_id\":\"opaque\",\"input\":\"next\"}")]
    [InlineData("{\"input\":[{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,abc\"}]}")]
    [InlineData("{\"input\":\"hello\",\"n\":2}")]
    public async Task UnsupportedAccountingCannotDispatch(string body)
    {
        var calls = 0;
        var bounds = new CopilotExecutionBounds(4, 100000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>(),
            (_, _, _) => { calls++; return Task.CompletedTask; });
        using var request = Request(body);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bounds.ReserveAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(0, calls); Assert.Equal(0, bounds.ModelCalls);
    }
    [Fact]
    public async Task FailedReservationDoesNotGiveBackItsAllowance()
    {
        var bounds = new CopilotExecutionBounds(1, 10000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string>(),
            (_, _, _) => throw new IOException("Injected journal crash"));
        using var request = Request("{\"input\":\"hello\"}");
        await Assert.ThrowsAsync<IOException>(() => bounds.ReserveAsync(request, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bounds.ReserveAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, bounds.ModelCalls);
    }
    [Fact]
    public async Task ScopeHooksRejectExpansionEvenWithBroadPermissions()
    {
        var bounds = new CopilotExecutionBounds(4, 10000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string> { "project_read" }, (_, _, _) => Task.CompletedTask);
        var config = new CopilotRuntimeConfiguration(Path.GetTempPath(), "model", EnableApproveAll: true) { ExecutionBounds = bounds };
        await using var client = new GitHubCopilotSdkClient(new CopilotClient(new CopilotClientOptions()), config, NullLogger.Instance);
        var source = new CopilotSdkSessionConfiguration(new(new("tenant"), config, PermissionMode: CopilotPermissionMode.ApproveAll), null, null);
        var hooks = client.BuildCreateConfig(source).Hooks!;
        var denied = await hooks.OnPreToolUse!(new() { ToolName = "project_write" }, new());
        Assert.Equal("deny", denied!.PermissionDecision);
        var permission = await GitHubCopilotSdkClient.BuildPermissionHandler(source)(new PermissionRequestShell { FullCommandText = "echo escaped", RequestSandboxBypass = true, CanOfferSessionApproval = false, Commands = [], HasWriteFileRedirection = false, Intention = "test", PossiblePaths = [], PossibleUrls = [] }, new());
        Assert.IsType<PermissionDecisionReject>(permission);
    }
    [Fact]
    public async Task BoundedCommandsBootstrapPolicyWithoutChangingAuthenticationOrProvider()
    {
        var bounds = new CopilotExecutionBounds(1, 10000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string> { "bash" }, (_, _, _) => Task.CompletedTask);
        var config = new CopilotRuntimeConfiguration(Path.GetTempPath(), "model", GitHubToken: "test-token") { ExecutionBounds = bounds };
        await using var client = new GitHubCopilotSdkClient(new CopilotClient(new CopilotClientOptions()), config, NullLogger.Instance);
        var provider = new CopilotProviderResolution("configured", "model", new GitHub.Copilot.ProviderConfig { Type = "openai", BaseUrl = "https://provider.example/v1" });
        var source = new CopilotSdkSessionConfiguration(new(new("tenant"), config), provider, null);
        var create = client.BuildCreateConfig(source);
        var resume = client.BuildResumeConfig(source);
        Assert.True(create.EnableManagedSettings); Assert.Null(create.GitHubToken); Assert.Same(provider.Provider, create.Provider);
        Assert.Equal(DisableBypassPermissionsModes.Disable, create.ManagedSettings!.Permissions!.DisableBypassPermissionsMode);
        Assert.True(resume.EnableManagedSettings); Assert.Null(resume.GitHubToken); Assert.Equal(DisableBypassPermissionsModes.Disable, resume.ManagedSettings!.Permissions!.DisableBypassPermissionsMode);
        Assert.Same(provider.Provider, resume.Provider);
        var missing = source with { Request = source.Request with { Configuration = config with { GitHubToken = null } } };
        Assert.True(client.BuildCreateConfig(missing).EnableManagedSettings);
    }
    [Fact]
    public async Task RetainedCountersRejectNextInputWithoutChargingOrExposingContent()
    {
        var writes = 0;
        var bounds = new CopilotExecutionBounds(20, 200000, DateTimeOffset.UtcNow.AddMinutes(30), new HashSet<string>(),
            (_, _, _) => { writes++; return Task.CompletedTask; });
        // Four complete wire reservations total exactly the retained 167,402-token ceiling.
        foreach (var desired in new[] { 40000, 40000, 40000, 47402 })
        {
            using var probe = Request("{\"input\":\"\",\"max_output_tokens\":8192}");
            var baseline = System.Text.Encoding.UTF8.GetByteCount(JsonNode.Parse(await probe.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))!.ToJsonString());
            using var request = Request(new JsonObject { ["input"] = new string('x', desired - 4096 - 8192 - baseline), ["max_output_tokens"] = 8192 }.ToJsonString());
            await bounds.ReserveAsync(request, TestContext.Current.CancellationToken);
        }
        Assert.Equal(167402, bounds.ChargedTokens); Assert.Equal(4, bounds.ModelCalls);
        using var next = Request(new JsonObject { ["input"] = new string('y', 35000) }.ToJsonString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => bounds.ReserveAsync(next, TestContext.Current.CancellationToken));
        Assert.Equal(4, writes); Assert.Equal(167402, bounds.ChargedTokens);
        Assert.Equal(CopilotAdmissionStopKind.Tokens, bounds.AdmissionStop!.Kind);
        Assert.True(bounds.AdmissionStop.RequiredInputTokens > 200000 - 167402);
        var json = System.Text.Json.JsonSerializer.Serialize(bounds.AdmissionStop, CopilotCoreJsonContext.Default.CopilotAdmissionStop);
        Assert.DoesNotContain("yyyy", json); Assert.DoesNotContain("xxxx", json);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AdmissionReasonsDistinguishCallAndDeadlineCeilings(bool deadline)
    {
        var bounds = new CopilotExecutionBounds(0, 200000, deadline ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddHours(1),
            new HashSet<string>(), (_, _, _) => throw new InvalidOperationException("Must not charge"));
        using var request = Request("{\"input\":\"x\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => bounds.ReserveAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(deadline ? CopilotAdmissionStopKind.Deadline : CopilotAdmissionStopKind.Calls, bounds.AdmissionStop!.Kind);
        Assert.Equal(0, bounds.ModelCalls);
    }

    [Fact]
    public async Task StopDrainsAdmittedFileWorkAndRejectsLateOperationsEvenAfterTimeout()
    {
        var bounds = new CopilotExecutionBounds(5, 200000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string> { "project_write" }, (_, _, _) => Task.CompletedTask);
        var work = bounds.EnterFileOperation("project_write");
        using var cancel = new CancellationTokenSource();
        var stopping = bounds.StopAsync(cancel.Token);
        Assert.False(stopping.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => bounds.EnterFileOperation("project_write"));
        await cancel.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
        work.Dispose(); work.Dispose();
        await bounds.StopAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => bounds.EnterFileOperation("project_write"));
    }

    [Fact]
    public async Task SdkAdmissionsCannotDisappearBetweenHookAndExecutionEvent()
    {
        var bounds = new CopilotExecutionBounds(5, 200000, DateTimeOffset.UtcNow.AddMinutes(1), new HashSet<string> { "shell" }, (_, _, _) => Task.CompletedTask);
        Assert.True(bounds.TryAdmitSdkTool("shell"));
        await bounds.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(bounds.TryAdmitSdkTool("shell")); Assert.False(bounds.ToolAdmissionsObserved(0));
        Assert.False(bounds.ToolAdmissionsObserved(1));
        bounds.CompleteSdkTool("shell"); Assert.True(bounds.ToolAdmissionsObserved(1));
        bounds.CompleteSdkTool("shell"); Assert.False(bounds.ToolAdmissionsObserved(1));
    }

    private static HttpRequestMessage Request(string json) => new(HttpMethod.Post, "https://provider.example/v1/responses") { Content = new StringContent(json) };
}
