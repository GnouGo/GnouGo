using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class KeyVaultCopilotPermissionGrantStoreTests
{
    [Fact]
    public async Task FutureAgentGrant_SurvivesStoreRestartAndFollowsAgentRename()
    {
        var records = new FakeRecordStore();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var first = CreateStore(records, clock);
        var created = await first.GrantFutureAgentRunsAsync(
            Context("tenant-a", "execution-a", "agent-a", "Old name"),
            TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromMinutes(5));
        var restarted = CreateStore(records, clock);
        var loaded = await restarted.FindReusableGrantAsync(
            Context("tenant-a", "execution-b", "agent-a", "New name"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("New name", loaded.AgentName);
        Assert.Equal(clock.GetUtcNow(), loaded.LastUsedAt);
        Assert.Equal(created.CreatedAt, loaded.CreatedAt);
        Assert.False(loaded.AllowSandboxBypass);
        Assert.Single(records.Records);
    }

    [Fact]
    public async Task FutureAgentSandboxBypassGrant_SurvivesRestartAndIsNeverDowngraded()
    {
        var records = new FakeRecordStore();
        var first = CreateStore(records);
        var created = await first.GrantFutureAgentRunsWithSandboxBypassAsync(
            Context("tenant-a", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken);

        Assert.True(created.AllowSandboxBypass);

        var restarted = CreateStore(records);
        var ordinaryUpdate = await restarted.GrantFutureAgentRunsAsync(
            Context("tenant-a", "execution-b", "agent-a", "Renamed"),
            TestContext.Current.CancellationToken);
        var loaded = await restarted.FindReusableGrantAsync(
            Context("tenant-a", "execution-c", "agent-a", "Renamed"),
            TestContext.Current.CancellationToken);

        Assert.True(ordinaryUpdate.AllowSandboxBypass);
        Assert.True(loaded?.AllowSandboxBypass);
        Assert.Equal(created.Id, loaded?.Id);
    }

    [Fact]
    public async Task LegacyFutureAgentGrant_WithoutSandboxFieldRemainsNonBypass()
    {
        var records = new FakeRecordStore();
        var store = CreateStore(records);
        var context = Context("tenant-a", "execution-a", "agent-a", "Reviewer");
        var recordKey = KeyVaultCopilotPermissionGrantStore.BuildRecordKey("agent-a");
        var now = DateTimeOffset.UtcNow;
        var serialized = JsonSerializer.Serialize(
            new CopilotPermissionGrant(
                "legacy-grant",
                "tenant-a",
                CopilotPermissionGrantScope.FutureAgentRuns,
                ExecutionId: null,
                AgentId: "agent-a",
                AgentName: "Reviewer",
                now,
                now),
            CopilotCoreJsonContext.Default.CopilotPermissionGrant);
        var legacyPayload = JsonNode.Parse(serialized)!.AsObject();
        Assert.True(legacyPayload.Remove("allowSandboxBypass"));
        records.SetRaw("tenant-a", recordKey, legacyPayload.ToJsonString());

        Assert.Null(await store.FindReusableSandboxBypassGrantAsync(
            context,
            TestContext.Current.CancellationToken));
        var ordinary = await store.FindReusableGrantAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.NotNull(ordinary);
        Assert.False(ordinary.AllowSandboxBypass);
    }

    [Fact]
    public async Task FutureAgentGrant_IsIsolatedByTenantAndAgent()
    {
        var records = new FakeRecordStore();
        var store = CreateStore(records);
        await store.GrantFutureAgentRunsAsync(
            Context("tenant-a", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken);

        Assert.Null(await store.FindReusableGrantAsync(
            Context("tenant-b", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken));
        Assert.Null(await store.FindReusableGrantAsync(
            Context("tenant-a", "execution-a", "agent-b", "Reviewer"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FutureAgentGrants_CanBeListedAndRevokedByGrantOrAgent()
    {
        var records = new FakeRecordStore();
        var store = CreateStore(records);
        var zulu = await store.GrantFutureAgentRunsAsync(
            Context("tenant-a", "execution-a", "agent-z", "Zulu"),
            TestContext.Current.CancellationToken);
        await store.GrantFutureAgentRunsAsync(
            Context("tenant-a", "execution-b", "agent-a", "Alpha"),
            TestContext.Current.CancellationToken);

        var grants = await store.ListFutureAgentGrantsAsync(
            "tenant-a",
            TestContext.Current.CancellationToken);
        Assert.Equal(["Alpha", "Zulu"], grants.Select(grant => grant.AgentName));

        Assert.True(await store.RevokeAsync("tenant-a", zulu.Id, TestContext.Current.CancellationToken));
        Assert.False(await store.RevokeAsync("tenant-a", zulu.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, await store.RevokeAgentAsync("tenant-a", "agent-a", TestContext.Current.CancellationToken));
        Assert.Equal(0, await store.RevokeAgentAsync("tenant-a", "agent-a", TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListFutureAgentGrantsAsync("tenant-a", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentFutureAgentGrants_ProduceOneTenantAgentRecord()
    {
        var records = new FakeRecordStore();
        var stores = Enumerable.Range(0, 8).Select(_ => CreateStore(records)).ToArray();

        await Task.WhenAll(stores.Select((store, index) => store.GrantFutureAgentRunsAsync(
            Context("tenant-a", $"execution-{index}", "agent-a", $"Reviewer {index}"),
            TestContext.Current.CancellationToken)));

        var grants = await stores[0].ListFutureAgentGrantsAsync(
            "tenant-a",
            TestContext.Current.CancellationToken);
        Assert.Single(grants);
        Assert.Equal("agent-a", grants[0].AgentId);
        Assert.Single(records.Records);
    }

    [Fact]
    public async Task CorruptedOrIdentityMismatchedPayloads_AreRejected()
    {
        var records = new FakeRecordStore();
        var store = CreateStore(records);
        var recordKey = KeyVaultCopilotPermissionGrantStore.BuildRecordKey("agent-a");
        records.SetRaw("tenant-a", recordKey, "not-json");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.FindReusableGrantAsync(
            Context("tenant-a", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken));

        var mismatched = new CopilotPermissionGrant(
            "grant-id",
            "tenant-a",
            CopilotPermissionGrantScope.FutureAgentRuns,
            ExecutionId: null,
            AgentId: "agent-b",
            AgentName: "Reviewer",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        records.SetRaw(
            "tenant-a",
            recordKey,
            JsonSerializer.Serialize(mismatched, CopilotCoreJsonContext.Default.CopilotPermissionGrant));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.FindReusableGrantAsync(
            Context("tenant-a", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnavailableKeyVault_DoesNotReturnAnAutomaticGrant()
    {
        var records = new FakeRecordStore
        {
            ReadFailure = new KeyVaultAccessException(
                "KeyVault is unavailable.",
                new IOException("Storage unavailable."))
        };
        var store = CreateStore(records);

        await Assert.ThrowsAsync<KeyVaultAccessException>(() => store.FindReusableGrantAsync(
            Context("tenant-a", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkflowGrant_IsMemoryOnlyAndExpiresAfterInactivity()
    {
        var records = new FakeRecordStore();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = CreateStore(records, clock, workflowGrantTtlSeconds: 60);
        await store.GrantWorkflowRunAsync(
            Context("tenant-a", "execution-a", "agent-a", "Reviewer"),
            TestContext.Current.CancellationToken);

        var sameExecution = await store.FindReusableGrantAsync(
            Context("tenant-a", "execution-a", "agent-b", "Other"),
            TestContext.Current.CancellationToken);
        Assert.Equal(CopilotPermissionGrantScope.WorkflowRun, sameExecution?.Scope);
        Assert.Empty(records.Records);

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(await store.FindReusableGrantAsync(
            Context("tenant-a", "execution-a", agentId: null, agentName: null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WorkflowSandboxBypassGrant_IsReusableAndNeverDowngraded()
    {
        var store = CreateStore(new FakeRecordStore());
        var context = Context("tenant-a", "execution-a", "agent-a", "Reviewer");

        var created = await store.GrantWorkflowRunWithSandboxBypassAsync(
            context,
            TestContext.Current.CancellationToken);
        var ordinaryUpdate = await store.GrantWorkflowRunAsync(
            context,
            TestContext.Current.CancellationToken);
        var loaded = await store.FindReusableGrantAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.True(created.AllowSandboxBypass);
        Assert.True(ordinaryUpdate.AllowSandboxBypass);
        Assert.True(loaded?.AllowSandboxBypass);
    }

    [Fact]
    public async Task IneligibleWorkflowGrant_BypassLookupDoesNotRefreshExpiry()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = CreateStore(new FakeRecordStore(), clock, workflowGrantTtlSeconds: 60);
        var context = Context("tenant-a", "execution-a", "agent-a", "Reviewer");
        await store.GrantWorkflowRunAsync(context, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(await store.FindReusableSandboxBypassGrantAsync(
            context,
            TestContext.Current.CancellationToken));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await store.FindReusableGrantAsync(
            context,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IneligibleWorkflowGrant_BypassLookupFallsBackToPersistentAgentGrant()
    {
        var records = new FakeRecordStore();
        var store = CreateStore(records);
        var context = Context("tenant-a", "execution-a", "agent-a", "Reviewer");
        await store.GrantFutureAgentRunsWithSandboxBypassAsync(
            context,
            TestContext.Current.CancellationToken);
        await store.GrantWorkflowRunAsync(
            context,
            TestContext.Current.CancellationToken);

        var loaded = await store.FindReusableSandboxBypassGrantAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(CopilotPermissionGrantScope.FutureAgentRuns, loaded.Scope);
        Assert.True(loaded.AllowSandboxBypass);
    }

    private static KeyVaultCopilotPermissionGrantStore CreateStore(
        IKeyVaultRecordStore records,
        TimeProvider? timeProvider = null,
        int workflowGrantTtlSeconds = 3600)
        => new(
            records,
            Options.Create(new CodeServerSettings
            {
                Copilot = new CodeCopilotSettings
                {
                    WorkflowGrantTtlSeconds = workflowGrantTtlSeconds
                }
            }),
            timeProvider ?? TimeProvider.System);

    private static CopilotRequestContext Context(
        string tenant,
        string execution,
        string? agentId,
        string? agentName)
        => new(tenant, ExecutionId: execution, AgentId: agentId, AgentName: agentName);

    private sealed class FakeRecordStore : IKeyVaultRecordStore
    {
        private readonly ConcurrentDictionary<RecordCoordinate, KeyVaultRecordValue> _records = new();

        public IReadOnlyCollection<KeyVaultRecordValue> Records => _records.Values.ToArray();
        public Exception? ReadFailure { get; init; }

        public Task<KeyVaultRecordValue?> GetAsync(
            string collection,
            string tenantId,
            string key,
            string author,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadFailure is not null)
                throw ReadFailure;
            _records.TryGetValue(new RecordCoordinate(collection, tenantId, key), out var value);
            return Task.FromResult(value);
        }

        public Task<KeyVaultRecordValue> UpsertAsync(
            string collection,
            string tenantId,
            string key,
            string value,
            string author,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var coordinate = new RecordCoordinate(collection, tenantId, key);
            var now = DateTimeOffset.UtcNow;
            var saved = _records.AddOrUpdate(
                coordinate,
                _ => new KeyVaultRecordValue(collection, tenantId, key, value, now, now),
                (_, existing) => existing with { Value = value, UpdatedAt = now });
            return Task.FromResult(saved);
        }

        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(
            string collection,
            string tenantId,
            string author,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<KeyVaultRecordValue> values = _records.Values
                .Where(record => string.Equals(record.Collection, collection, StringComparison.Ordinal)
                                 && string.Equals(record.TenantId, tenantId, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult(values);
        }

        public Task<bool> DeleteAsync(
            string collection,
            string tenantId,
            string key,
            string author,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_records.TryRemove(new RecordCoordinate(collection, tenantId, key), out _));
        }

        public void SetRaw(string tenantId, string key, string value)
        {
            var now = DateTimeOffset.UtcNow;
            _records[new RecordCoordinate(KeyVaultCopilotPermissionGrantStore.CollectionName, tenantId, key)] =
                new KeyVaultRecordValue(
                    KeyVaultCopilotPermissionGrantStore.CollectionName,
                    tenantId,
                    key,
                    value,
                    now,
                    now);
        }

        private sealed record RecordCoordinate(string Collection, string TenantId, string Key);
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
