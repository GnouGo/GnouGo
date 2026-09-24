namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotHumanInputRouterTests
{
    [Fact]
    public async Task BackgroundCallbacks_UseActiveInvocationAndIsolateConcurrentSessions()
    {
        var host = new AmbientHost();
        var first = new CopilotHumanInputRouter(host);
        var second = new CopilotHumanInputRouter(host);
        host.Current.Value = "create-request";
        using (first.Bind(new("tenant-a"))) { }
        host.Current.Value = "send-request-a";
        using var a = first.Bind(new("tenant-a", StepId: "send-a"));
        host.Current.Value = "send-request-b";
        using var b = second.Bind(new("tenant-b", StepId: "send-b"));
        var ct = TestContext.Current.CancellationToken;
        Task<CopilotHumanInputResponse> pendingA;
        Task<CopilotHumanInputResponse> pendingB;
        using (ExecutionContext.SuppressFlow())
        {
            pendingA = Task.Run(() => first.RequestAsync(Question(), ct), ct);
            pendingB = Task.Run(() => second.RequestAsync(Question(), ct), ct);
        }
        var answers = await Task.WhenAll(pendingA, pendingB);
        Assert.Equal("send-request-a/tenant-a/send-a", answers[0].Answer);
        Assert.Equal("send-request-b/tenant-b/send-b", answers[1].Answer);
        a.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.RequestAsync(Question(), TestContext.Current.CancellationToken));
    }

    private static CopilotHumanInputRequest Question() => new(new("stale-creation-tenant"), "permission", "Allow?", ["Allow once"], false);
    private sealed class AmbientHost : ICopilotHumanInputProvider
    {
        public AsyncLocal<string> Current { get; } = new();
        public ICopilotHumanInputProvider Capture() => new Captured(Current.Value!);
        public Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Background dispatch must use a captured host context.");
        private sealed class Captured(string tag) : ICopilotHumanInputProvider
        {
            public Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken cancellationToken)
                => Task.FromResult(new CopilotHumanInputResponse(true, tag + "/" + request.Context.TenantId + "/" + request.Context.StepId));
        }
    }
}
