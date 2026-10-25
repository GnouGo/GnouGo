using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Integrations.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningHttpJournalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceWindowHeadroomSurvivesBothHostJournalsAndExactReceiptRecovery(bool workflow)
    {
        await using var host = await Host.CreateAsync(workflow, [500, 200], presentation: 6);
        await host.OpenAsync();
        var request = host.Issue(reviews: 4);
        var exact = Serialize(request);
        var response = await host.CallAsync(request);
        Assert.Equal("secret result", response.Json!["value"]!.ToString());
        Assert.Equal(2, host.Handler.Bodies.Count);
        var usage = Serialize(await host.UsageAsync());
        var journal = (await host.TransportAsync(request)).ToJsonString();
        await host.OpenAsync();
        Assert.Equal(Serialize(response), Serialize(await host.CallAsync(request)));
        Assert.Equal(exact, Serialize(request));
        Assert.Equal(usage, Serialize(await host.UsageAsync()));
        Assert.Equal(journal, (await host.TransportAsync(request)).ToJsonString());
        Assert.Equal(2, host.Handler.Bodies.Count);
        await Assert.ThrowsAsync<PlanningConflictException>(() => host.CallAsync(host.Issue(reviews: 9)));
        Assert.Equal(2, host.Handler.Bodies.Count);
        Assert.Equal(usage, Serialize(await host.UsageAsync()));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConfirmedRetriesRetainTwoReservationsAndReplayOneVerifiedReceipt(bool workflow)
    {
        await using var host = await Host.CreateAsync(workflow, [500, 429, 200]);
        await host.OpenAsync();
        var request = host.Issue(); var exact = Serialize(request);
        host.Handler.BeforeSend = async count =>
        {
            var admitted = await host.UsageAsync();
            Assert.Equal(count, admitted.Calls); Assert.Equal(count, admitted.JournaledAttempts!.Count);
            var pending = (await host.TransportAsync(request))["transport"]!["Attempts"]!.AsArray();
            Assert.Equal(count, pending.Count); Assert.Null(pending[^1]!["Status"]);
        };
        var result = await host.CallAsync(request);
        Assert.Equal("secret result", result.Json!["value"]!.ToString());
        Assert.Equal(3, host.Handler.Bodies.Count);
        Assert.Single(host.Handler.Bodies.Distinct(StringComparer.Ordinal));
        Assert.Equal(3, host.Handler.Ids.Distinct(StringComparer.Ordinal).Count());
        var usage = await host.UsageAsync();
        var attempts = usage.JournaledAttempts!.Values.ToArray();
        Assert.Equal(3, usage.Calls); Assert.Equal(3, attempts.Length);
        Assert.Equal(2, attempts.Count(a => a.VerifiedUsage is null));
        var verified = Assert.Single(attempts, a => a.VerifiedUsage is not null).VerifiedUsage!;
        Assert.Equal(10, verified.InputTokens); Assert.Equal(5, verified.OutputTokens);
        Assert.Equal(attempts.Where(a => a.VerifiedUsage is null).Sum(a => (long)a.InputTokenCeiling) + 10, usage.InputTokens);
        Assert.Equal(attempts.Where(a => a.VerifiedUsage is null).Sum(a => (long)a.OutputTokenCeiling) + 5, usage.OutputTokens);
        Assert.Equal(usage.InputTokens + usage.OutputTokens, usage.TotalTokens);
        Assert.Equal(usage.TotalTokens * Estimator.TokenCost, usage.EstimatedCost);
        Assert.Equal(3, result.Usage!["transport_attempts"]!.GetValue<int>());
        Assert.Equal(2, result.Usage["unknown_attempts"]!.GetValue<int>());
        var journal = await host.TransportAsync(request);
        Assert.Equal(new[] { 500, 429, 200 }, journal["transport"]!["Attempts"]!.AsArray().Select(a => a!["Status"]!.GetValue<int>()));
        Assert.Null(await host.Fixture.Records.GetAsync(host.HttpCollection, "other-tenant", request.ClientRequestId!, "test", Ct));

        await host.OpenAsync();
        var replay = await host.CallAsync(request);
        Assert.Equal(Serialize(result), Serialize(replay));
        Assert.Equal(Serialize(usage), Serialize(await host.UsageAsync()));
        Assert.Equal(exact, Serialize(request)); Assert.Equal(3, host.Handler.Bodies.Count);
        await host.CloseAsync();
        foreach (var path in Directory.GetFiles(host.Fixture.Root, "*", SearchOption.AllDirectories))
        {
            var contents = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path, Ct));
            Assert.DoesNotContain("secret intent", contents); Assert.DoesNotContain("secret result", contents);
        }
    }

    [Theory]
    [InlineData(false, "http-before")] [InlineData(true, "http-before")]
    [InlineData(false, "http-after")] [InlineData(true, "http-after")]
    [InlineData(false, "receipt")] [InlineData(true, "receipt")]
    public async Task InterruptedPersistenceOnlyRecoversADurableResponseWithoutResending(bool workflow, string boundary)
    {
        await using var host = await Host.CreateAsync(workflow, [200]);
        host.Records.Boundary = boundary;
        await host.OpenAsync(); var request = host.Issue(); var exact = Serialize(request);
        await Assert.ThrowsAsync<IOException>(() => host.CallAsync(request));
        Assert.True(host.Records.Interrupted); Assert.Single(host.Handler.Bodies);
        var before = await host.UsageAsync(); Assert.Equal(1, before.Calls);
        var journal = await host.TransportAsync(request);
        if (boundary == "http-before") Assert.Null(journal["transport"]!["Attempts"]![0]!["Status"]);
        else
        {
            Assert.Equal(200, journal["transport"]!["Attempts"]![0]!["Status"]!.GetValue<int>());
            // A saved success remains recoverable after the captured dispatch deadline.
            journal["deadline_utc"] = DateTimeOffset.UtcNow.AddMinutes(-1);
            await host.Fixture.Records.UpsertAsync(host.HttpCollection, "tenant", request.ClientRequestId!, journal.ToJsonString(), "test", Ct);
            host.DeploymentUnavailable = true;
        }
        await host.OpenAsync();
        if (boundary == "http-before")
        {
            var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => host.CallAsync(request));
            Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, error.Code);
            Assert.Equal(Serialize(before), Serialize(await host.UsageAsync()));
        }
        else
        {
            var recovered = await host.CallAsync(request);
            Assert.Equal("secret result", recovered.Json!["value"]!.ToString());
            var after = await host.UsageAsync(); Assert.Equal(1, after.Calls);
            Assert.Equal(15, after.TotalTokens); Assert.NotNull(Assert.Single(after.JournaledAttempts!).Value.VerifiedUsage);
            Assert.Equal(Serialize(recovered), Serialize(await host.CallAsync(request)));
            Assert.Equal(Serialize(after), Serialize(await host.UsageAsync()));
        }
        Assert.Equal(exact, Serialize(request)); Assert.Single(host.Handler.Bodies);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingHttpOutcomeRetainsItsReservationAndCannotReplay(bool workflow)
    {
        await using var host = await Host.CreateAsync(workflow, [0]);
        await host.OpenAsync(); var request = host.Issue();
        await Assert.ThrowsAsync<LLMClientException>(() => host.CallAsync(request));
        var usage = await host.UsageAsync(); Assert.Equal(1, usage.Calls);
        Assert.Null(Assert.Single(usage.JournaledAttempts!).Value.VerifiedUsage);
        await host.OpenAsync();
        var stopped = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => host.CallAsync(request));
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, stopped.Code);
        Assert.Equal(Serialize(usage), Serialize(await host.UsageAsync())); Assert.Single(host.Handler.Bodies);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ParentHeadroomPreventsASecondAttemptWithoutChargingAReview(bool workflow)
    {
        var parent = new LLMUsageBudgetScope(new() { MaxCalls = 2 });
        await using var host = await Host.CreateAsync(workflow, [500, 200], parent);
        await host.OpenAsync(); var request = host.Issue(reviews: 1);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => host.CallAsync(request));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, error.Code);
        Assert.Single(host.Handler.Bodies);
        Assert.Equal(1, parent.Snapshot.Calls); Assert.Equal(1, (await host.UsageAsync()).Calls);
        Assert.Null(Assert.Single(parent.Snapshot.JournaledAttempts!).Value.VerifiedUsage);
        Assert.Equal(parent.Snapshot.TotalTokens, (await host.UsageAsync()).TotalTokens);
        var retained = await host.TransportAsync(request);
        Assert.Equal(1, retained["reserved_attempts"]!.GetValue<int>());
        Assert.Equal(500, Assert.Single(retained["transport"]!["Attempts"]!.AsArray())!["Status"]!.GetValue<int>());
        await host.OpenAsync();
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => host.CallAsync(request));
        Assert.Single(host.Handler.Bodies); Assert.Equal(1, parent.Snapshot.Calls);
        Assert.True(JsonNode.DeepEquals(retained, await host.TransportAsync(request)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ThreeConfirmedFailuresRemainUnknownAndTerminalAcrossRestart(bool workflow)
    {
        await using var host = await Host.CreateAsync(workflow, [500, 502, 503]);
        await host.OpenAsync(); var request = host.Issue();
        await Assert.ThrowsAsync<LLMClientException>(() => host.CallAsync(request));
        var usage = await host.UsageAsync(); Assert.Equal(3, usage.Calls);
        Assert.All(usage.JournaledAttempts!.Values, attempt => Assert.Null(attempt.VerifiedUsage));
        await host.OpenAsync();
        await Assert.ThrowsAsync<LLMClientException>(() => host.CallAsync(request));
        Assert.Equal(3, host.Handler.Bodies.Count); Assert.Equal(Serialize(usage), Serialize(await host.UsageAsync()));
    }

    [Fact]
    public async Task ExpiredDispatchDeadlineDoesNotReserveOrSend()
    {
        await using var host = await Host.CreateAsync(false, [200]);
        await host.OpenAsync(); var request = host.Issue();
        var budget = new LLMUsageBudgetScope(new() { MaxCalls = 4 });
        var journal = new PlanningHttpModelJournal(host.Records, budget, host.Planning, request, new Estimator(),
            "deadline-test", "test", DateTimeOffset.UtcNow.AddSeconds(-1));
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => journal.CallAsync(host.Client, true, Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, error.Code); Assert.Equal(0, budget.Snapshot.Calls);
        Assert.Empty(host.Handler.Bodies);
        Assert.Null(await host.Fixture.Records.GetAsync("deadline-test", "tenant", request.ClientRequestId!, "test", Ct));
    }

    [Theory]
    [InlineData("receipt")] [InlineData("http-after")] [InlineData("http-before")]
    public async Task ServerRecoveryKeepsLogicalIdentityAndPhysicalAccountingSeparate(string boundary)
    {
        await using var host = await Host.CreateAsync(false, [500, 200]);
        host.Records.Boundary = boundary;
        await host.OpenAsync(); var request = host.Issue(); var exact = Serialize(request);
        await Assert.ThrowsAsync<IOException>(() => host.CallAsync(request));
        var usage = await host.UsageAsync(); Assert.Equal(2, usage.Calls);
        var state = new PlanningSession
        {
            Request = host.Planning, IntentVersion = 2, Construction = new(), Status = PlanningStatus.Stopped,
            ModelCalls = 1, ReplanAttempts = 2, ActiveMilliseconds = 123,
            PendingCall = new() { Id = request.ClientRequestId!, Purpose = "construction_intent", Request = request }
        };
        var limits = new LLMUsageBudgetLimits { MaxCalls = 8, MaxEstimatedCostUsd = 100m };
        if (boundary == "http-before")
        {
            var stopped = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningModelRecovery.PrepareAsync(state,
                host.Fixture.Records, limits, new Estimator(), new TestExchangeRateProvider(), Ct));
            Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, stopped.Code); Assert.Equal(PlanningStatus.Stopped, state.Status);
        }
        else
        {
            await PlanningModelRecovery.PrepareAsync(state, host.Fixture.Records, limits, new Estimator(), new TestExchangeRateProvider(), Ct);
            Assert.Equal(PlanningStatus.Generating, state.Status);
            Assert.Equal(Serialize(usage), Serialize(state.Usage!));
            await host.OpenAsync(); await host.CallAsync(request);
            await PlanningModelRecovery.PrepareAsync(state = new()
            {
                Request = host.Planning, IntentVersion = 2, Construction = new(), Status = PlanningStatus.Stopped,
                ModelCalls = 1, ReplanAttempts = 2, ActiveMilliseconds = 123, Usage = usage,
                PendingCall = new() { Id = request.ClientRequestId!, Purpose = "construction_intent", Request = request }
            }, host.Fixture.Records, limits, new Estimator(), new TestExchangeRateProvider(), Ct);
            Assert.Equal(2, state.Usage!.Calls);
        }
        Assert.Equal(1, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts); Assert.Equal(123, state.ActiveMilliseconds);
        Assert.Equal(request.ClientRequestId, state.PendingCall!.Id); Assert.Equal(exact, Serialize(state.PendingCall.Request));
        Assert.Equal(2, host.Handler.Bodies.Count);
    }

    [Fact]
    public async Task WorkflowReopenSettlesSavedSuccessBeforeExpiredPlanningAdmissionAndRestoresParentOnFailure()
    {
        var parent = new LLMUsageBudgetScope(new() { MaxCalls = 8 });
        await using var host = await Host.CreateAsync(true, [200], parent);
        await host.OpenAsync(); var request = host.Issue();
        await host.SavePendingAsync(request, 0);
        host.Records.Boundary = "http-after";
        await Assert.ThrowsAsync<IOException>(() => host.CallAsync(request));
        const double elapsed = 18_000_001;
        await host.SavePendingAsync(request, elapsed);
        host.Records.Boundary = "receipt"; host.Records.Interrupted = false; host.DeploymentUnavailable = true;
        await Assert.ThrowsAsync<IOException>(() => host.OpenAsync());
        Assert.Same(parent, host.LastContext!.LLMUsageBudget);
        Assert.Equal(1, parent.Snapshot.Calls); Assert.Equal(15, parent.Snapshot.TotalTokens);
        await host.OpenAsync();
        Assert.True(host.ActiveMilliseconds >= elapsed);
        Assert.Equal(15, (await host.UsageAsync()).TotalTokens);
        Assert.Equal("secret result", (await host.CallAsync(request)).Json!["value"]!.ToString());
        Assert.Single(host.Handler.Bodies); Assert.Equal(1, parent.Snapshot.Calls);
    }

    private static string Serialize(LLMRequest value) => JsonSerializer.Serialize(value, PlanningJsonContext.Default.LLMRequest);
    private static string Serialize(LLMResponse value) => JsonSerializer.Serialize(value, PlanningJsonContext.Default.LLMResponse);
    private static string Serialize(LLMUsageBudgetSnapshot value) => JsonSerializer.Serialize(value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);

    private sealed class Host : IAsyncDisposable
    {
        internal readonly PlanningPersistenceTests.StoreFixture Fixture;
        internal readonly InterruptingRecords Records;
        internal readonly Handler Handler;
        internal readonly RoutingLLMClientAdapter Client;
        internal PlanningRequest Planning = null!;
        internal bool DeploymentUnavailable;
        internal StepExecutionContext? LastContext;
        internal double ActiveMilliseconds => _runtime!.Session.ActiveMilliseconds;
        private readonly bool _workflow;
        private readonly HttpClient _http;
        private readonly PlanningSession _initial;
        private readonly LLMUsageBudgetScope? _parent;
        private IPlanningRuntimeSession? _runtime;
        private PlanningModelJournal? _model;
        private string BudgetCollection => _workflow ? "flow-planning-budgets-v10" : PlanningBudgetSink.Collection;
        internal string HttpCollection => _workflow ? "flow-planning-model-http-v10" : PlanningModelJournal.HttpCollection;

        private Host(PlanningPersistenceTests.StoreFixture fixture, bool workflow, int[] statuses, LLMUsageBudgetScope? parent, int? presentation)
        {
            Fixture = fixture; Records = new(fixture.Records); _workflow = workflow; _parent = parent;
            Handler = new(statuses); _http = new(Handler);
            Client = new(new RoutingLLMClient(_http, new LLMOptions
            {
                DefaultProvider = "fixture", DefaultModel = "journal-fixture",
                Models = { ["fixture"] = new() { Type = "openai", Url = "https://example.invalid/v1/chat/completions" } },
                ModelOverrides = { ["openai/journal-fixture"] = new() { Id = "journal-fixture", ProviderType = "openai",
                    MaxInputTokens = 128000, MaxOutputTokens = 65536, ContextWindowTokens = 200000,
                    Capabilities = new() { SupportsStructuredOutput = true, SupportsReasoningEffort = true, SupportedReasoningEfforts = ["medium"] } } }
            }));
            var policy = new PlanningConstructionPhasePolicy
            {
                Navigation = new() { TargetInputTokens = 2048, MaxInputTokens = 4096, OutputTokens = 128 },
                Draft = new() { TargetInputTokens = 2048, MaxInputTokens = 4096, OutputTokens = 128 },
                Review = new() { TargetInputTokens = 2048, MaxInputTokens = 4096, OutputTokens = 64 }
            };
            _initial = new() { Construction = new() { Limits = new() { PhasePolicy = policy } }, Request = new()
            {
                TenantId = "tenant", SessionId = "http-session", Prompt = "secret intent", MaxModelCalls = 8,
                Generation = new() { MaxInputTokensPerRequest = 4096, MaxOutputTokens = 128 },
                Options = new() { ["llm_budget"] = new JsonObject { ["max_calls"] = 8, ["max_estimated_cost"] = new JsonObject { ["amount"] = 100m, ["currency"] = "USD" } }, ["construction_version"] = 2,
                    ["construction_limits"] = JsonSerializer.SerializeToNode(new PlanningConstructionOptions { PhasePolicy = policy }, PlanningJsonContext.Default.PlanningConstructionOptions) }
            } };
            if (presentation is { } version) _initial.Request.Options["construction_presentation_version"] = version;
            PlanningTransportPolicies.Capture(_initial.Request, new() { BaseDelayMilliseconds = 1, MaxDelayMilliseconds = 1, MaxTotalDelayMilliseconds = 10 });
        }

        internal static async Task<Host> CreateAsync(bool workflow, int[] statuses, LLMUsageBudgetScope? parent = null, int? presentation = null) =>
            new(await PlanningPersistenceTests.StoreFixture.CreateAsync(), workflow, statuses, parent, presentation);

        internal async Task OpenAsync()
        {
            await CloseAsync();
            if (_workflow)
            {
                LastContext = new()
                {
                    Engine = new WorkflowEngine { LLMClient = Client, ModelUsageCostEstimator = new Estimator(),
                        LLMCapabilities = DeploymentUnavailable ? new UnavailableCapabilities() : null },
                    LLMUsageBudget = _parent, Limits = new() { RunId = "run", TenantId = "tenant" }, Data = new(),
                    Step = new() { Source = new StepDef { Id = "plan", Type = "workflow.plan" } }
                };
                _runtime = await new WorkflowPlanningRuntimeFactory(Records, Path.Combine(Fixture.Root, "leases")).OpenAsync(LastContext, _initial, Ct);
                Planning = _runtime.Session.Request;
            }
            else
            {
                Planning = _initial.Request;
                var snapshot = await ReadUsageAsync();
                var budget = new LLMUsageBudgetScope(new() { MaxCalls = 8, MaxEstimatedCostUsd = 100m }, snapshot, _parent,
                    new PlanningBudgetSink(Records, "tenant", Planning.SessionId));
                _model = new(Client, Fixture, Records, "tenant", Planning.SessionId, budget, new Estimator(), Planning.Generation,
                    capabilities: DeploymentUnavailable ? new UnavailableCapabilities() : null, planning: Planning);
            }
        }

        internal LLMRequest Issue(int reviews = 0)
        {
            var request = PlanningConstructionPhasePolicies.Apply(new()
            {
                Provider = "fixture", Model = "journal-fixture", Prompt = "secret intent", RequiredReviewCalls = reviews,
                StructuredOutputStrict = true,
                StructuredOutputSchema = JsonNode.Parse("""{"title":"construction_intent","type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")
            }, Planning, "construction_intent");
            request.ClientRequestId = Planning.SessionId + ":1:" + PlanningGraphCompiler.Fingerprint(Serialize(request));
            return request;
        }

        internal Task<LLMResponse> CallAsync(LLMRequest request) => _workflow
            ? _runtime!.Runtime.CallAsync(request, "construction_intent", Ct) : _model!.CallAsync(request, Ct);
        internal Task SavePendingAsync(LLMRequest request, double elapsed)
        {
            _runtime!.Session.PendingCall = new() { Id = request.ClientRequestId!, Purpose = "construction_intent", Request = request };
            _runtime.Session.ActiveMilliseconds = elapsed;
            return _runtime.Runtime.CheckpointAsync(_runtime.Session, Ct);
        }
        private async Task<LLMUsageBudgetSnapshot?> ReadUsageAsync()
        {
            var saved = await Fixture.Records.GetAsync(BudgetCollection, "tenant", Planning.SessionId, "test", Ct);
            return saved is null ? null : JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        }
        internal async Task<LLMUsageBudgetSnapshot> UsageAsync() => (await ReadUsageAsync())!;
        internal async Task<JsonObject> TransportAsync(LLMRequest request) => JsonNode.Parse(
            (await Fixture.Records.GetAsync(HttpCollection, "tenant", request.ClientRequestId!, "test", Ct))!.Value)!.AsObject();
        internal async Task CloseAsync()
        {
            if (_runtime is not null) { await _runtime.DisposeAsync(); _runtime = null; }
        }
        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
            _http.Dispose(); await Fixture.DisposeAsync();
        }
    }

    private sealed class Handler(int[] statuses) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        internal List<string> Ids { get; } = [];
        internal Func<int, Task>? BeforeSend;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            Ids.Add(request.Headers.GetValues("X-Client-Request-Id").Single());
            if (BeforeSend is not null) await BeforeSend(Bodies.Count);
            var status = statuses[Bodies.Count - 1];
            if (status == 0) throw new HttpRequestException("No HTTP outcome.");
            var result = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(status == 200
                ? """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"value\":\"secret result\"}"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""
                : status == 429 ? """{"error":{"code":"rate_limit_exceeded"}}""" : """{"error":{"code":"upstream_error"}}""", Encoding.UTF8, "application/json") };
            if (status == 429) result.Headers.TryAddWithoutValidation("Retry-After", "0");
            return result;
        }
    }

    private sealed class Estimator : IModelUsageCostEstimator
    {
        internal const decimal TokenCost = 0.000001m;
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null)
            => ((inputTokens ?? 0) + (outputTokens ?? 0)) * TokenCost;
    }

    private sealed class UnavailableCapabilities : ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => throw new InvalidOperationException("Saved receipt recovery must not read deployment limits.");
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("Saved receipt recovery must not repeat schema admission.");
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("Saved receipt recovery must not repeat reasoning admission.");
    }

    private sealed class InterruptingRecords(IKeyVaultRecordStore inner) : IKeyVaultRecordStore
    {
        internal string? Boundary;
        internal bool Interrupted;
        public Task<KeyVaultRecordValue?> GetAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) => inner.GetAsync(collection, tenantId, key, author, ct);
        public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(string collection, string tenantId, string author, CancellationToken ct = default) => inner.ListAsync(collection, tenantId, author, ct);
        public Task<bool> DeleteAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default) => inner.DeleteAsync(collection, tenantId, key, author, ct);
        public async Task<KeyVaultRecordValue> UpsertAsync(string collection, string tenantId, string key, string value, string author, CancellationToken ct = default)
        {
            var interrupt = !Interrupted && (Boundary == "receipt" && collection.EndsWith("model-receipts-v10", StringComparison.Ordinal) ||
                Boundary is "http-before" or "http-after" && collection.EndsWith("model-http-v10", StringComparison.Ordinal) &&
                JsonNode.Parse(value)?["transport"]?["Attempts"]?.AsArray().LastOrDefault()?["Status"]?.GetValue<int>() == 200);
            if (interrupt)
            {
                Interrupted = true;
                if (Boundary == "http-after") await inner.UpsertAsync(collection, tenantId, key, value, author, ct);
                throw new IOException("Injected journal persistence interruption.");
            }
            return await inner.UpsertAsync(collection, tenantId, key, value, author, ct);
        }
    }
}
