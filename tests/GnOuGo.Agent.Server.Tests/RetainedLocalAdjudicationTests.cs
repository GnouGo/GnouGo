using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class RetainedLocalAdjudicationTests
{
    [Theory]
    [InlineData("schema5-governing-applicability-diagnostics-1:local", true)]
    [InlineData("schema5-governing-applicability-diagnostics-1:mixed", false)]
    [InlineData("schema5-governing-applicability-diagnostics-2:local", false)]
    [InlineData("local", false)]
    [InlineData("stage1", false)]
    public void OnlyTheExactRetainedLocalMayBeAdjudicated(string id, bool allowed)
    {
        if (allowed) RetainedDiagnosticRecords.RequireCase(id);
        else Assert.Throws<InvalidOperationException>(() => RetainedDiagnosticRecords.RequireCase(id));
    }

    [Fact]
    public async Task AdjudicationStoreAllowsReadsButNeverForwardsWrites()
    {
        var inner = new RecordingStore(); var records = new RetainedDiagnosticRecords(inner);
        Assert.NotNull(await records.GetAsync("collection", "tenant", "key", "author", TestContext.Current.CancellationToken));
        Assert.Single(await records.ListAsync("collection", "tenant", "author", TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => { _ = records.UpsertAsync("collection", "tenant", "key", "private", "author", TestContext.Current.CancellationToken); });
        Assert.Throws<InvalidOperationException>(() => { _ = records.DeleteAsync("collection", "tenant", "key", "author", TestContext.Current.CancellationToken); });
        Assert.Equal(2, records.WritesAttempted); Assert.Equal(0, inner.Writes); Assert.Equal(2, inner.Reads);
    }

    private sealed class RecordingStore : IKeyVaultRecordStore
    {
        internal int Writes, Reads;
        private static KeyVaultRecordValue Value => new("collection", "tenant", "key", "private", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
        { Reads++; return Task.FromResult<KeyVaultRecordValue?>(Value); }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default)
        { Reads++; return Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>([Value]); }
        public Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default)
        { Writes++; return Task.FromResult(Value); }
        public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
        { Writes++; return Task.FromResult(true); }
    }
}
