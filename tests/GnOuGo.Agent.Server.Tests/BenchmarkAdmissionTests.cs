using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class BenchmarkAdmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LLMOptions Options() => new() { DefaultProvider = "fixture", DefaultModel = "pinned",
        Models = { ["fixture"] = new() { Type = "openai" } } };

    [Theory]
    [InlineData("missing-price")]
    [InlineData("missing-quote")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("invalid-rate")]
    [InlineData("wrong-currency")]
    [InlineData("network")]
    public async Task ReadinessStopsBeforeProviderOrExternalWork(string defect)
    {
        using var fixture = new Fixture();
        var rates = new Rates(fixture.Time) { Defect = defect };
        var provider = new Provider(fixture.Campaign);
        using var model = new KeyVaultBenchmarkModel(fixture.Campaign, Options(), provider, rates,
            new Price { Missing = defect == "missing-price" }, fixture.Time);
        var readiness = await model.ReadinessAsync(Ct);
        Assert.False(readiness["ready"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, readiness["accounting"]!["error"]!.ToString());
        Assert.Equal("not_started", readiness["accounting"]!["details"]!["dispatch_status"]!.ToString());
        // The real host exits before constructing the MCP transport, starting a proxy or executing any workflow.
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => LiveWorkflowEvaluation.RunAsync(
            ["--case", "amazon", "--run", "unused"], "execute", fixture.Campaign, model, fixture.Root));
        Assert.Equal(0, provider.Calls);
        Assert.Null(await fixture.Campaign.LoadAsync(SchemaPortabilityCampaign.Collection, "run:unused", Ct));
        Assert.Equal(0L, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct))["calls"]!.GetValue<long>());
    }

    [Fact]
    public async Task FreshQuotesSurviveRestartButExpiredQuotesNeverAuthorizeNewRequests()
    {
        using var fixture = new Fixture();
        var rates = new Rates(fixture.Time);
        using (var model = fixture.Model(rates)) Assert.True((await model.ReadinessAsync(Ct))["ready"]!.GetValue<bool>());
        var original = await fixture.Campaign.LoadAsync("planning-evaluation-exchange-rates", "USD-EUR", Ct);
        Assert.Equal(1, rates.Calls);
        rates.Defect = "network";
        using (var restarted = fixture.Model(rates)) Assert.True((await restarted.ReadinessAsync(Ct))["ready"]!.GetValue<bool>());
        Assert.Equal(1, rates.Calls);
        fixture.Time.Advance(TimeSpan.FromDays(8));
        using (var restarted = fixture.Model(rates)) Assert.False((await restarted.ReadinessAsync(Ct))["ready"]!.GetValue<bool>());
        Assert.Equal(2, rates.Calls);
        Assert.True(JsonNode.DeepEquals(original, await fixture.Campaign.LoadAsync("planning-evaluation-exchange-rates", "USD-EUR", Ct)));
        using var other = new KeyVaultBenchmarkModel(new(fixture.Records, "other"), Options(), new Provider(fixture.Campaign), rates, new Price(), fixture.Time);
        Assert.False((await other.ReadinessAsync(Ct))["ready"]!.GetValue<bool>());
        Assert.Equal(3, rates.Calls);
    }

    [Fact]
    public async Task SettlementUsesAdmittedQuoteAndCommittedReceiptReplaysWithoutNetwork()
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time);
        var provider = new Provider(fixture.Campaign)
        {
            AfterDispatch = () => { fixture.Time.Advance(TimeSpan.FromDays(8)); rates.Defect = "network"; }
        };
        var request = new LLMRequest { ClientRequestId = "fresh:1", Prompt = "observed", MaxTokens = 8192 };
        using (var model = fixture.Model(rates, provider))
        {
            var response = await model.CallExecutionAsync(request, Ct);
            Assert.Equal(.09m, response.Usage!["benchmark_cost_eur"]!.GetValue<decimal>());
        }
        Assert.Equal(1, provider.Calls); Assert.Equal(1, rates.Calls);
        var record = await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId, Ct);
        Assert.Equal(.9m, record!["exchange_quote"]!["rate"]!.GetValue<decimal>());
        Assert.Equal("fixture-authority", record["exchange_quote"]!["source"]!.ToString());
        using (var restarted = fixture.Model(rates, provider))
            Assert.Equal(.09m, (await restarted.CallExecutionAsync(request, Ct)).Usage!["benchmark_cost_eur"]!.GetValue<decimal>());
        Assert.Equal(1, provider.Calls); Assert.Equal(1, rates.Calls);
    }

    [Fact]
    public async Task TypedNonDispatchIsDurableAndCannotRedispatchOrChangeItsIdentity()
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time) { Defect = "missing-quote" };
        var provider = new Provider(fixture.Campaign);
        var request = new LLMRequest { ClientRequestId = "denied:1", Prompt = "original", MaxTokens = 8192 };
        using (var model = fixture.Model(rates, provider))
        {
            var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => model.CallExecutionAsync(request, Ct));
            Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, error.Code);
            Assert.Equal("currency_quote_unavailable", error.Details!["reason"]!.ToString());
            Assert.Equal(0L, (await model.PartialUsageAsync(request.ClientRequestId, Ct))!["input_tokens"]!.GetValue<long>());
        }
        rates.Defect = null;
        using (var restarted = fixture.Model(rates, provider))
        {
            await Assert.ThrowsAsync<WorkflowRuntimeException>(() => restarted.CallExecutionAsync(request, Ct));
            request.Prompt = "changed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CallExecutionAsync(request, Ct));
        }
        Assert.Equal(0, provider.Calls); Assert.Equal(1, rates.Calls);
        Assert.False(await fixture.Campaign.HasUncertainRequestAsync(Ct));
        Assert.Null(await fixture.Campaign.LoadAsync("planning-evaluation-requests", request.ClientRequestId, Ct));
    }

    [Theory]
    [InlineData("mapping.dynamic")]
    [InlineData("llm.call")]
    public async Task VerifiedFailurePersistsAndCleansUpWithoutRetry(string stepType)
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time) { Defect = "missing-quote" };
        var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        var workflow = Workflow(stepType);
        var client = new IdentifiedClient(model);
        var result = await fixture.Engine(client).ExecuteAsync(workflow, new JsonObject(), Ct);
        Assert.True(result.Error?.Code == ErrorCodes.LlmBudgetUnverifiable, result.Error?.Code + ": " + result.Error?.Message);
        var saved = (await fixture.Store().ReadAsync("tenant", "run", Ct))!;
        Assert.True(saved.FinalizationCompleted);
        Assert.Equal("completed", Assert.Single(saved.Invocations.Values, i => i.IsFinalization && i.StepType == "set").Status);
        Assert.DoesNotContain(saved.Invocations.Values, i => i.Status == "needs_reconciliation");
        Assert.Equal(0, provider.Calls);
        var replay = await fixture.Engine(client).ResumeAsync("tenant", "run", saved.Revision, workflow, Ct);
        Assert.Equal(result.Error?.Code, replay.Error?.Code);
        Assert.Equal(0, provider.Calls); Assert.Equal(1, rates.Calls);
        Assert.Null(await fixture.Store().ReadAsync("other", "run", Ct));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("post-dispatch-budget")]
    [InlineData("failed-preflight-record")]
    [InlineData("failed-completion-record")]
    public async Task UnverifiedOrUndurableCompletionStillBlocksCleanupAndReplay(string defect)
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time);
        var provider = new Provider(fixture.Campaign);
        if (defect == "failed-preflight-record") { rates.Defect = "missing-quote"; fixture.Records.FailCollection = "planning-evaluation-failures"; }
        if (defect == "failed-completion-record") fixture.Records.FailCollection = "planning-evaluation-receipts";
        if (defect == "transport") provider.Failure = new IOException("Completion unavailable");
        if (defect == "post-dispatch-budget") provider.Failure = new WorkflowRuntimeException(ErrorCodes.LlmBudgetUnverifiable, "Settlement unavailable");
        using var model = fixture.Model(rates, provider);
        var workflow = Workflow("mapping.dynamic");
        var result = await fixture.Engine(model).ExecuteAsync(workflow, new JsonObject(), Ct);
        Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error?.Code);
        var saved = (await fixture.Store().ReadAsync("tenant", "run", Ct))!;
        Assert.DoesNotContain(saved.Invocations.Values, i => i.IsFinalization && i.Status == "completed");
        var calls = provider.Calls;
        await fixture.Engine(model).ResumeAsync("tenant", "run", saved.Revision, workflow, Ct);
        Assert.Equal(calls, provider.Calls);
    }

    [Fact]
    public async Task HistoricalUnknownAdmissionIsNotReclassifiedOrRepriced()
    {
        using var fixture = new Fixture(); const string id = "historical:1";
        var request = new LLMRequest { ClientRequestId = id, MaxTokens = 8192 };
        var journal = new BenchmarkHttpJournal(fixture.Campaign, id, 100, 20, 1m);
        await journal.SaveAsync(new() { Attempts = [new() { Id = "unknown" }] }, Ct);
        await fixture.Campaign.SaveAsync("planning-evaluation-requests", id,
            System.Text.Json.JsonSerializer.SerializeToNode(request, GnOuGo.Flow.Core.Planning.PlanningJsonContext.Default.LLMRequest)!.AsObject(), Ct);
        var original = await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct);
        var rates = new Rates(fixture.Time) { Defect = "network" };
        using var model = fixture.Model(rates);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.CallExecutionAsync(request, Ct));
        Assert.Equal(0, rates.Calls);
        Assert.True(JsonNode.DeepEquals(original, await fixture.Campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, Ct)));
        Assert.Equal(1m, (await BenchmarkHttpJournal.AccountingAsync(fixture.Campaign, ct: Ct))["reserved_cost_eur"]!.GetValue<decimal>());
        Assert.Null((await fixture.Campaign.LoadAsync("planning-evaluation-failures", id, Ct))?["dispatch_status"]);
    }

    [Fact]
    public async Task FailedFlowReceiptPublicationRemainsUncertainAfterRestart()
    {
        using var fixture = new Fixture(); var rates = new Rates(fixture.Time) { Defect = "missing-quote" };
        var provider = new Provider(fixture.Campaign);
        fixture.RunRecords.FailContent = "LLM_BUDGET_UNVERIFIABLE";
        using var model = fixture.Model(rates, provider);
        var workflow = Workflow("mapping.dynamic"); RunResult? result = null;
        var failure = await Record.ExceptionAsync(async () => result = await fixture.Engine(model).ExecuteAsync(workflow, new JsonObject(), Ct));
        Assert.True(failure is not null || result?.Success == false);
        fixture.RunRecords.FailContent = null;
        var saved = (await fixture.Store().ReadAsync("tenant", "run", Ct))!;
        Assert.DoesNotContain(saved.Invocations.Values, i => i.IsFinalization && i.Status == "completed");
        Assert.Contains(saved.Invocations.Values, i => i.Recovery == StepRecovery.External && i.DispatchedAt is not null && i.CompletedAt is null);
        var resumed = await fixture.Engine(model).ResumeAsync("tenant", "run", saved.Revision, workflow, Ct);
        Assert.Equal("RUN_NEEDS_RECONCILIATION", resumed.Error?.Code);
        Assert.Equal(0, provider.Calls); Assert.Equal(1, rates.Calls);
    }

    [Fact]
    public async Task CancellationDuringQuoteResolutionIsNotDisguisedAsAccountingRefusal()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
        var rates = new Rates(fixture.Time) { Cancel = cancel }; var provider = new Provider(fixture.Campaign);
        using var model = fixture.Model(rates, provider);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.CallExecutionAsync(new() { ClientRequestId = "cancelled:1", MaxTokens = 8192 }, cancel.Token));
        Assert.Equal(0, provider.Calls);
        Assert.Null((await fixture.Campaign.LoadAsync("planning-evaluation-failures", "cancelled:1", Ct))?["dispatch_status"]);
        Assert.False(await fixture.Campaign.HasUncertainRequestAsync(Ct));
    }

    private static CompiledWorkflow Workflow(string stepType)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: model
                    type: mapping.dynamic
                    input:
                      sources: {raw: {name: observed}}
                      objective: Extract the observed name.
                      binding: fixture
                      producer_contract: fixture
                    output_schema:
                      type: object
                      properties:
                        value: {type: object, properties: {name: {type: string}}, required: [name], additionalProperties: false}
                      required: [value]
                      additionalProperties: false
                finally:
                  - {id: cleanup, type: set, input: {value: cleaned}}
            """);
        if (stepType == "llm.call")
        {
            var step = document.Workflows["main"].Steps[0]; step.Type = stepType; step.OutputSchema = null;
            step.Input = new JsonObject { ["model"] = "pinned", ["prompt"] = "observed", ["max_tokens"] = 8192 };
        }
        return new WorkflowCompiler().Compile(document).Workflows["main"];
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan time) => _now += time;
    }
    private sealed class Rates(Clock time) : IExchangeRateProvider
    {
        internal int Calls; internal string? Defect; internal CancellationTokenSource? Cancel;
        public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct)
        {
            Calls++;
            Cancel?.Cancel(); ct.ThrowIfCancellationRequested();
            if (Defect == "network") throw new HttpRequestException("Fixture unavailable");
            return ValueTask.FromResult<CurrencyExchangeQuote?>(Defect == "missing-quote" ? null : new(
                Defect == "wrong-currency" ? "GBP" : sourceCurrency, targetCurrency, Defect == "invalid-rate" ? 0m : .9m,
                time.GetUtcNow() + (Defect == "stale" ? TimeSpan.FromDays(-8) : Defect == "future" ? TimeSpan.FromDays(1) : TimeSpan.Zero), "fixture-authority"));
        }
    }
    private sealed class Price : IModelUsageCostEstimator
    {
        internal bool Missing;
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => Missing ? null : .1m;
    }
    private sealed class IdentifiedClient(KeyVaultBenchmarkModel model) : ILLMClient
    {
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            request.ClientRequestId ??= "runtime:1";
            return model.CallExecutionAsync(request, ct);
        }
    }
    private sealed class Provider(BenchmarkCampaign campaign) : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls; internal Action? AfterDispatch; internal Exception? Failure;
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(96000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var record = (await campaign.LoadAsync(BenchmarkHttpJournal.Collection, request.ClientRequestId!, ct))!;
            var journal = new BenchmarkHttpJournal(campaign, request.ClientRequestId!, record["input_ceiling"]!.GetValue<long>(),
                record["output_ceiling"]!.GetValue<long>(), record["cost_ceiling_eur"]!.GetValue<decimal>(), record["session_attempt_limit"]?.GetValue<int>());
            var state = (await journal.LoadAsync(ct))!;
            state.Attempts.Add(new() { Id = "attempt" }); await journal.SaveAsync(state, ct);
            Calls++; AfterDispatch?.Invoke(); if (Failure is not null) throw Failure;
            state.Attempts[^1].Status = 200; await journal.SaveAsync(state, CancellationToken.None);
            return new() { Text = "{\"script\":\"source.raw\"}", Json = new JsonObject { ["script"] = "source.raw" },
                Usage = new JsonObject { ["input_tokens"] = 10L, ["output_tokens"] = 2L } };
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("admission-fixture-").FullName;
        internal Clock Time { get; } = new();
        internal Records Records { get; }
        internal Records RunRecords { get; }
        internal BenchmarkCampaign Campaign => new(Records, "fixture");
        internal Fixture()
        {
            Records = new(new KeyVaultRecordStore(Path.Combine(Root, "vault.db")));
            RunRecords = new(new KeyVaultRecordStore(Path.Combine(Root, "runs.db")));
        }
        internal KeyVaultBenchmarkModel Model(Rates rates, Provider? provider = null) => new(Campaign, Options(), provider ?? new Provider(Campaign), rates, new Price(), Time);
        internal EncryptedWorkflowRunStore Store() => new(RunRecords, Path.Combine(Root, "index.db"), Path.Combine(Root, "owners"));
        internal WorkflowEngine Engine(ILLMClient client) => new() { LLMClient = client, RunStore = Store(), LlmDefaults = new() { Model = "pinned" }, Limits = new() { TenantId = "tenant", RunId = "run" } };
        public void Dispose() => Directory.Delete(Root, true);
    }
    private sealed class Records(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        internal string? FailCollection;
        internal string? FailContent;
        public Task<KeyVaultRecordValue?> GetAsync(string c, string t, string k, string a, CancellationToken ct = default) => inner.GetAsync(c, t, k, a, ct);
        public Task<KeyVaultRecordValue> UpsertAsync(string c, string t, string k, string v, string a, CancellationToken ct = default)
            => c == FailCollection || FailContent is not null && v.Contains(FailContent, StringComparison.Ordinal)
                ? throw new IOException("Injected persistence failure") : inner.UpsertAsync(c, t, k, v, a, ct);
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string c, string t, string a, CancellationToken ct = default) => inner.ListAsync(c, t, a, ct);
        public Task<bool> DeleteAsync(string c, string t, string k, string a, CancellationToken ct = default) => inner.DeleteAsync(c, t, k, a, ct);
    }
}
