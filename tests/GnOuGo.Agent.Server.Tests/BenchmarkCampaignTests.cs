using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;
public sealed class BenchmarkCampaignTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task OfflineReplayKeepsOriginalSchemaAndNeverWritesCampaignRecords()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recorded");
        var request = new LLMRequest { ClientRequestId = "session:1:hash", Prompt = "original prompt", StructuredOutputSchema = new JsonObject { ["type"] = "string" } };
        var state = new PlanningSession { Request = new() { TenantId = "benchmark", SessionId = "session", Name = "local", Prompt = "original prompt" }, ModelCalls = 3, RepairAttempts = 2 };
        await campaign.SaveAsync("planning-evaluation-runs", "source:pilot:local:1", new() { ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", request.ClientRequestId, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        await campaign.SaveAsync("planning-evaluation-receipts", request.ClientRequestId, JsonSerializer.SerializeToNode(new LLMResponse { Text = "\"recorded\"" }, PlanningJsonContext.Default.LLMResponse)!.AsObject(), Ct);
        var before = records.Writes;
        var snapshot = await campaign.InspectAsync(Ct);
        var replay = await campaign.ReadReplayAsync("source:pilot:local:1", Ct);
        var reserved = replay.State.PendingCall!.Request;
        Assert.Equal("string", reserved.StructuredOutputSchema!["type"]!.ToString());
        reserved.StructuredOutputSchema["type"] = "object";
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.Client.CallAsync(reserved, Ct));
        reserved.StructuredOutputSchema["type"] = "string";
        Assert.Equal("\"recorded\"", (await replay.Client.CallAsync(reserved, Ct)).Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => replay.Client.CallAsync(reserved, Ct));
        Assert.Equal(before, records.Writes);
        Assert.True(JsonNode.DeepEquals(snapshot, await campaign.InspectAsync(Ct)));
        var unchanged = (await campaign.LoadAsync("planning-evaluation-runs", "source:pilot:local:1", Ct))!["session"]!;
        Assert.Equal(3, unchanged["modelCalls"]!.GetValue<int>()); Assert.Equal(2, unchanged["repairAttempts"]!.GetValue<int>());
        Assert.Equal(8, unchanged["request"]!["maxModelCalls"]!.GetValue<int>());
    }
    [Fact]
    public async Task OfflineReplayWithoutReceiptFailsWithoutReservingOrWriting()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recorded");
        var state = new PlanningSession { Request = new() { SessionId = "session", Name = "local" } };
        await campaign.SaveAsync("planning-evaluation-runs", "source:pilot:local:1", new() { ["session"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession) }, Ct);
        await campaign.SaveAsync("planning-evaluation-requests", "session:1:hash", JsonSerializer.SerializeToNode(new LLMRequest { ClientRequestId = "session:1:hash" }, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var before = records.Writes;
        Assert.Contains("No completion receipt", (await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.ReadReplayAsync("source:pilot:local:1", Ct))).Message);
        Assert.Equal(before, records.Writes); Assert.True(await campaign.HasUncertainRequestAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BenchmarkCampaign(records, "different").ReadReplayAsync("source:pilot:local:1", Ct));
    }
    [Fact]
    public async Task UncertainFailureRetainsSafeMetadataWithoutInventingAReceipt()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recorded");
        await Assert.ThrowsAsync<LLMClientException>(() => campaign.CallAsync(new() { ClientRequestId = "session:1:hash", Prompt = "private prompt" }, _ => Task.CompletedTask,
            _ => throw new LLMClientException(LLMClientFailureKind.ServiceUnavailable, "raw body containing private data", true, 503, "overloaded", 1, false, 1000), Ct));
        var failure = (await campaign.LoadAsync("planning-evaluation-failures", "session:1:hash", Ct))!;
        Assert.Equal("ServiceUnavailable", failure["kind"]!.ToString()); Assert.Equal(503, failure["status_code"]!.GetValue<int>());
        Assert.Equal("dispatch", failure["stage"]!.ToString()); Assert.Equal(1, failure["attempt_count"]!.GetValue<int>());
        Assert.DoesNotContain("private", failure.ToJsonString()); Assert.DoesNotContain("raw body", failure.ToJsonString());
        Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", "session:1:hash", Ct));
        var writes = records.Writes; var inspection = await campaign.InspectAsync(Ct);
        Assert.Equal(1, inspection["reserved_requests"]!.GetValue<int>()); Assert.Equal(0, inspection["completed_receipts"]!.GetValue<int>());
        Assert.False(inspection["completion_receipts_complete"]!.GetValue<bool>()); Assert.Null(inspection["known_budget_cost"]);
        Assert.Equal("session:1:hash", Assert.Single(inspection["uncertain_requests"]!.AsArray())!.ToString());
        Assert.Equal(writes, records.Writes);
    }
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
        internal int Writes;
        private readonly Dictionary<string, KeyVaultRecordValue> _rows = [];
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_rows.GetValueOrDefault(c + t + k)); }
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Writes++; var row = new KeyVaultRecordValue(c, t, k, v, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); _rows[c + t + k] = row; return Task.FromResult(row); }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_rows.Values.Where(r => r.Collection == c && r.TenantId == t).ToArray());
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
