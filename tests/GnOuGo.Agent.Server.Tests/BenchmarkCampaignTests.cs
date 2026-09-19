using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;
public sealed class BenchmarkCampaignTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task NewCampaignIgnoresOldUncertaintyAndReplaysOwnReceiptsWithoutDispatch()
    {
        var records = new Records(); var old = new BenchmarkCampaign(records, "old"); var fresh = new BenchmarkCampaign(records, "fresh");
        await old.SaveAsync("planning-evaluation-requests", "pending", new() { ["private"] = "unchanged" }, Ct);
        var request = new LLMRequest { ClientRequestId = "session:1", StructuredOutputSchema = new JsonObject { ["type"] = "object" } }; var calls = 0;
        Task<LLMResponse> Dispatch(CancellationToken _) { calls++; return Task.FromResult(new LLMResponse { Text = "receipt" }); }
        Assert.Equal("receipt", (await fresh.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct)).Text);
        var reopened = new BenchmarkCampaign(records, "fresh");
        Assert.Equal("receipt", (await reopened.CallAsync(request, _ => throw new InvalidOperationException("Preflight must not rerun"), Dispatch, Ct)).Text);
        Assert.Equal(1, calls); Assert.True(await old.HasUncertainRequestAsync(Ct)); Assert.False(await fresh.HasUncertainRequestAsync(Ct));
        Assert.Equal("unchanged", (await old.LoadAsync("planning-evaluation-requests", "pending", Ct))!["private"]!.ToString());
    }
    [Fact]
    public async Task UncertainDispatchBlocksFurtherRequestsWithoutResend()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh"); var calls = 0;
        Task<LLMResponse> Dispatch(CancellationToken _) { calls++; throw new IOException("Uncertain transport"); }
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(new() { ClientRequestId = "one" }, _ => Task.CompletedTask, Dispatch, Ct));
        var reopened = new BenchmarkCampaign(records, "fresh");
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.CallAsync(new() { ClientRequestId = "two" }, _ => Task.CompletedTask, Dispatch, Ct));
        Assert.Equal(1, calls); Assert.Equal("uncertain_dispatch", reopened.StopReason);
    }
    [Fact]
    public async Task CompletedReceiptSurvivesCancellationAndPinnedConfigurationCannotDrift()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh");
        await campaign.PinAsync(new() { ["model"] = "one" }, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.PinAsync(new() { ["model"] = "two" }, Ct));
        using var cancelled = new CancellationTokenSource();
        await campaign.CallAsync(new() { ClientRequestId = "one" }, _ => Task.CompletedTask, _ => { cancelled.Cancel(); return Task.FromResult(new LLMResponse { Text = "durable" }); }, cancelled.Token);
        Assert.NotNull(await campaign.LoadAsync("planning-evaluation-receipts", "one", Ct));
    }
    [Fact]
    public async Task CheckpointsRetainReservedSchemaAndBudgetRefusalDoesNotReserveDispatch()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "fresh");
        var evidence = new JsonObject { ["session"] = new JsonObject { ["modelCalls"] = 1, ["pendingCall"] = new JsonObject { ["responseSchema"] = new JsonObject { ["type"] = "string" } } } };
        await campaign.SaveAsync("planning-evaluation-runs", "pilot:local:1", evidence, Ct);
        Assert.True(JsonNode.DeepEquals(evidence, await new BenchmarkCampaign(records, "fresh").LoadAsync("planning-evaluation-runs", "pilot:local:1", Ct)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(new() { ClientRequestId = "one" }, _ => throw new InvalidOperationException("budget"), _ => throw new InvalidOperationException("must not dispatch"), Ct));
        Assert.False(await campaign.HasUncertainRequestAsync(Ct));
    }
    private sealed class Records : IKeyVaultRecordStore
    {
        private readonly Dictionary<string, KeyVaultRecordValue> _rows = [];
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_rows.GetValueOrDefault(c + t + k)); }
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); var row = new KeyVaultRecordValue(c, t, k, v, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); _rows[c + t + k] = row; return Task.FromResult(row); }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_rows.Values.Where(r => r.Collection == c && r.TenantId == t).ToArray());
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
