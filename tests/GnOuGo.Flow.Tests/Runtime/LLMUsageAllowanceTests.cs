using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class LLMUsageAllowanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LLMRequest Request() => new() { Provider = "fixture", Model = "priced" };

    [Theory]
    [InlineData("calls")]
    [InlineData("total_tokens")]
    [InlineData("estimated_cost_usd")]
    public async Task DraftFitsButDraftAndRequiredReviewDoNotMutateAccounting(string kind)
    {
        var sink = new Sink();
        var scope = new LLMUsageBudgetScope(new()
        {
            MaxCalls = kind == "calls" ? 1 : null,
            MaxTotalTokens = kind == "total_tokens" ? 25 : null,
            MaxEstimatedCostUsd = kind == "estimated_cost_usd" ? 20m : null
        }, sink: sink);
        var before = scope.Snapshot;
        await scope.EnsureAllowanceAsync(Request(), new Price(), 10, [5], "construction", Ct);
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.EnsureAllowanceAsync(Request(), new Price(), 10, [5, 5], "construction", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal(kind, failure.Details!["limit_kind"]!.ToString());
        Assert.Equal("not_started", failure.Details["dispatch_status"]!.ToString());
        Assert.Equal(before, scope.Snapshot);
        Assert.Equal(0, sink.Writes);
    }

    [Fact]
    public async Task ParentMonetaryLimitStillBlocksAChildWithRoom()
    {
        var parent = new LLMUsageBudgetScope(new() { MaxEstimatedCostUsd = 25 });
        var child = parent.CreateChild(new() { MaxEstimatedCostUsd = 100 });
        var parentBefore = parent.Snapshot; var childBefore = child.Snapshot;
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            child.EnsureAllowanceAsync(Request(), new Price(), 10, [5, 5], "construction", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal(parentBefore, parent.Snapshot);
        Assert.Equal(childBefore, child.Snapshot);
        await child.EnsureAllowanceAsync(Request(), new Price(), 10, [5], "construction", Ct);
    }

    [Fact]
    public async Task CurrencyAdmissionUsesValidatedQuotesWithoutPinningOrPersistingThem()
    {
        var sink = new Sink(); var exchange = new Exchange();
        var scope = new LLMUsageBudgetScope(new() { MaxEstimatedCost = new(20, "EUR") }, sink: sink, exchangeRateProvider: exchange);
        var before = scope.Snapshot;
        await scope.EnsureAllowanceAsync(Request(), new Price(), 10, [5], "construction", Ct);
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.EnsureAllowanceAsync(Request(), new Price(), 10, [5, 5], "construction", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal("EUR", failure.Details!["estimated_cost_currency"]!.ToString());
        Assert.Equal(before, scope.Snapshot);
        Assert.Empty(scope.Snapshot.ExchangeRates);
        Assert.Equal(0, sink.Writes);
        Assert.True(exchange.Calls > 0);
    }

    [Fact]
    public async Task UnknownPricingFailsBeforeChangingAnyLedger()
    {
        var scope = new LLMUsageBudgetScope(new() { MaxEstimatedCostUsd = 10 });
        var before = scope.Snapshot;
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.EnsureAllowanceAsync(Request(), null, 10, [5, 5], "construction", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, failure.Code);
        Assert.Equal("pricing", failure.Details!["limit_kind"]!.ToString());
        Assert.Equal(before, scope.Snapshot);
    }

    private sealed class Price : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null)
            => (inputTokens ?? 0) + (outputTokens ?? 0);
    }
    private sealed class Sink : ILLMUsageBudgetSink
    {
        public int Writes;
        public ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct) { Writes++; return ValueTask.CompletedTask; }
    }
    private sealed class Exchange : IExchangeRateProvider
    {
        public int Calls;
        public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct)
        { Calls++; return ValueTask.FromResult<CurrencyExchangeQuote?>(new(sourceCurrency, targetCurrency, 0.8m, DateTimeOffset.UtcNow.AddSeconds(-1), "fixture")); }
    }
}
