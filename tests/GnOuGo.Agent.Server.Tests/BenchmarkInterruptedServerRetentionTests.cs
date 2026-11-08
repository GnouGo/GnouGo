using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class BenchmarkPermanentRetentionTests
{
    private const string InterruptedServerRun = "interrupted-server-run";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnjournaledServerRetentionKeepsUncertaintyAndTheWholeHoldAcrossColdRestart(bool cancelled)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "GnOuGo.ServerRetention", Guid.NewGuid().ToString("N"));
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(Path.Combine(workspace, ".GnOuGo/data/live-server", InterruptedServerRun));
        var campaign = await SeedInterruptedServerAsync(fixture);
        var retentionKind = cancelled ? BenchmarkCampaign.CancelledPlanningRetention : Interrupted;
        if (cancelled) await MarkUnjournaledServerCancelledAsync(campaign, fixture);
        var budgetBefore = await fixture.Records.GetAsync("agent-planning-budgets-v11", "benchmark", Session, "test", Ct);
        var accountingBefore = await BenchmarkHttpJournal.AccountingAsync(campaign, ct: Ct);
        var proposal = await campaign.InspectPermanentRetentionAsync("planning-evaluation-runs", InterruptedServerRun, RequestId, Ct, retentionKind, workspace);
        var closure = await campaign.ApplyPermanentRetentionAsync("planning-evaluation-runs", InterruptedServerRun, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, retentionKind, workspace);
        Assert.Equal("unknown", closure["evidence"]!["dispatch_certainty"]!.ToString());
        Assert.Equal(.95m, closure["evidence"]!["retained_application_reservation_eur"]!.GetValue<decimal>());
        Assert.Equal("inconclusive", closure["outcome"]!.ToString());
        var cold = new BenchmarkCampaign(new KeyVaultRecordStore(Path.Combine(fixture.Root, "vault.db")), "retention");
        await cold.ApplyPermanentRetentionAsync("planning-evaluation-runs", InterruptedServerRun, RequestId,
            proposal["evidence_hash"]!.ToString(), Operator, Authorization, Ct, retentionKind, workspace);
        Assert.False(await cold.HasUncertainRequestAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cold.RequireRunNotRetainedAsync(Session, Ct));
        await cold.RequireRunNotRetainedAsync("independent-session", Ct);
        await cold.RequireRetainedApplicationHoldsAsync(.95m, Ct, requireComplete: true);
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => cold.RequireRetainedApplicationHoldsAsync(.949m, Ct));
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => cold.RequireRetainedApplicationHoldsAsync(1m, Ct, requireComplete: true));
        Assert.True(JsonNode.DeepEquals(accountingBefore, await BenchmarkHttpJournal.AccountingAsync(cold, ct: Ct)));
        Assert.Equal(budgetBefore, await fixture.Records.GetAsync("agent-planning-budgets-v11", "benchmark", Session, "test", Ct));
        Assert.Null(await cold.LoadAsync("planning-evaluation-receipts", RequestId, Ct));
        using var handler = new RetainedHoldRuntimeHandler();
        using var http = new HttpClient(handler);
        var options = new LLMOptions { DefaultProvider = "fixture", DefaultModel = "pinned",
            Models = { ["fixture"] = new() { Type = "openai", Url = "https://example.invalid/v1" } } };
        using (var model = new KeyVaultBenchmarkModel(cold, options, new RoutingLLMClientAdapter(new RoutingLLMClient(http, options)),
            new RetainedHoldRates(), new RetainedHoldPrice(), http: http) { AdvisoryFinancialForecasts = true, RetainedExternalReservationsEur = .95m })
        {
            var request = new LLMRequest { ClientRequestId = "fresh-execution:one", Prompt = "Exact approved input", MaxTokens = 8192 };
            await model.CallExecutionAsync(request, Ct);
            await model.CallExecutionAsync(request, Ct);
            Assert.Equal(1, handler.Calls); // Exact receipt recovery cannot dispatch or charge again.
            Assert.Equal(.09m, (await BenchmarkHttpJournal.AccountingAsync(cold, ct: Ct))["known_cost_eur"]!.GetValue<decimal>());
            model.RetainedExternalReservationsEur = 1m;
            await model.CallExecutionAsync(request, Ct); // Current funding cannot block an exact already-paid receipt.
            await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => model.CallExecutionAsync(
                new() { ClientRequestId = "fresh-execution:two", Prompt = "Different approved input", MaxTokens = 8192 }, Ct));
            Assert.Equal(1, handler.Calls); // Additional unverified external history remains blocked.
        }
        var admitted = new BenchmarkHttpJournal(cold, "independent-session:1", 100, 100, 1m,
            effectiveTransportPolicy: new() { ["retained_external_reservations_eur"] = .95m });
        await admitted.PrepareAsync(Ct);
        await admitted.SaveAsync(new() { Fingerprint = new string('b', 64), Attempts = [new() { Id = "independent-session:1:attempt:1" }] }, Ct);
        var after = await BenchmarkHttpJournal.AccountingAsync(cold, ct: Ct);
        Assert.Equal(1m, after["reserved_cost_eur"]!.GetValue<decimal>()); // The separate .95 hold remains external, counted once.
        var understated = new BenchmarkHttpJournal(cold, "independent-session:2", 100, 100, 1m);
        await understated.PrepareAsync(Ct);
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => understated.SaveAsync(
            new() { Fingerprint = new string('c', 64), Attempts = [new() { Id = "independent-session:2:attempt:1" }] }, Ct));
        Assert.Empty((await understated.LoadAsync(Ct))!.Attempts);
        await fixture.DisposeAsync();
        Directory.Delete(workspace, true);
    }

    [Theory]
    [InlineData("active_status")]
    [InlineData("missing_cancelled_result")]
    [InlineData("changed_budget")]
    public async Task CancelledUnjournaledServerRetentionRequiresBothCancelledStateAndExactHold(string defect)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "GnOuGo.ServerRetention", Guid.NewGuid().ToString("N"));
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(Path.Combine(workspace, ".GnOuGo/data/live-server", InterruptedServerRun));
        var campaign = await SeedInterruptedServerAsync(fixture);
        await MarkUnjournaledServerCancelledAsync(campaign, fixture);
        if (defect == "active_status")
        {
            var state = (await fixture.Store.LoadAsync("benchmark", Session, Ct))!;
            state.Status = PlanningStatus.Generating; state.Revision++;
            Assert.True(await fixture.Store.TrySaveAsync(state, state.Revision - 1, Ct));
        }
        else if (defect == "missing_cancelled_result")
        {
            var run = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedServerRun, Ct))!;
            run["result"]!["status"] = PlanningStatus.Generating;
            await campaign.SaveAsync("planning-evaluation-runs", InterruptedServerRun, run, Ct);
        }
        else await fixture.Records.UpsertAsync("agent-planning-budgets-v11", "benchmark", Session, "{}", "test", Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs",
            InterruptedServerRun, RequestId, Ct, BenchmarkCampaign.CancelledPlanningRetention, workspace));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-closures", RequestId, Ct));
        await fixture.DisposeAsync();
        Directory.Delete(workspace, true);
    }

    private static async Task MarkUnjournaledServerCancelledAsync(BenchmarkCampaign campaign, PlanningPersistenceTests.StoreFixture fixture)
    {
        var state = (await fixture.Store.LoadAsync("benchmark", Session, Ct))!;
        state.Status = PlanningStatus.Cancelled; state.Revision++;
        Assert.True(await fixture.Store.TrySaveAsync(state, state.Revision - 1, Ct));
        var run = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedServerRun, Ct))!;
        run["result"]!["status"] = PlanningStatus.Cancelled;
        await campaign.SaveAsync("planning-evaluation-runs", InterruptedServerRun, run, Ct);
    }

    private sealed class RetainedHoldRuntimeHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"exact result"}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""") });
        }
    }

    private sealed class RetainedHoldRates : IExchangeRateProvider
    {
        public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct)
            => ValueTask.FromResult<CurrencyExchangeQuote?>(new(sourceCurrency, targetCurrency, .9m, DateTimeOffset.UtcNow, "fixture-authority"));
    }
    private sealed class RetainedHoldPrice : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => .1m;
    }

    [Theory]
    [InlineData("active_owner")]
    [InlineData("receipt")]
    [InlineData("server_receipt")]
    [InlineData("transport")]
    [InlineData("usage")]
    [InlineData("failure")]
    [InlineData("hold")]
    [InlineData("configuration")]
    [InlineData("execution")]
    [InlineData("tools")]
    public async Task InterruptedServerRetentionRejectsMissingEligibilityOrReceivedEvidence(string defect)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "GnOuGo.ServerRetention", Guid.NewGuid().ToString("N"));
        await using var fixture = await PlanningPersistenceTests.StoreFixture.CreateAsync(Path.Combine(workspace, ".GnOuGo/data/live-server", InterruptedServerRun));
        var campaign = await SeedInterruptedServerAsync(fixture);
        await using var owner = defect == "active_owner" ? await fixture.Store.TryAcquireLeaseAsync("benchmark", Session, Ct) : null;
        switch (defect)
        {
            case "receipt": await campaign.SaveAsync("planning-evaluation-receipts", RequestId, new(), Ct); break;
            case "server_receipt": await fixture.Records.UpsertAsync("agent-planning-model-receipts-v11", "benchmark", Session + ":" + RequestId, "{}", "test", Ct); break;
            case "transport":
                var http = await fixture.Records.GetAsync("agent-planning-model-http-v11", "benchmark", RequestId, "test", Ct);
                var changed = JsonNode.Parse(http!.Value)!; changed["transport"] = new JsonObject();
                await fixture.Records.UpsertAsync("agent-planning-model-http-v11", "benchmark", RequestId, changed.ToJsonString(), "test", Ct); break;
            case "usage":
                var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
                journal["usage"] = new JsonObject(); await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct); break;
            case "failure": await campaign.SaveAsync("planning-evaluation-failures", RequestId, new(), Ct); break;
            case "hold": await fixture.Records.UpsertAsync("agent-planning-budgets-v11", "benchmark", Session, "{}", "test", Ct); break;
            case "configuration":
            case "execution":
                var run = (await campaign.LoadAsync("planning-evaluation-runs", InterruptedServerRun, Ct))!;
                if (defect == "execution") run["execution_started"] = true; else run["campaign_configuration"] = new string('d', 64);
                await campaign.SaveAsync("planning-evaluation-runs", InterruptedServerRun, run, Ct); break;
            case "tools":
                var state = (await fixture.Store.LoadAsync("benchmark", Session, Ct))!;
                state.PendingCall!.Request.Tools = [new() { Name = "external" }]; state.Revision++;
                Assert.True(await fixture.Store.TrySaveAsync(state, state.Revision - 1, Ct)); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.InspectPermanentRetentionAsync("planning-evaluation-runs",
            InterruptedServerRun, RequestId, Ct, Interrupted, workspace));
        Assert.Null(await campaign.LoadAsync("planning-evaluation-closures", RequestId, Ct));
        if (owner is not null) await owner.DisposeAsync();
        await fixture.DisposeAsync();
        Directory.Delete(workspace, true);
    }

    private static async Task<BenchmarkCampaign> SeedInterruptedServerAsync(PlanningPersistenceTests.StoreFixture fixture)
    {
        var campaign = await SeedCancelledServerAsync(fixture.Records);
        var run = (await campaign.LoadAsync("planning-evaluation-runs", CancelledRun, Ct))!;
        var state = run["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
        state.Status = PlanningStatus.Generating; state.Phase = state.PendingCall!.Purpose;
        state.Diagnostics.Clear();
        run["run"] = InterruptedServerRun; run["campaign"] = "retention";
        // Keep a stale polling snapshot: inspection must read the authoritative index.
        run["result"]!["status"] = PlanningStatus.Generating;
        await campaign.SaveAsync("planning-evaluation-runs", InterruptedServerRun, run, Ct);
        Assert.True(await fixture.Store.TrySaveAsync(state, null, Ct));
        var request = JsonSerializer.SerializeToNode(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        var input = JsonSerializer.SerializeToUtf8Bytes(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest).Length;
        await fixture.Records.UpsertAsync("agent-planning-model-requests-v11", "benchmark", Session + ":" + RequestId, request.ToJsonString(), "test", Ct);
        await fixture.Records.UpsertAsync("agent-planning-model-http-v11", "benchmark", RequestId, new JsonObject {
            ["version"] = 1, ["tenant"] = "benchmark", ["session"] = Session, ["request_hash"] = PlanningGraphCompiler.Fingerprint(request.ToJsonString()),
            ["policy"] = state.Request.Options[PlanningTransportPolicies.OptionName]!.DeepClone(), ["reserved_attempts"] = 1,
            ["input_ceiling"] = input, ["output_ceiling"] = state.PendingCall.Request.MaxTokens, ["transport"] = null }.ToJsonString(), "test", Ct);
        await fixture.Records.UpsertAsync("agent-planning-budgets-v11", "benchmark", Session, new JsonObject {
            ["uncertainCalls"] = 1, ["estimatedCostCurrency"] = "EUR", ["journaledAttempts"] = new JsonObject {
                [RequestId + ":attempt:1"] = new JsonObject { ["inputTokenCeiling"] = input, ["outputTokenCeiling"] = state.PendingCall.Request.MaxTokens,
                    ["reservedEstimatedCost"] = .95m, ["verifiedUsage"] = null } } }.ToJsonString(), "test", Ct);
        var journal = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, RequestId, Ct))!;
        journal["input_ceiling"] = input + 256;
        journal["transport"] = new JsonObject { ["Fingerprint"] = "", ["Attempts"] = new JsonArray(), ["DelayMilliseconds"] = 0d };
        await campaign.SaveAsync(BenchmarkHttpJournal.Collection, RequestId, journal, Ct);
        await using var db = fixture.CreateDbContext();
        db.Calls.Add(new() { TenantId = "benchmark", SessionId = Session, RequestHash = RequestId, PayloadKey = Session + ":" + RequestId });
        await db.SaveChangesAsync(Ct);
        return campaign;
    }
}
