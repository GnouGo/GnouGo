using System.Collections.Concurrent;
using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotSessionManagerTests
{
    [Fact]
    public async Task ManagedSession_SerializesConcurrentSends()
    {
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory);
        var descriptor = await manager.CreateAsync(CreateRequest("tenant-a"), TestContext.Current.CancellationToken);

        await Task.WhenAll(
            manager.SendAsync(new CopilotSendRequest(Context("tenant-a"), descriptor.Handle, "first"), TestContext.Current.CancellationToken),
            manager.SendAsync(new CopilotSendRequest(Context("tenant-a"), descriptor.Handle, "second"), TestContext.Current.CancellationToken));

        Assert.Equal(1, factory.LastSession!.MaxConcurrentSends);
    }

    [Fact]
    public async Task SessionHandle_IsTenantBound()
    {
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory);
        var descriptor = await manager.CreateAsync(CreateRequest("tenant-a"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.SendAsync(
            new CopilotSendRequest(Context("tenant-b"), descriptor.Handle, "attempt"),
            TestContext.Current.CancellationToken));
        Assert.Empty(manager.List("tenant-b"));
    }

    [Fact]
    public async Task DisconnectAndResume_UsesOpaqueHandleAndPreservesSdkSessionId()
    {
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory);
        var created = await manager.CreateAsync(CreateRequest("tenant-a"), TestContext.Current.CancellationToken);

        await manager.DisconnectAsync(Context("tenant-a"), created.Handle, TestContext.Current.CancellationToken);
        var resumed = await manager.ResumeAsync(new CopilotSessionResumeRequest(Context("tenant-a"), created.Handle), TestContext.Current.CancellationToken);

        Assert.True(resumed.Connected);
        Assert.Equal(created.CopilotSessionId, resumed.CopilotSessionId);
        Assert.Equal(created.Handle, resumed.Handle);
        Assert.Equal(1, factory.ResumeCount);
    }

    [Fact]
    public async Task OneShot_DeletesSessionState()
    {
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory);

        var result = await manager.OneShotAsync(CreateRequest("tenant-a", CopilotPermissionMode.Deny), "review", null, TestContext.Current.CancellationToken);

        Assert.Equal("reply:review", result.Content);
        Assert.Equal(1, factory.DeleteCount);
        Assert.Empty(manager.List("tenant-a"));
    }

    [Fact]
    public async Task InteractiveOneShot_UsesManagedInteractiveSessionAndDeletesState()
    {
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory);
        var progress = new List<CopilotStreamEvent>();

        var result = await manager.InteractiveOneShotAsync(
            CreateRequest("tenant-a", CopilotPermissionMode.Deny) with { SessionKind = CopilotSessionKind.OneShot },
            "install dependencies",
            null,
            TestContext.Current.CancellationToken,
            progress.Add);

        Assert.Equal("reply:install dependencies", result.Content);
        Assert.Equal(CopilotSessionKind.Managed, factory.LastConfiguration?.Request.SessionKind);
        Assert.Equal(CopilotPermissionMode.Interactive, factory.LastConfiguration?.Request.PermissionMode);
        Assert.Equal(1, factory.DeleteCount);
        Assert.Empty(manager.List("tenant-a"));
        Assert.Equal(
            ["session_create", "session_created", "request_send", "request_completed", "session_delete", "session_deleted"],
            progress.Select(static item => item.Kind));
    }

    [Fact]
    public async Task InteractiveOneShot_DeletesStateWhenSendFails()
    {
        var factory = new FakeClientFactory { SendException = new InvalidOperationException("send failed") };
        await using var manager = new CopilotSessionManager(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InteractiveOneShotAsync(
            CreateRequest("tenant-a"),
            "run tests",
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal("send failed", exception.Message);
        Assert.Equal(1, factory.DeleteCount);
        Assert.Empty(manager.List("tenant-a"));
    }

    [Fact]
    public async Task InteractiveOneShot_DeletesStateWhenSendIsCancelled()
    {
        var factory = new FakeClientFactory { SendDelay = TimeSpan.FromSeconds(5) };
        await using var manager = new CopilotSessionManager(factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InteractiveOneShotAsync(
            CreateRequest("tenant-a"),
            "run lint",
            null,
            cancellation.Token));

        Assert.Equal(1, factory.DeleteCount);
        Assert.Empty(manager.List("tenant-a"));
    }

    [Fact]
    public async Task InteractiveOneShot_ReportsCreateFailureWithoutAttemptingDeletion()
    {
        var factory = new FakeClientFactory { CreateException = new InvalidOperationException("create failed") };
        await using var manager = new CopilotSessionManager(factory);
        var progress = new List<CopilotStreamEvent>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InteractiveOneShotAsync(
            CreateRequest("tenant-a"),
            "run tests",
            null,
            TestContext.Current.CancellationToken,
            progress.Add));

        Assert.Equal("create failed", exception.Message);
        Assert.Equal(["session_create", "session_create_failed"], progress.Select(static item => item.Kind));
        Assert.Equal(0, factory.DeleteCount);
        Assert.Empty(manager.List("tenant-a"));
    }

    [Fact]
    public async Task InteractiveOneShot_PreservesPrimaryFailureWhenDeletionAlsoFails()
    {
        var factory = new FakeClientFactory
        {
            SendException = new InvalidOperationException("send failed"),
            DeleteException = new InvalidOperationException("delete failed")
        };
        await using var manager = new CopilotSessionManager(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InteractiveOneShotAsync(
            CreateRequest("tenant-a"),
            "run tests",
            null,
            TestContext.Current.CancellationToken));

        Assert.Equal("send failed", exception.Message);
        Assert.Equal("delete failed", exception.Data["CopilotSessionCleanupError"]);
        Assert.Equal(1, factory.DeleteCount);
        Assert.Empty(manager.List("tenant-a"));
    }

    [Fact]
    public async Task ApproveAll_RequiresHostPolicyGate()
    {
        await using var manager = new CopilotSessionManager(new FakeClientFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync(
            CreateRequest("tenant-a", CopilotPermissionMode.ApproveAll),
            TestContext.Current.CancellationToken));

        var enabled = CreateRequest("tenant-a", CopilotPermissionMode.ApproveAll) with
        {
            Configuration = Configuration() with { EnableApproveAll = true }
        };
        var result = await manager.CreateAsync(enabled, TestContext.Current.CancellationToken);
        Assert.Equal(CopilotPermissionMode.ApproveAll, result.PermissionMode);
    }

    [Fact]
    public async Task ExpiredSession_IsDeleted()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-03T00:00:00Z"));
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory, timeProvider: clock);
        var request = CreateRequest("tenant-a") with { Configuration = Configuration() with { ManagedSessionTtlSeconds = 2 } };
        await manager.CreateAsync(request, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(3));
        var deleted = await manager.SweepExpiredAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Equal(1, factory.DeleteCount);
    }

    [Fact]
    public async Task Cancellation_IsPropagatedToSend()
    {
        var factory = new FakeClientFactory { SendDelay = TimeSpan.FromSeconds(5) };
        await using var manager = new CopilotSessionManager(factory);
        var descriptor = await manager.CreateAsync(CreateRequest("tenant-a"), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SendAsync(
            new CopilotSendRequest(Context("tenant-a"), descriptor.Handle, "cancel"), cancellation.Token));
    }

    [Fact]
    public async Task ForegroundSession_IsReturnedAsTenantBoundOpaqueHandle()
    {
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory);
        var descriptor = await manager.CreateAsync(CreateRequest("tenant-a"), TestContext.Current.CancellationToken);

        await manager.SetForegroundAsync(Context("tenant-a"), descriptor.Handle, TestContext.Current.CancellationToken);
        var foreground = await manager.GetForegroundAsync(Context("tenant-a"), descriptor.Handle, TestContext.Current.CancellationToken);

        Assert.True(foreground.HasForeground);
        Assert.Equal(descriptor.Handle, foreground.Handle);
    }

    [Fact]
    public async Task WorkspaceFile_RejectsParentTraversal()
    {
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory);
        var descriptor = await manager.CreateAsync(CreateRequest("tenant-a"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.ReadWorkspaceFileAsync(
            Context("tenant-a"), descriptor.Handle, "../secret", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConfigurationDescription_ContainsCapabilitiesButNoCredentials()
    {
        var factory = new FakeClientFactory();
        await using var manager = new CopilotSessionManager(factory);
        var request = CreateRequest("tenant-a") with
        {
            Configuration = Configuration() with
            {
                GitHubToken = "do-not-return",
                PermissionAllowlist = ["view"],
                AvailableTools = ["read"],
                SkillDirectories = ["skills"],
                EnableConfigDiscovery = true
            }
        };
        var descriptor = await manager.CreateAsync(request, TestContext.Current.CancellationToken);

        var result = manager.DescribeConfiguration(Context("tenant-a"), descriptor.Handle);

        Assert.Equal(["view"], result.PermissionAllowlist);
        Assert.Equal(["read"], result.AvailableTools);
        Assert.True(result.ConfigDiscoveryEnabled);
        Assert.DoesNotContain("do-not-return", System.Text.Json.JsonSerializer.Serialize(result));
    }

    private static CopilotSessionCreateRequest CreateRequest(string tenant, CopilotPermissionMode permissionMode = CopilotPermissionMode.Deny)
        => new(Context(tenant), Configuration(), CopilotSessionKind.Managed, permissionMode);

    private static CopilotRequestContext Context(string tenant) => new(tenant, "correlation", "run", "step", "owner/repo", 12, "abcdef123456");

    private static CopilotRuntimeConfiguration Configuration()
        => new(Path.GetTempPath(), "test-model", ManagedSessionTtlSeconds: 60);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeClientFactory : ICopilotSdkClientFactory
    {
        private readonly ConcurrentDictionary<string, FakeSession> _sessions = new(StringComparer.Ordinal);
        public FakeSession? LastSession { get; private set; }
        public int DeleteCount;
        public int ResumeCount;
        public TimeSpan SendDelay { get; set; } = TimeSpan.FromMilliseconds(30);
        public Exception? CreateException { get; set; }
        public Exception? SendException { get; set; }
        public Exception? DeleteException { get; set; }
        public string? ForegroundSessionId { get; set; }
        public CopilotSdkSessionConfiguration? LastConfiguration { get; private set; }

        public ICopilotSdkClient Create(CopilotRuntimeConfiguration configuration) => new FakeClient(this);

        private sealed class FakeClient(FakeClientFactory owner) : ICopilotSdkClient
        {
            public string ConnectionState => "connected";
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<CopilotConnectivityResult> PingAsync(CancellationToken cancellationToken) => Task.FromResult(new CopilotConnectivityResult("ok", "now", "1"));
            public Task<CopilotStatusResult> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new CopilotStatusResult("1", "1", "connected"));
            public Task<CopilotAuthResult> GetAuthStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new CopilotAuthResult(true, "test", null, null, null));
            public Task<IReadOnlyList<CopilotModelResult>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CopilotModelResult>>([]);

            public Task<ICopilotSdkSession> CreateSessionAsync(CopilotSdkSessionConfiguration configuration, CancellationToken cancellationToken)
            {
                if (owner.CreateException is not null)
                    throw owner.CreateException;
                owner.LastConfiguration = configuration;
                var session = new FakeSession(Guid.NewGuid().ToString("N"), owner.SendDelay, owner.SendException);
                owner._sessions[session.SessionId] = session;
                owner.LastSession = session;
                return Task.FromResult<ICopilotSdkSession>(session);
            }

            public Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, CopilotSdkSessionConfiguration configuration, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.ResumeCount);
                owner.LastConfiguration = configuration;
                var session = new FakeSession(sessionId, owner.SendDelay, owner.SendException);
                owner._sessions[sessionId] = session;
                owner.LastSession = session;
                return Task.FromResult<ICopilotSdkSession>(session);
            }

            public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
            {
                owner._sessions.TryRemove(sessionId, out _);
                Interlocked.Increment(ref owner.DeleteCount);
                if (owner.DeleteException is not null)
                    throw owner.DeleteException;
                return Task.CompletedTask;
            }

            public Task<string?> GetForegroundSessionIdAsync(CancellationToken cancellationToken) => Task.FromResult(owner.ForegroundSessionId);
            public Task SetForegroundSessionIdAsync(string sessionId, CancellationToken cancellationToken)
            {
                owner.ForegroundSessionId = sessionId;
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession(string sessionId, TimeSpan delay, Exception? sendException) : ICopilotSdkSession
    {
        private int _concurrent;
        public string SessionId { get; } = sessionId;
        public int MaxConcurrentSends { get; private set; }

        public async Task<CopilotSendResult> SendAsync(string handle, CopilotSendRequest request, CancellationToken cancellationToken)
        {
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaxConcurrentSends = Math.Max(MaxConcurrentSends, concurrent);
            try
            {
                await Task.Delay(delay, cancellationToken);
                if (sendException is not null)
                    throw sendException;
                return new CopilotSendResult(handle, SessionId, $"reply:{request.Prompt}", "test-model", []);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public Task<IReadOnlyList<CopilotHistoryEvent>> GetHistoryAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CopilotHistoryEvent>>([]);
        public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetModelAsync(string model, string? reasoningEffort, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> GetModeAsync(CancellationToken cancellationToken) => Task.FromResult("interactive");
        public Task SetModeAsync(string mode, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotPlanResult> ReadPlanAsync(CancellationToken cancellationToken) => Task.FromResult(new CopilotPlanResult(false, null));
        public Task UpdatePlanAsync(string content, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeletePlanAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["review.md"]);
        public Task<CopilotWorkspaceFileResult> ReadWorkspaceFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult(new CopilotWorkspaceFileResult(path, "content", true));
        public Task CreateWorkspaceFileAsync(string path, string content, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
