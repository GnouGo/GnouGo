using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class LLMJournaledAttemptBudgetTests
{
    private static readonly IModelUsageCostEstimator Pricing = new CostEstimator();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LLMRequest Request() => new() { Model = "model", Provider = "provider", Prompt = "private original" };
    private static JsonObject Usage(long input, long output) => new()
        { ["input_tokens"] = input, ["output_tokens"] = output, ["total_tokens"] = input + output };

    [Fact]
    public async Task AdvisoryTokenLimitsPropagateToChildrenAndRecoveryWithoutWaivingCallOrMoneyGuards()
    {
        var limits = new LLMUsageBudgetLimits { MaxCalls = 2, MaxTotalTokens = 1 };
        var parent = new LLMUsageBudgetScope(true, limits);
        var child = parent.CreateChild(limits);
        await child.ReserveJournaledAttemptAsync("first", Request(), null, 10, 20, [], "planning", Ct);
        await child.SettleJournaledAttemptAsync("first", Request(), null, Usage(11, 21), "planning", Ct);
        var recovered = new LLMUsageBudgetScope(true, limits, child.Snapshot);
        await recovered.ReserveJournaledAttemptAsync("first", Request(), null, 10, 20, [], "planning", Ct);
        await recovered.ReserveJournaledAttemptAsync("second", Request(), null, 10, 20, [], "planning", Ct);
        Assert.Equal(62, recovered.Snapshot.TotalTokens);
        Assert.Equal(2, recovered.Snapshot.Calls);
        var calls = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => recovered.ReserveJournaledAttemptAsync(
            "third", Request(), null, 10, 20, [], "planning", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, calls.Code);
        var money = new LLMUsageBudgetScope(true, new() { MaxTotalTokens = 1, MaxEstimatedCostUsd = 1 });
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => money.ReserveJournaledAttemptAsync(
            "cost", Request(), Pricing, 10, 20, [], "planning", Ct));
        Assert.Equal(0, money.Snapshot.Calls);
        var strictParent = new LLMUsageBudgetScope(limits);
        var advisoryChild = new LLMUsageBudgetScope(true, limits, parent: strictParent);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => advisoryChild.ReserveJournaledAttemptAsync(
            "no-parent-authority", Request(), null, 10, 20, [], "planning", Ct));
    }

    [Fact]
    public async Task ReservationAndSettlementAccountEachPhysicalAttemptOnceInEveryCurrency()
    {
        var order = new List<string>();
        var parentSink = new Sink("parent", order); var childSink = new Sink("child", order);
        var parent = new LLMUsageBudgetScope(new() { MaxCalls = 8, MaxTotalTokens = 200, MaxEstimatedCost = new(100, "EUR") },
            sink: parentSink, exchangeRateProvider: new Exchange());
        var child = parent.CreateChild(new() { MaxCalls = 8, MaxTotalTokens = 200, MaxEstimatedCostUsd = 100 }, childSink);
        await child.ReserveJournaledAttemptAsync("request:1", Request(), Pricing, 10, 20, [5], "planning", Ct);
        Assert.Equal(new[] { "parent", "child" }, order);
        Assert.Equal(1, parentSink.Last!.Calls); Assert.Equal(30, childSink.Last!.TotalTokens);
        Assert.Equal(24m, parent.Snapshot.EstimatedCost); Assert.Equal(30m, child.Snapshot.EstimatedCost);
        Assert.Null(child.Snapshot.JournaledAttempts!["request:1"].VerifiedUsage);
        await child.ReserveJournaledAttemptAsync("request:1", Request(), Pricing, 10, 20, [5], "planning", Ct);
        await child.ReserveJournaledAttemptAsync("request:2", Request(), Pricing, 10, 20, [], "planning", Ct);
        var unknown = child.Snapshot.JournaledAttempts!["request:2"];
        await child.SettleJournaledAttemptAsync("request:1", Request(), Pricing, Usage(2, 3), "planning", Ct);
        await child.SettleJournaledAttemptAsync("request:1", Request(), Pricing, Usage(2, 3), "planning", Ct);
        Assert.Equal(2, parent.Snapshot.Calls); Assert.Equal(2, child.Snapshot.Calls);
        Assert.Equal(35, parent.Snapshot.TotalTokens); Assert.Equal(35, child.Snapshot.TotalTokens);
        Assert.Equal(28m, parent.Snapshot.EstimatedCost); Assert.Equal(35m, child.Snapshot.EstimatedCost);
        Assert.Equal(35m, parent.Snapshot.EstimatedCostUsd);
        Assert.Equal(unknown, child.Snapshot.JournaledAttempts!["request:2"]);
        Assert.Null(unknown.VerifiedUsage);
        Assert.Single(parent.Snapshot.ExchangeRates);
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("tokens")]
    [InlineData("cost")]
    public async Task RequiredReviewMustFitBeforeAnyAttemptIsCharged(string limit)
    {
        var limits = limit switch
        {
            "calls" => new LLMUsageBudgetLimits { MaxCalls = 1 },
            "tokens" => new LLMUsageBudgetLimits { MaxTotalTokens = 40 },
            _ => new LLMUsageBudgetLimits { MaxEstimatedCostUsd = 40 }
        };
        var scope = new LLMUsageBudgetScope(limits);
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            scope.ReserveJournaledAttemptAsync("request:1", Request(), Pricing, 10, 20, [1], "planning", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
        Assert.Equal(0, scope.Snapshot.Calls); Assert.Equal(0, scope.Snapshot.TotalTokens);
        Assert.Null(scope.Snapshot.JournaledAttempts);
    }

    [Fact]
    public async Task ConflictingBoundsOrUsageDoNotChangeAccountingAndExactRecoveryIsIdempotent()
    {
        var limits = new LLMUsageBudgetLimits { MaxCalls = 2, MaxTotalTokens = 100 };
        var scope = new LLMUsageBudgetScope(limits);
        await scope.ReserveJournaledAttemptAsync("request:1", Request(), null, 10, 20, [], "planning", Ct);
        var saved = JsonSerializer.Serialize(scope.Snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => scope.ReserveJournaledAttemptAsync(
            "request:1", Request(), null, 11, 20, [], "planning", Ct));
        Assert.Equal(saved, JsonSerializer.Serialize(scope.Snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        var restored = new LLMUsageBudgetScope(limits,
            JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        await restored.ReserveJournaledAttemptAsync("request:1", Request(), null, 10, 20, [], "planning", Ct);
        await restored.SettleJournaledAttemptAsync("request:1", Request(), null, Usage(2, 3), "planning", Ct);
        saved = JsonSerializer.Serialize(restored.Snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => restored.SettleJournaledAttemptAsync(
            "request:1", Request(), null, Usage(3, 3), "planning", Ct));
        Assert.Equal(saved, JsonSerializer.Serialize(restored.Snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        restored = new(limits, JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        await restored.SettleJournaledAttemptAsync("request:1", Request(), null, Usage(2, 3), "planning", Ct);
        Assert.Equal(1, restored.Snapshot.Calls); Assert.Equal(5, restored.Snapshot.TotalTokens);
    }

    [Fact]
    public async Task PersistenceGapRetainsConservativeChargesAndDoesNotReplayOrRefund()
    {
        var order = new List<string>();
        var parentSink = new Sink("parent", order);
        var childSink = new Sink("child", order) { Fail = true };
        var parent = new LLMUsageBudgetScope(new() { MaxCalls = 2, MaxTotalTokens = 100 }, sink: parentSink);
        var child = parent.CreateChild(new() { MaxCalls = 2, MaxTotalTokens = 100 }, childSink);
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => child.ReserveJournaledAttemptAsync(
            "request:1", Request(), null, 10, 20, [], "planning", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetUnverifiable, failure.Code);
        Assert.Equal(30, parentSink.Last!.TotalTokens); Assert.Equal(30, child.Snapshot.TotalTokens);
        childSink.Fail = false;
        await child.ReserveJournaledAttemptAsync("request:1", Request(), null, 10, 20, [], "planning", Ct);
        Assert.Equal(1, childSink.Last!.Calls); Assert.Equal(1, parent.Snapshot.Calls);
        Assert.Null(childSink.Last.JournaledAttempts!["request:1"].VerifiedUsage);
    }

    [Theory]
    [InlineData(11, 3)]
    [InlineData(2, 21)]
    public async Task VerifiedOverageIsDurableInAllScopesAndRepeatedSettlementStillFails(long input, long output)
    {
        var parent = new LLMUsageBudgetScope(new() { MaxTotalTokens = 100 });
        var sink = new Sink("child", []);
        var child = parent.CreateChild(new() { MaxTotalTokens = 100 }, sink);
        await child.ReserveJournaledAttemptAsync("request:1", Request(), null, 10, 20, [], "planning", Ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => child.SettleJournaledAttemptAsync(
                "request:1", Request(), null, Usage(input, output), "planning", Ct));
            Assert.Equal(ErrorCodes.LlmBudgetExceeded, failure.Code);
            Assert.Equal(input + output, parent.Snapshot.TotalTokens);
            Assert.Equal(input + output, sink.Last!.TotalTokens);
            Assert.Equal(1, child.Snapshot.Calls);
            Assert.NotNull(sink.Last.JournaledAttempts!["request:1"].VerifiedUsage);
        }
    }

    [Fact]
    public async Task MissingUsageKeepsReservationAndKnownUsageSurvivesCallerCancellation()
    {
        var scope = new LLMUsageBudgetScope(new() { MaxCalls = 1, MaxTotalTokens = 100 });
        await scope.ReserveJournaledAttemptAsync("request:1", Request(), null, 10, 20, [], "planning", Ct);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => scope.SettleJournaledAttemptAsync(
            "request:1", Request(), null, new JsonObject { ["total_tokens"] = 5 }, "planning", Ct));
        Assert.Equal(30, scope.Snapshot.TotalTokens);
        await scope.SettleJournaledAttemptAsync("request:1", Request(), null, Usage(2, 3), "planning", new CancellationToken(true));
        Assert.Equal(5, scope.Snapshot.TotalTokens);
    }

    [Fact]
    public async Task PartialSettlementPersistenceRecoversEachScopeWithoutRefundingAnotherAttempt()
    {
        var limits = new LLMUsageBudgetLimits { MaxCalls = 4, MaxTotalTokens = 200 };
        var parentSink = new Sink("parent", []); var childSink = new Sink("child", []);
        var parent = new LLMUsageBudgetScope(limits, sink: parentSink);
        var child = parent.CreateChild(limits, childSink);
        await child.ReserveJournaledAttemptAsync("request:1", Request(), null, 10, 20, [], "planning", Ct);
        await child.ReserveJournaledAttemptAsync("request:2", Request(), null, 10, 20, [], "planning", Ct);
        childSink.Fail = true;
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => child.SettleJournaledAttemptAsync(
            "request:1", Request(), null, Usage(2, 3), "planning", Ct));
        Assert.Equal(35, parentSink.Last!.TotalTokens);
        Assert.Equal(60, childSink.Last!.TotalTokens);
        parent = new(limits, parentSink.Last);
        child = new(limits, childSink.Last, parent: parent);
        await child.SettleJournaledAttemptAsync("request:1", Request(), null, Usage(2, 3), "planning", Ct);
        Assert.Equal(35, parent.Snapshot.TotalTokens); Assert.Equal(35, child.Snapshot.TotalTokens);
        Assert.Equal(2, parent.Snapshot.Calls); Assert.Equal(2, child.Snapshot.Calls);
        Assert.Null(child.Snapshot.JournaledAttempts!["request:2"].VerifiedUsage);
    }

    [Fact]
    public async Task MissingPricingCannotCreateOrSettleAMonetaryReservation()
    {
        var scope = new LLMUsageBudgetScope(new() { MaxEstimatedCostUsd = 100 });
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => scope.ReserveJournaledAttemptAsync(
            "request:1", Request(), null, 10, 20, [], "planning", Ct));
        Assert.Equal(0, scope.Snapshot.Calls);
        await scope.ReserveJournaledAttemptAsync("request:1", Request(), Pricing, 10, 20, [], "planning", Ct);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => scope.SettleJournaledAttemptAsync(
            "request:1", Request(), null, Usage(2, 3), "planning", Ct));
        Assert.Equal(30, scope.Snapshot.EstimatedCost);
        Assert.Null(scope.Snapshot.JournaledAttempts!["request:1"].VerifiedUsage);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotExceedParentAndNeverDoubleChargeAnIdentity()
    {
        var parent = new LLMUsageBudgetScope(new() { MaxCalls = 4, MaxTotalTokens = 120 });
        var children = Enumerable.Range(0, 8).Select(_ => parent.CreateChild(new() { MaxCalls = 4 })).ToArray();
        var accepted = await Task.WhenAll(children.Select((child, index) => Task.Run(async () =>
        {
            try { await child.ReserveJournaledAttemptAsync("request:" + index, Request(), null, 10, 20, [], "planning", Ct); return true; }
            catch (WorkflowRuntimeException exception) when (exception.Code == ErrorCodes.LlmBudgetExceeded) { return false; }
        }, Ct)));
        Assert.Equal(4, accepted.Count(value => value));
        Assert.Equal(4, parent.Snapshot.Calls); Assert.Equal(120, parent.Snapshot.TotalTokens);
        Assert.Equal(4, parent.Snapshot.JournaledAttempts!.Count);
        Assert.Equal(4, children.Sum(child => child.Snapshot.Calls));
    }

    [Fact]
    public void HistoricalSerializationOmitsAttemptsAndInvalidRecoveredAccountingFailsClosed()
    {
        var snapshot = new LLMUsageBudgetSnapshot { StartedAtUtc = DateTimeOffset.UtcNow };
        var serialized = JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        Assert.DoesNotContain("journaled", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Null(JsonSerializer.Deserialize(serialized, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!.JournaledAttempts);
        var invalid = snapshot with { Calls = 1, JournaledAttempts = new Dictionary<string, LLMJournaledAttempt>
            { ["request:1"] = new(10, 20, 0, 0) } };
        Assert.Throws<ArgumentException>(() => new LLMUsageBudgetScope(new() { MaxCalls = 2 }, invalid));
    }

    private sealed class CostEstimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null)
            => (inputTokens ?? 0) + (outputTokens ?? 0);
    }

    private sealed class Exchange : IExchangeRateProvider
    {
        public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct)
            => ValueTask.FromResult<CurrencyExchangeQuote?>(new(sourceCurrency, targetCurrency, 0.8m, DateTimeOffset.UtcNow, "test"));
    }

    private sealed class Sink(string name, List<string> order) : ILLMUsageBudgetSink
    {
        internal bool Fail;
        internal LLMUsageBudgetSnapshot? Last;
        public ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); order.Add(name);
            if (Fail) throw new IOException("Expected persistence gap.");
            Last = snapshot; return ValueTask.CompletedTask;
        }
    }
}
