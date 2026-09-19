using System.Text.Json.Nodes;
using System.Net;
using GnOuGo.AI.Core;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;
public sealed class BenchmarkCampaignTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task HttpRecoveryPreservesUnknownAllowanceAndReplaysAfterReceiptWriteFailure()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "recovery");
        var request = new LLMRequest { ClientRequestId = "session:1:hash", Prompt = "immutable", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        var ids = new List<string>();
        using var http = new HttpClient(new Handler(message =>
        {
            ids.Add(message.Headers.GetValues("X-Client-Request-Id").Single());
            if (ids.Count == 1) throw new TaskCanceledException();
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}") };
        }));
        Task<LLMResponse> Dispatch(CancellationToken ct) => DispatchAsync(campaign, request.ClientRequestId!, http, ct);
        records.FailCollection = "planning-evaluation-receipts";
        await Assert.ThrowsAsync<IOException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct, allowHttpRecovery: true));
        Assert.Equal(2, ids.Distinct().Count());
        var before = await BenchmarkHttpJournal.AccountingAsync(campaign, request.ClientRequestId, ct: Ct);
        Assert.Equal(2, before["calls"]!.GetValue<long>()); Assert.Equal(.1m, before["known_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(1m, before["reserved_cost_eur"]!.GetValue<decimal>()); Assert.Equal(1.1m, before["cost_upper_bound_eur"]!.GetValue<decimal>());
        Assert.Equal(100L, before["reserved_input_tokens"]!.GetValue<long>()); Assert.Equal(20L, before["reserved_output_tokens"]!.GetValue<long>());
        Assert.NotNull(await campaign.LoadAsync("planning-evaluation-failures", request.ClientRequestId, Ct));
        records.FailCollection = null;
        campaign = new(records, "recovery");
        var response = await campaign.CallAsync(request, _ => Task.CompletedTask, Dispatch, Ct, allowHttpRecovery: true);
        Assert.Equal(1, response.Usage!["uncertain_attempts"]!.GetValue<int>());
        var replay = await campaign.CallAsync(request, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), Ct, allowHttpRecovery: true);
        Assert.True(JsonNode.DeepEquals(response.Usage, replay.Usage)); Assert.Equal(2, ids.Count);
        Assert.True(JsonNode.DeepEquals(before, await BenchmarkHttpJournal.AccountingAsync(campaign, request.ClientRequestId, ct: Ct)));
        Assert.False(await campaign.HasUncertainRequestAsync(Ct)); // The original uncertain attempt still exists in its HTTP journal.
        Assert.Equal("immutable", (await campaign.LoadAsync("planning-evaluation-requests", request.ClientRequestId, Ct))!["prompt"]!.ToString());
        // Recovery does not poison the rest of the campaign.
        await campaign.CallAsync(new() { ClientRequestId = "next:1:hash" }, _ => Task.CompletedTask, _ => Task.FromResult(new LLMResponse()), Ct);
        Assert.Null(campaign.StopReason);
    }
    [Fact]
    public async Task ConservativeAdmissionRejectsCostAndCallExhaustionWithoutChangingLedger()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "limits");
        var journal = new BenchmarkHttpJournal(campaign, "session:1:hash", 100, 20, 26m);
        var state = new LLMHttpRetryState { Fingerprint = "fixed", Attempts = [new() { Id = "first", Failure = "timeout" }] };
        await journal.SaveAsync(state, Ct);
        state.Attempts.Add(new() { Id = "second" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.SaveAsync(state, Ct));
        Assert.Single((await journal.LoadAsync(Ct))!.Attempts);
        Assert.Equal(26m, (await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct))["cost_upper_bound_eur"]!.GetValue<decimal>());
        var other = new BenchmarkCampaign(records, "calls"); var limited = new BenchmarkHttpJournal(other, "session:1:hash", 100, 20, .1m);
        state = new() { Fingerprint = "fixed" };
        for (var i = 0; i < 8; i++) { state.Attempts.Add(new() { Id = i.ToString() }); await limited.SaveAsync(state, Ct); state.Attempts[^1].Status = 503; await limited.SaveAsync(state, Ct); }
        state.Attempts.Add(new() { Id = "ninth" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => limited.SaveAsync(state, Ct));
        Assert.Equal(8, (await limited.LoadAsync(Ct))!.Attempts.Count);
    }
    [Fact]
    public async Task HttpExhaustionAndCancellationCannotDispatchOnRestart()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "exhaustion"); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; throw new HttpRequestException("unknown delivery"); }));
        var request = new LLMRequest { ClientRequestId = "session:1:hash" };
        for (var restart = 0; restart < 2; restart++)
        {
            campaign = new(records, "exhaustion");
            await Assert.ThrowsAsync<HttpRequestException>(() => campaign.CallAsync(request, _ => Task.CompletedTask,
                ct => DispatchAsync(campaign, request.ClientRequestId!, http, ct), Ct, allowHttpRecovery: true));
        }
        Assert.Equal(2, calls); Assert.Null(await campaign.LoadAsync("planning-evaluation-receipts", request.ClientRequestId, Ct));
        var accounting = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        Assert.Equal(2L, accounting["calls"]!.GetValue<long>()); Assert.Equal(2m, accounting["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Equal("uncertain_dispatch", campaign.StopReason);
    }
    [Fact]
    public async Task PreparedJournalResumesBeforeFirstSendAndOriginalSchemaCannotChange()
    {
        var records = new Records(); var campaign = new BenchmarkCampaign(records, "prepared");
        var request = new LLMRequest { ClientRequestId = "session:1:hash", StructuredOutputSchema = new JsonObject { ["type"] = "object" } };
        await new BenchmarkHttpJournal(campaign, request.ClientRequestId, 100, 20, 1m).PrepareAsync(Ct);
        await campaign.SaveAsync("planning-evaluation-requests", request.ClientRequestId, JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}") }; }));
        request.StructuredOutputSchema["type"] = "string";
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.CallAsync(request, _ => Task.CompletedTask, ct => DispatchAsync(campaign, request.ClientRequestId, http, ct), Ct, allowHttpRecovery: true));
        Assert.Equal(0, calls);
        request.StructuredOutputSchema["type"] = "object";
        await campaign.CallAsync(request, _ => Task.CompletedTask, ct => DispatchAsync(campaign, request.ClientRequestId, http, ct), Ct, allowHttpRecovery: true);
        Assert.Equal(1, calls);
    }
    private static async Task<LLMResponse> DispatchAsync(BenchmarkCampaign campaign, string id, HttpClient http, CancellationToken ct)
    {
        var journal = new BenchmarkHttpJournal(campaign, id, 100, 20, 1m);
        using var context = new LLMHttpRetryContext(id, journal).Activate();
        var response = await new OpenAiLLMProvider(http).CallAsync("test", new() { Url = "https://provider.example/v1", RetryPolicy = new() { BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1 } }, new() { Prompt = "fixed", MaxOutputTokens = 20 }, ct);
        var usage = await journal.CompleteAsync(new() { ["input_tokens"] = 10L, ["output_tokens"] = 2L, ["benchmark_cost_eur"] = .1m }, ct);
        return new() { Json = response.Json, Text = response.Text, Usage = usage };
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(action(request)); }

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
        internal string? FailCollection;
        private readonly Dictionary<string, KeyVaultRecordValue> _rows = [];
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(_rows.GetValueOrDefault(c + t + k)); }
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); if (c == FailCollection) throw new IOException("Injected store failure"); Writes++; var row = new KeyVaultRecordValue(c, t, k, v, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); _rows[c + t + k] = row; return Task.FromResult(row); }
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KeyVaultRecordValue>>(_rows.Values.Where(r => r.Collection == c && r.TenantId == t).ToArray());
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
