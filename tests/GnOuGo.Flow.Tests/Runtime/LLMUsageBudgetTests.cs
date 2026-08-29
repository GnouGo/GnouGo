using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class LLMUsageBudgetTests
{
    [Fact]
    public async Task CallBudget_RejectsBeforeDispatchAtCallLimit()
    {
        var client = new StubClient(Usage(2, 3));
        var scope = Scope(maxCalls: 1);

        await scope.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.False(failure.Retryable);
        Assert.Equal("calls", failure.Details!["limit_kind"]!.GetValue<string>());
        Assert.Equal(1, client.CallCount);
        Assert.Equal(1, scope.Snapshot.Calls);
    }

    [Fact]
    public async Task TokenBudget_AccountsAndRejectsOverBudgetResponse()
    {
        var client = new StubClient(Usage(6, 5));
        var scope = Scope(maxTokens: 10);

        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal("total_tokens", failure.Details!["limit_kind"]!.GetValue<string>());
        Assert.Equal(11, scope.Snapshot.TotalTokens);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task TokenBudget_DoesNotTrustUnderreportedTotalBelowComponentUsage()
    {
        var usage = Usage(6, 5);
        usage["total_tokens"] = 1;
        var scope = Scope(maxTokens: 10);

        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(
                new StubClient(usage),
                null,
                Request(),
                "neutral.stage",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal(11, scope.Snapshot.TotalTokens);
    }

    [Fact]
    public async Task TokenBudget_MissingUsageFailsClosed()
    {
        var client = new StubClient(null);
        var scope = Scope(maxTokens: 10);

        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, failure.Code);
        Assert.Equal("usage", failure.Details!["limit_kind"]!.GetValue<string>());
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task CostBudget_UnknownPricingFailsBeforeDispatch()
    {
        var client = new StubClient(Usage(1, 1));
        var scope = Scope(maxCost: 5m);

        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, failure.Code);
        Assert.Equal("pricing", failure.Details!["limit_kind"]!.GetValue<string>());
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task CostBudget_AccountsAndRejectsOverBudgetResponse()
    {
        var client = new StubClient(Usage(1, 1));
        var scope = Scope(maxCost: 1m);

        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(client, new FixedCostEstimator(1m), Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal("estimated_cost_usd", failure.Details!["limit_kind"]!.GetValue<string>());
        Assert.Equal(2m, scope.Snapshot.EstimatedCostUsd);
    }

    [Fact]
    public async Task ChildBudget_AlsoConsumesParentBudget()
    {
        var parent = Scope(maxCalls: 2);
        var child = parent.CreateChild(new LLMUsageBudgetLimits { MaxCalls = 1 });
        var client = new StubClient(Usage(1, 1));

        await child.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            child.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal(1, child.Snapshot.Calls);
        Assert.Equal(1, parent.Snapshot.Calls);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task ElapsedBudget_RejectsBeforeDispatch()
    {
        var clock = new MutableTimeProvider();
        var scope = new LLMUsageBudgetScope(
            new LLMUsageBudgetLimits { MaxElapsed = TimeSpan.FromMinutes(1) },
            timeProvider: clock);
        clock.Advance(TimeSpan.FromMinutes(1));
        var client = new StubClient(Usage(1, 1));

        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal("elapsed_ms", failure.Details!["limit_kind"]!.GetValue<string>());
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task CancelledCall_StopsBeforeReservationAndDispatch()
    {
        var scope = Scope(maxCalls: 2);
        var client = new StubClient(Usage(1, 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.CallAsync(client, null, Request(), "neutral.stage", cancellation.Token));

        Assert.Equal(0, client.CallCount);
        Assert.Equal(0, scope.Snapshot.Calls);
    }

    [Fact]
    public void InitialSnapshot_RejectsInconsistentTokenTotals()
    {
        var snapshot = new LLMUsageBudgetSnapshot
        {
            StartedAtUtc = DateTimeOffset.UtcNow,
            Calls = 1,
            InputTokens = 5,
            OutputTokens = 4,
            TotalTokens = 1
        };

        Assert.Throws<ArgumentException>(() => new LLMUsageBudgetScope(
            new LLMUsageBudgetLimits { MaxCalls = 2 },
            snapshot));
    }

    [Fact]
    public async Task MonetaryBudget_SerializesUnaccountedCalls()
    {
        var client = new ConcurrentClient();
        var scope = Scope(maxCalls: 3, maxCost: 100m);
        var estimator = new FixedCostEstimator(0.01m);

        await Task.WhenAll(
            scope.CallAsync(client, estimator, Request(), "neutral.stage", TestContext.Current.CancellationToken),
            scope.CallAsync(client, estimator, Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(1, client.MaxConcurrency);
        Assert.Equal(2, scope.Snapshot.Calls);
    }

    [Fact]
    public async Task ProviderFailure_ConsumesCallAndReleasesMonetaryGate()
    {
        var client = new FailOnceClient();
        var scope = Scope(maxCalls: 3, maxCost: 100m);
        var estimator = new FixedCostEstimator(0.01m);

        await Assert.ThrowsAsync<LLMClientException>(() =>
            scope.CallAsync(client, estimator, Request(), "neutral.stage", TestContext.Current.CancellationToken));
        var response = await scope.CallAsync(
            client,
            estimator,
            Request(),
            "neutral.stage",
            TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, scope.Snapshot.Calls);
        Assert.Equal(2, client.CallCount);
    }

    [Fact]
    public async Task DurableSink_ReceivesOnlyRedactedSnapshots()
    {
        var sink = new RecordingSink();
        var scope = new LLMUsageBudgetScope(
            new LLMUsageBudgetLimits { MaxCalls = 2, MaxTotalTokens = 10 },
            sink: sink);

        await scope.CallAsync(
            new StubClient(Usage(1, 1)),
            null,
            new LLMRequest { Provider = "random-provider", Model = "random-model", Prompt = "secret prompt" },
            "stage with unsafe/value",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, sink.Snapshots.Count);
        Assert.All(sink.Snapshots, snapshot => Assert.IsType<LLMUsageBudgetSnapshot>(snapshot));
        var serialized = string.Join('|', sink.Snapshots.Select(static snapshot => snapshot.ToString()));
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentAccounting_SerializesDurableLedgerAndPersistsLatestSnapshot()
    {
        var sink = new ConcurrentRecordingSink();
        var scope = new LLMUsageBudgetScope(
            new LLMUsageBudgetLimits { MaxCalls = 8, MaxTotalTokens = 100 },
            sink: sink);
        var client = new StubClient(Usage(1, 1));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            scope.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken)));

        Assert.Equal(1, sink.MaxConcurrency);
        Assert.Equal(scope.Snapshot, sink.Snapshots[^1]);
    }

    [Fact]
    public async Task DurableSinkFailure_FailsClosedBeforeProviderDispatch()
    {
        var scope = new LLMUsageBudgetScope(
            new LLMUsageBudgetLimits { MaxCalls = 2 },
            sink: new ThrowingSink());
        var client = new StubClient(Usage(1, 1));

        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(client, null, Request(), "neutral.stage", TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, failure.Code);
        Assert.Equal("ledger", failure.Details!["limit_kind"]!.GetValue<string>());
        Assert.Equal(0, client.CallCount);
        Assert.Equal(0, scope.Snapshot.Calls);
    }

    [Fact]
    public async Task BudgetFailure_MetadataDoesNotContainRequestContentOrIdentities()
    {
        var scope = Scope(maxCalls: 1);
        var request = new LLMRequest
        {
            Provider = "provider-sensitive-value",
            Model = "model-sensitive-value",
            Prompt = "prompt-sensitive-value"
        };
        var client = new StubClient(Usage(1, 1));
        await scope.CallAsync(client, null, request, "stage/unsafe value", TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.CallAsync(client, null, request, "stage/unsafe value", TestContext.Current.CancellationToken));
        var serialized = failure.Details!.ToJsonString();

        Assert.DoesNotContain("provider-sensitive-value", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("model-sensitive-value", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt-sensitive-value", serialized, StringComparison.Ordinal);
        Assert.Equal("stage_unsafe_value", failure.Details["stage"]!.GetValue<string>());
    }

    private static LLMUsageBudgetScope Scope(
        int? maxCalls = null,
        long? maxTokens = null,
        decimal? maxCost = null)
        => new(new LLMUsageBudgetLimits
        {
            MaxCalls = maxCalls,
            MaxTotalTokens = maxTokens,
            MaxEstimatedCostUsd = maxCost
        });

    private static LLMRequest Request() => new()
    {
        Provider = "neutral-provider",
        Model = "neutral-model",
        Prompt = "neutral request"
    };

    private static JsonObject Usage(long input, long output) => new()
    {
        ["input_tokens"] = input,
        ["output_tokens"] = output,
        ["total_tokens"] = input + output
    };

    private sealed class StubClient(JsonNode? usage) : ILLMClient
    {
        public int CallCount { get; private set; }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new LLMResponse { Text = "ok", Usage = usage?.DeepClone() });
        }
    }

    private sealed class ConcurrentClient : ILLMClient
    {
        private int _active;
        private int _maxConcurrency;
        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrency);
            }
            while (active > observed
                   && Interlocked.CompareExchange(ref _maxConcurrency, active, observed) != observed);

            try
            {
                await Task.Delay(40, ct);
                return new LLMResponse { Text = "ok", Usage = Usage(1, 1) };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class FailOnceClient : ILLMClient
    {
        public int CallCount { get; private set; }

        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new LLMClientException(
                    LLMClientFailureKind.Transport,
                    "redacted transport failure",
                    retryable: true);
            }

            return Task.FromResult(new LLMResponse { Text = "ok", Usage = Usage(1, 1) });
        }
    }

    private sealed class FixedCostEstimator(decimal ratePerToken) : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(
            string? model,
            long? inputTokens = null,
            long? outputTokens = null,
            string? providerType = null)
            => ((inputTokens ?? 0) + (outputTokens ?? 0)) * ratePerToken;
    }

    private sealed class RecordingSink : ILLMUsageBudgetSink
    {
        public List<LLMUsageBudgetSnapshot> Snapshots { get; } = [];

        public ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct)
        {
            Snapshots.Add(snapshot with { });
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : ILLMUsageBudgetSink
    {
        public ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct)
            => ValueTask.FromException(new IOException("sensitive sink failure"));
    }

    private sealed class ConcurrentRecordingSink : ILLMUsageBudgetSink
    {
        private readonly object _gate = new();
        private int _active;
        private int _maxConcurrency;

        public List<LLMUsageBudgetSnapshot> Snapshots { get; } = [];
        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrency);
            }
            while (active > observed
                   && Interlocked.CompareExchange(ref _maxConcurrency, active, observed) != observed);

            try
            {
                await Task.Delay(5, ct);
                lock (_gate)
                    Snapshots.Add(snapshot with { });
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
