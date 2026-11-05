using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class TrackingOnlyBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly LLMRequest Request = new() { Model = "private-sol-deployment", MaxTokens = 128000 };
    [Theory]
    [InlineData("limited")]
    [InlineData("unlimited")]
    [InlineData(null)]
    public void IncompletePersistedPolicyCannotBeReadAsAFreshAllowanceOrLegacyRun(string? mode)
    {
        var run = new WorkflowRun { TenantId = "tenant", RunId = "run", Limits = new() { TenantId = "tenant", RunId = "run" },
            ApplicationLlmBudgetMode = mode, ApplicationLlmBudgetVersion = 1, ApplicationLlmBudgetOrigin = "operator_preference",
            ModelBudget = mode == "limited" ? new() { MaxCalls = 1 } : null };
        // Missing accounting must not become a new zero-usage scope on restart.
        var saved = JsonSerializer.Serialize(run, WorkflowRunJsonContext.Default.WorkflowRun);
        Assert.Throws<WorkflowRunConflictException>(() => WorkflowRunStorage.Read(saved, "tenant", "run"));
        Assert.Null(run.ModelUsage);
    }
    [Fact]
    public async Task HighTariffReviewHeadroomBlocksLimitedButTrackingRetainsIdenticalAllocations()
    {
        var limited = new LLMUsageBudgetScope(new() { MaxEstimatedCost = new(50, "EUR") }, exchangeRateProvider: new Exchange());
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => limited.EnsureAllowanceAsync(Request, new Prices(),
            16000, [128000, 128000, 128000], "planning", Ct));
        Assert.Equal(ErrorCodes.LlmBudgetExceeded, error.Code);
        Assert.Equal("not_started", error.Details!["dispatch_status"]!.ToString());
        Assert.Equal(0, limited.Snapshot.Calls);
        var sink = new Sink();
        var unlimited = LLMUsageBudgetScope.CreateTrackingOnly(sink: sink, exchangeRateProvider: new Exchange(), currency: "EUR");
        await unlimited.EnsureAllowanceAsync(Request, new Prices(), 16000, [128000, 128000, 128000], "planning", Ct);
        await unlimited.ReserveJournaledAttemptAsync("request-1", Request, new Prices(), 16000, 128000, [128000, 128000], "planning", Ct);
        Assert.True(unlimited.IsTrackingOnly); Assert.Null(unlimited.Limits.MaxCalls); Assert.Null(unlimited.Limits.MaxTotalTokens);
        Assert.Null(unlimited.Limits.MaxElapsed); Assert.Null(unlimited.Limits.MaxEstimatedCost);
        Assert.True(sink.Saved!.EstimatedCost > 0); Assert.True(sink.Saved.CostEstimateAvailable);
        Assert.Null(sink.Saved.JournaledAttempts!["request-1"].VerifiedUsage);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(sink.Saved, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        unlimited = LLMUsageBudgetScope.CreateTrackingOnly(restored, sink: sink, currency: "EUR");
        await unlimited.ReserveJournaledAttemptAsync("request-1", Request, new Prices(), 16000, 128000, [], "planning", Ct);
        Assert.Equal(restored.EstimatedCost, unlimited.Snapshot.EstimatedCost); Assert.Equal(1, unlimited.Snapshot.Calls);
        await unlimited.SettleJournaledAttemptAsync("request-1", Request, new Prices(), new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }, "planning", Ct);
        Assert.Equal(15, sink.Saved!.TotalTokens); Assert.NotNull(sink.Saved.JournaledAttempts!["request-1"].VerifiedUsage);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingPricesOrExchangeNeverBlockTrackingAndNeverBecomeFree(bool prices)
    {
        var scope = LLMUsageBudgetScope.CreateTrackingOnly(currency: "EUR");
        await scope.ReserveJournaledAttemptAsync("uncertain", Request, prices ? new Prices() : null, 16000, 128000, [], "planning", Ct);
        Assert.Equal(1, scope.Snapshot.Calls); Assert.False(scope.Snapshot.CostEstimateAvailable);
        Assert.Equal(prices ? "exchange_rate_unavailable" : "pricing_unavailable", scope.Snapshot.CostUnavailableReason);
        Assert.NotNull(scope.Snapshot.JournaledAttempts!["uncertain"].CostUnavailableReason);
        Assert.Null(scope.Snapshot.JournaledAttempts["uncertain"].VerifiedUsage);
    }
    [Fact]
    public async Task ExplicitFiniteChildrenAndCancellationRemainMandatory()
    {
        Assert.Throws<ArgumentException>(() => new LLMUsageBudgetLimits().Validate());
        var parent = LLMUsageBudgetScope.CreateTrackingOnly();
        var child = parent.CreateChild(new() { MaxCalls = 1 });
        await child.ReserveJournaledAttemptAsync("one", Request, null, 10, 20, [], "execution", Ct);
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => child.ReserveJournaledAttemptAsync("two", Request, null, 10, 20, [], "execution", Ct));
        Assert.Equal(1, parent.Snapshot.Calls);
        using var cancelled = new CancellationTokenSource(); await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parent.ReserveJournaledAttemptAsync("cancelled", Request, null, 10, 20, [], "execution", cancelled.Token));
        Assert.Equal(1, parent.Snapshot.Calls);
    }
    private sealed class Sink : ILLMUsageBudgetSink
    { public LLMUsageBudgetSnapshot? Saved; public ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct) { Saved = snapshot; return ValueTask.CompletedTask; } }
    private sealed class Prices : IModelUsageCostEstimator
    { public decimal? EstimateCost(string? model, long? inputTokens, long? outputTokens, string? provider = null) => (inputTokens * 30m + outputTokens * 180m) / 1_000_000m; }
    private sealed class Exchange : IExchangeRateProvider
    { public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct) => ValueTask.FromResult<CurrencyExchangeQuote?>(new(sourceCurrency, targetCurrency, .9m, DateTimeOffset.UtcNow.AddMinutes(-1), "controlled")); }
}
