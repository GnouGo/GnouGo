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
        var stopped = bounds.StopAsync(); Assert.False(stopped.IsCompleted); Assert.True(bounds.Stopped);
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
    private static HttpRequestMessage Request(string json) => new(HttpMethod.Post, "https://provider.example/v1/responses") { Content = new StringContent(json) };
}
