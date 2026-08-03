using System.Collections.Concurrent;

namespace GnOuGo.GithubCopilot.Core;

public sealed class CopilotSessionManager : IAsyncDisposable
{
    private readonly ICopilotSdkClientFactory _clientFactory;
    private readonly ICopilotProviderResolver? _providerResolver;
    private readonly ICopilotHumanInputProvider? _humanInputProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ManagedEntry> _entries = new(StringComparer.Ordinal);
    private int _disposed;

    public CopilotSessionManager(
        ICopilotSdkClientFactory clientFactory,
        ICopilotProviderResolver? providerResolver = null,
        ICopilotHumanInputProvider? humanInputProvider = null,
        TimeProvider? timeProvider = null)
    {
        _clientFactory = clientFactory;
        _providerResolver = providerResolver;
        _humanInputProvider = humanInputProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CopilotSessionDescriptor> CreateAsync(CopilotSessionCreateRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ValidateRequest(request);
        await SweepExpiredAsync(cancellationToken);

        var provider = await ResolveProviderAsync(request.Configuration, cancellationToken);
        var client = _clientFactory.Create(request.Configuration);
        try
        {
            await client.StartAsync(cancellationToken);
            var session = await client.CreateSessionAsync(
                new CopilotSdkSessionConfiguration(request, provider, _humanInputProvider),
                cancellationToken);
            var now = _timeProvider.GetUtcNow();
            var handle = CreateHandle();
            var entry = new ManagedEntry(handle, request, provider, client, session, now);
            if (!_entries.TryAdd(handle, entry))
                throw new InvalidOperationException("Could not allocate a unique Copilot session handle.");
            return entry.Describe(now);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<CopilotSessionDescriptor> ResumeAsync(CopilotSessionResumeRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await SweepExpiredAsync(cancellationToken);
        var entry = GetOwnedEntry(request.Handle, request.Context.TenantId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Session is null)
            {
                var client = _clientFactory.Create(entry.Request.Configuration);
                try
                {
                    await client.StartAsync(cancellationToken);
                    var session = await client.ResumeSessionAsync(
                        entry.CopilotSessionId,
                        new CopilotSdkSessionConfiguration(entry.Request, entry.Provider, _humanInputProvider),
                        cancellationToken);
                    entry.Client = client;
                    entry.Session = session;
                }
                catch
                {
                    await client.DisposeAsync();
                    throw;
                }
            }

            entry.Touch(_timeProvider.GetUtcNow());
            return entry.Describe(_timeProvider.GetUtcNow());
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public IReadOnlyList<CopilotSessionDescriptor> List(string tenantId)
    {
        ThrowIfDisposed();
        ValidateTenantId(tenantId);
        var now = _timeProvider.GetUtcNow();
        return _entries.Values
            .Where(entry => string.Equals(entry.TenantId, tenantId, StringComparison.Ordinal))
            .Where(entry => entry.ExpiresAt > now)
            .OrderByDescending(static entry => entry.LastAccessedAt)
            .Select(entry => entry.Describe(now))
            .ToArray();
    }

    public CopilotSessionConfigurationResult DescribeConfiguration(CopilotRequestContext context, string handle)
    {
        var entry = GetOwnedEntry(handle, context.TenantId);
        var configuration = entry.Request.Configuration;
        return new CopilotSessionConfigurationResult(
            handle,
            entry.Model,
            configuration.ReasoningEffort,
            entry.Request.PermissionMode,
            configuration.PermissionAllowlist ?? [],
            configuration.AvailableTools ?? [],
            configuration.ExcludedTools ?? [],
            configuration.SkillDirectories ?? [],
            configuration.DisabledSkills ?? [],
            configuration.McpServers?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [],
            configuration.EnableConfigDiscovery,
            StableAuditHooksEnabled: true,
            ElicitationEnabled: _humanInputProvider is not null);
    }

    public async Task<CopilotSendResult> SendAsync(CopilotSendRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("prompt must not be empty.", nameof(request));
        if (request.DeliveryMode is not ("enqueue" or "immediate"))
            throw new ArgumentException("deliveryMode must be 'enqueue' or 'immediate'.", nameof(request));

        await SweepExpiredAsync(cancellationToken);
        var entry = GetOwnedEntry(request.Handle, request.Context.TenantId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var session = entry.Session ?? throw new InvalidOperationException("The Copilot session is disconnected. Resume it before sending a message.");
            entry.Touch(_timeProvider.GetUtcNow());
            return await session.SendAsync(entry.Handle, request, cancellationToken);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<CopilotSendResult> OneShotAsync(
        CopilotSessionCreateRequest createRequest,
        string prompt,
        IReadOnlyList<CopilotAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var request = createRequest with { SessionKind = CopilotSessionKind.OneShot };
        var descriptor = await CreateAsync(request, cancellationToken);
        try
        {
            return await SendAsync(new CopilotSendRequest(request.Context, descriptor.Handle, prompt, Attachments: attachments), cancellationToken);
        }
        finally
        {
            await DeleteAsync(request.Context, descriptor.Handle, CancellationToken.None);
        }
    }

    public async Task<IReadOnlyList<CopilotHistoryEvent>> GetHistoryAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
        => await WithConnectedSessionAsync(context, handle, static (session, ct) => session.GetHistoryAsync(ct), cancellationToken);

    public async Task<CopilotOperationResult> AbortAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
    {
        await WithConnectedSessionAsync(context, handle, static async (session, ct) => { await session.AbortAsync(ct); return true; }, cancellationToken);
        return new CopilotOperationResult(true, handle, Message: "The active Copilot turn was aborted.");
    }

    public async Task<CopilotOperationResult> SetModelAsync(CopilotRequestContext context, string handle, string model, string? reasoningEffort, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("model must not be empty.", nameof(model));
        await WithConnectedSessionAsync(context, handle, async (session, ct) => { await session.SetModelAsync(model, reasoningEffort, ct); return true; }, cancellationToken);
        var entry = GetOwnedEntry(handle, context.TenantId);
        entry.Model = model.Trim();
        return new CopilotOperationResult(true, handle, entry.CopilotSessionId, $"Model changed to {entry.Model}.");
    }

    public async Task<string> GetModeAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
        => await WithConnectedSessionAsync(context, handle, static (session, ct) => session.GetModeAsync(ct), cancellationToken);

    public async Task<CopilotOperationResult> SetModeAsync(CopilotRequestContext context, string handle, string mode, CancellationToken cancellationToken)
    {
        var normalized = NormalizeMode(mode);
        await WithConnectedSessionAsync(context, handle, async (session, ct) => { await session.SetModeAsync(normalized, ct); return true; }, cancellationToken);
        return new CopilotOperationResult(true, handle, Message: $"Mode changed to {normalized}.");
    }

    public async Task<CopilotPlanResult> ReadPlanAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
        => await WithConnectedSessionAsync(context, handle, static (session, ct) => session.ReadPlanAsync(ct), cancellationToken);

    public async Task<CopilotOperationResult> UpdatePlanAsync(CopilotRequestContext context, string handle, string content, CancellationToken cancellationToken)
    {
        await WithConnectedSessionAsync(context, handle, async (session, ct) => { await session.UpdatePlanAsync(content, ct); return true; }, cancellationToken);
        return new CopilotOperationResult(true, handle, Message: "Plan updated.");
    }

    public async Task<CopilotOperationResult> DeletePlanAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
    {
        await WithConnectedSessionAsync(context, handle, static async (session, ct) => { await session.DeletePlanAsync(ct); return true; }, cancellationToken);
        return new CopilotOperationResult(true, handle, Message: "Plan deleted.");
    }

    public Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
        => WithConnectedSessionAsync(context, handle, static (session, ct) => session.ListWorkspaceFilesAsync(ct), cancellationToken);

    public Task<CopilotWorkspaceFileResult> ReadWorkspaceFileAsync(CopilotRequestContext context, string handle, string path, CancellationToken cancellationToken)
    {
        ValidateWorkspacePath(path);
        return WithConnectedSessionAsync(context, handle, (session, ct) => session.ReadWorkspaceFileAsync(path, ct), cancellationToken);
    }

    public async Task<CopilotOperationResult> CreateWorkspaceFileAsync(CopilotRequestContext context, string handle, string path, string content, CancellationToken cancellationToken)
    {
        ValidateWorkspacePath(path);
        await WithConnectedSessionAsync(context, handle, async (session, ct) =>
        {
            await session.CreateWorkspaceFileAsync(path, content, ct);
            return true;
        }, cancellationToken);
        return new CopilotOperationResult(true, handle, Message: $"Workspace file '{path}' created or replaced.");
    }

    public async Task<CopilotForegroundResult> GetForegroundAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
    {
        var entry = GetOwnedEntry(handle, context.TenantId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var client = entry.Client ?? throw new InvalidOperationException("The Copilot session is disconnected. Resume it before querying foreground state.");
            var sessionId = await client.GetForegroundSessionIdAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(sessionId))
                return new CopilotForegroundResult(false, null, null, "No Copilot session is in the foreground.");

            var owned = _entries.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.TenantId, context.TenantId, StringComparison.Ordinal)
                && string.Equals(candidate.CopilotSessionId, sessionId, StringComparison.Ordinal));
            return owned is null
                ? new CopilotForegroundResult(false, null, null, "The foreground session is not owned by this tenant.")
                : new CopilotForegroundResult(true, owned.Handle, owned.CopilotSessionId);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<CopilotOperationResult> SetForegroundAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
    {
        var entry = GetOwnedEntry(handle, context.TenantId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var client = entry.Client ?? throw new InvalidOperationException("The Copilot session is disconnected. Resume it before setting foreground state.");
            await client.SetForegroundSessionIdAsync(entry.CopilotSessionId, cancellationToken);
            entry.Touch(_timeProvider.GetUtcNow());
            return new CopilotOperationResult(true, handle, entry.CopilotSessionId, "Session moved to the foreground.");
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<CopilotOperationResult> DisconnectAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
    {
        var entry = GetOwnedEntry(handle, context.TenantId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Session is not null)
            {
                await entry.Session.DisposeAsync();
                entry.Session = null;
            }
            if (entry.Client is not null)
            {
                await entry.Client.DisposeAsync();
                entry.Client = null;
            }
            entry.Touch(_timeProvider.GetUtcNow());
            return new CopilotOperationResult(true, handle, entry.CopilotSessionId, "Session disconnected; persisted Copilot state can be resumed through this handle until TTL expiry.");
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public async Task<CopilotOperationResult> DeleteAsync(CopilotRequestContext context, string handle, CancellationToken cancellationToken)
    {
        var entry = GetOwnedEntry(handle, context.TenantId);
        if (!_entries.TryRemove(handle, out _))
            throw new InvalidOperationException("The Copilot session handle no longer exists.");
        await DeleteEntryAsync(entry, cancellationToken);
        return new CopilotOperationResult(true, handle, entry.CopilotSessionId, "Session state was permanently deleted.");
    }

    public async Task<CopilotConnectivityResult> PingAsync(CopilotRuntimeConfiguration configuration, CancellationToken cancellationToken)
        => await WithTemporaryClientAsync(configuration, static (client, ct) => client.PingAsync(ct), cancellationToken);

    public async Task<CopilotStatusResult> GetStatusAsync(CopilotRuntimeConfiguration configuration, CancellationToken cancellationToken)
        => await WithTemporaryClientAsync(configuration, static (client, ct) => client.GetStatusAsync(ct), cancellationToken);

    public async Task<CopilotAuthResult> GetAuthStatusAsync(CopilotRuntimeConfiguration configuration, CancellationToken cancellationToken)
        => await WithTemporaryClientAsync(configuration, static (client, ct) => client.GetAuthStatusAsync(ct), cancellationToken);

    public async Task<IReadOnlyList<CopilotModelResult>> ListModelsAsync(CopilotRuntimeConfiguration configuration, CancellationToken cancellationToken)
        => await WithTemporaryClientAsync(configuration, static (client, ct) => client.ListModelsAsync(ct), cancellationToken);

    public async Task<int> SweepExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var expired = _entries.Values.Where(entry => entry.ExpiresAt <= now).ToArray();
        var deleted = 0;
        foreach (var entry in expired)
        {
            if (!_entries.TryRemove(entry.Handle, out _))
                continue;
            await DeleteEntryAsync(entry, cancellationToken);
            deleted++;
        }
        return deleted;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        var entries = _entries.Values.ToArray();
        _entries.Clear();
        foreach (var entry in entries)
            await DisposeEntryAsync(entry);
    }

    private async Task<T> WithConnectedSessionAsync<T>(CopilotRequestContext context, string handle, Func<ICopilotSdkSession, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await SweepExpiredAsync(cancellationToken);
        var entry = GetOwnedEntry(handle, context.TenantId);
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var session = entry.Session ?? throw new InvalidOperationException("The Copilot session is disconnected. Resume it before using it.");
            entry.Touch(_timeProvider.GetUtcNow());
            return await action(session, cancellationToken);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task<T> WithTemporaryClientAsync<T>(CopilotRuntimeConfiguration configuration, Func<ICopilotSdkClient, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await using var client = _clientFactory.Create(configuration);
        await client.StartAsync(cancellationToken);
        return await action(client, cancellationToken);
    }

    private async Task<CopilotProviderResolution?> ResolveProviderAsync(CopilotRuntimeConfiguration configuration, CancellationToken cancellationToken)
        => _providerResolver is null
            ? null
            : await _providerResolver.ResolveAsync(configuration.ProviderName, configuration.Model, configuration.GitHubToken, cancellationToken);

    private ManagedEntry GetOwnedEntry(string handle, string tenantId)
    {
        ValidateTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(handle) || !_entries.TryGetValue(handle, out var entry))
            throw new KeyNotFoundException("The Copilot session handle was not found.");
        if (!string.Equals(entry.TenantId, tenantId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The Copilot session handle does not belong to this tenant.");
        return entry;
    }

    private static void ValidateRequest(CopilotSessionCreateRequest request)
    {
        ValidateTenantId(request.Context.TenantId);
        if (string.IsNullOrWhiteSpace(request.Configuration.WorkingDirectory) || !Path.IsPathFullyQualified(request.Configuration.WorkingDirectory))
            throw new ArgumentException("WorkingDirectory must be an absolute path resolved by the hosting policy.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Configuration.Model))
            throw new ArgumentException("A Copilot model is required.", nameof(request));
        if (request.PermissionMode == CopilotPermissionMode.ApproveAll && !request.Configuration.EnableApproveAll)
            throw new InvalidOperationException("approve_all is disabled by host policy.");
        if (request.PermissionMode == CopilotPermissionMode.Interactive && request.SessionKind == CopilotSessionKind.OneShot)
            throw new InvalidOperationException("Interactive permission mode requires a managed session so callbacks can complete across round trips.");
    }

    private static void ValidateTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("TenantId is required for Copilot sessions.", nameof(tenantId));
    }

    private static string NormalizeMode(string mode)
        => mode.Trim().ToLowerInvariant() switch
        {
            "interactive" => "interactive",
            "plan" => "plan",
            "autopilot" => "autopilot",
            _ => throw new ArgumentException("mode must be interactive, plan, or autopilot.", nameof(mode))
        };

    private static void ValidateWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
            throw new ArgumentException("Workspace file paths must be non-empty relative paths.", nameof(path));
        var normalized = path.Replace('\\', '/');
        if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static segment => segment == ".."))
            throw new UnauthorizedAccessException("Workspace file paths cannot leave the session workspace.");
    }

    private static string CreateHandle() => $"cps_{Guid.NewGuid():N}";

    private async Task DeleteEntryAsync(ManagedEntry entry, CancellationToken cancellationToken)
    {
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Session is not null)
            {
                await entry.Session.DisposeAsync();
                entry.Session = null;
            }
            var client = entry.Client;
            if (client is null)
            {
                client = _clientFactory.Create(entry.Request.Configuration);
                await client.StartAsync(cancellationToken);
                entry.Client = client;
            }
            await client.DeleteSessionAsync(entry.CopilotSessionId, cancellationToken);
            await client.DisposeAsync();
            entry.Client = null;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static async Task DisposeEntryAsync(ManagedEntry entry)
    {
        await entry.Gate.WaitAsync();
        try
        {
            if (entry.Session is not null)
                await entry.Session.DisposeAsync();
            if (entry.Client is not null)
                await entry.Client.DisposeAsync();
            entry.Session = null;
            entry.Client = null;
        }
        finally
        {
            entry.Gate.Release();
            entry.Gate.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed class ManagedEntry
    {
        public ManagedEntry(string handle, CopilotSessionCreateRequest request, CopilotProviderResolution? provider, ICopilotSdkClient client, ICopilotSdkSession session, DateTimeOffset now)
        {
            Handle = handle;
            Request = request;
            Provider = provider;
            Client = client;
            Session = session;
            CopilotSessionId = session.SessionId;
            TenantId = request.Context.TenantId;
            Model = provider?.Model ?? request.Configuration.Model;
            CreatedAt = now;
            LastAccessedAt = now;
            ExpiresAt = now.AddSeconds(Math.Max(1, request.Configuration.ManagedSessionTtlSeconds));
        }

        public string Handle { get; }
        public string CopilotSessionId { get; }
        public string TenantId { get; }
        public CopilotSessionCreateRequest Request { get; }
        public CopilotProviderResolution? Provider { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ICopilotSdkClient? Client { get; set; }
        public ICopilotSdkSession? Session { get; set; }
        public string Model { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset LastAccessedAt { get; private set; }
        public DateTimeOffset ExpiresAt { get; private set; }

        public void Touch(DateTimeOffset now)
        {
            LastAccessedAt = now;
            ExpiresAt = now.AddSeconds(Math.Max(1, Request.Configuration.ManagedSessionTtlSeconds));
        }

        public CopilotSessionDescriptor Describe(DateTimeOffset now)
            => new(Handle, CopilotSessionId, TenantId, Model, Request.SessionKind, Request.PermissionMode, CreatedAt, LastAccessedAt, ExpiresAt, Session is not null && ExpiresAt > now);
    }
}
