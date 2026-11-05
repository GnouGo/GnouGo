using System.Text.Json.Nodes;
using Xunit;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class PlanningModelCostTests
{
    [Fact]
    public async Task SettlementUsesReportedCacheAndCountsReasoningOnlyInsideOutput()
    {
        var options = new LLMOptions { DefaultProvider = "gateway", DefaultModel = "gpt-6.1-sol",
            Models = { ["gateway"] = new() { Type = "openai", Url = "https://example.invalid/v1" } } };
        var estimator = new ModelMetadataUsageCostEstimator(options);
        var usage = new JsonObject { ["input_tokens"] = 1000, ["output_tokens"] = 100, ["total_tokens"] = 1100,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 500 },
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 99 } };
        var money = estimator.EstimateReportedCostWithCurrency("gpt-6.1-sol", 1000, 100, usage, "gateway");
        Assert.Equal(.00205m, money!.Amount);
        var budget = new LLMUsageBudgetScope(new() { MaxEstimatedCostUsd = 100 });
        var request = new LLMRequest { Provider = "gateway", Model = "gpt-6.1-sol" };
        await budget.ReserveJournaledAttemptAsync("owned-attempt", request, estimator, 1000, 100, [], "test", TestContext.Current.CancellationToken);
        Assert.True(budget.Snapshot.EstimatedCost > money.Amount);
        await budget.SettleJournaledAttemptAsync("owned-attempt", request, estimator, usage, "test", TestContext.Current.CancellationToken);
        Assert.Equal(1100, budget.Snapshot.TotalTokens); Assert.Equal(100, budget.Snapshot.OutputTokens);
        Assert.Equal(.00205m, budget.Snapshot.EstimatedCost);
    }
}
