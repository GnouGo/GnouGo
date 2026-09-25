using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.ProxyCopilot.Server.Configuration;
using GnOuGo.ProxyCopilot.Server.Protocols;
using GnOuGo.ProxyCopilot.Server.Traffic;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class PricingTests
{
    private static ModelPricingMetadata Rates(string currency = "EUR", decimal input = 2, decimal output = 10) => new()
    { Currency = currency, InputPer1MTokens = input, OutputPer1MTokens = output };
    private static JsonObject Usage(string json) => JsonNode.Parse(json)!.AsObject();
    private static (TrafficStore Store, ModelRoute Route) Fixture()
    {
        var options = ConfigurationAndTrafficTests.Options();
        var route = new ModelRegistry(options).Models[0];
        route.Model.Metadata.Pricing = Rates();
        return (new(options, new(options)), route);
    }

    [Fact]
    public void SubsetsArePricedExactlyOnceWithOptionalRatesFallingBackToBaseRates()
    {
        var (_, route) = Fixture();
        var usage = Usage("""{"prompt_tokens":1000,"completion_tokens":100,"prompt_tokens_details":{"cached_tokens":300,"cache_write_tokens":200},"completion_tokens_details":{"reasoning_tokens":40}}""");
        var fallback = TrafficCost.Estimate(route.Model, usage, "completed");
        Assert.Equal(0.003m, fallback.Amount);
        route.Model.Metadata.Pricing!.CachedInputPer1MTokens = .2m;
        route.Model.Metadata.Pricing.CacheWriteInputPer1MTokens = 2.5m;
        route.Model.Metadata.Pricing.ReasoningOutputPer1MTokens = 15m;
        var result = TrafficCost.Estimate(route.Model, usage, "completed");
        Assert.Equal("estimated", result.Status);
        Assert.Equal("EUR", result.Currency);
        Assert.Equal(new CostBreakdown(.001m, .00006m, .0005m, .0006m, .0006m), result.Breakdown);
        Assert.Equal(.00276m, result.Amount);
    }

    [Theory]
    [InlineData(272000, null, 2)]
    [InlineData(272001, 272000L, 4)]
    [InlineData(500000, 272000L, 4)]
    [InlineData(500001, 500000L, 8)]
    public void HighestExclusiveThresholdPricesTheWholeRequest(long input, long? threshold, int rate)
    {
        var (_, route) = Fixture();
        route.Model.PricingTiers = [new() { InputTokensAbove = 500000, Pricing = Rates(input: 8, output: 30) },
            new() { InputTokensAbove = 272000, Pricing = Rates(input: 4, output: 20) }];
        var result = TrafficCost.Estimate(route.Model, ChatContract.Usage(input, 100), "completed");
        Assert.Equal(threshold, result.InputTokensAbove);
        Assert.Equal(input / 1_000_000m * rate, result.Breakdown!.Input);
        Assert.Equal(threshold is null ? .001m : threshold == 272000 ? .002m : .003m, result.Breakdown.Output);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"prompt_tokens\":1}")]
    [InlineData("{\"prompt_tokens\":-1,\"completion_tokens\":1}")]
    [InlineData("{\"prompt_tokens\":1,\"completion_tokens\":2,\"prompt_tokens_details\":{\"cached_tokens\":2}}")]
    [InlineData("{\"prompt_tokens\":1,\"completion_tokens\":2,\"completion_tokens_details\":{\"reasoning_tokens\":3}}")]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_tokens_details\":{\"cached_tokens\":6,\"cache_write_tokens\":5}}")]
    [InlineData("{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_tokens_details\":{\"cached_tokens\":\"secret\"}}")]
    public void IncompleteOrInvalidUsageIsUnknownAndCannotLeakStrings(string json)
    {
        var (store, route) = Fixture();
        var id = store.Start(route, []);
        store.Progress(id, new() { ["usage"] = Usage(json) });
        var summary = store.Detail(id)!.Summary;
        Assert.Equal("unknown", summary.Cost.Status);
        Assert.Null(summary.Cost.Amount);
        Assert.DoesNotContain("secret", summary.Usage!.ToJsonString());
    }

    [Fact]
    public void ExplicitZeroIsFreeButMissingPricesOrUsageAreUnknown()
    {
        var (_, route) = Fixture();
        Assert.Null(TrafficCost.Estimate(route.Model, null, "completed").Amount);
        route.Model.Metadata.Pricing = null;
        Assert.Null(TrafficCost.Estimate(route.Model, ChatContract.Usage(10, 10), "completed").Amount);
        route.Model.Metadata.Pricing = new() { Currency = "EUR", InputPer1MTokens = 0 };
        Assert.Null(TrafficCost.Estimate(route.Model, ChatContract.Usage(10, 10), "completed").Amount);
        route.Model.Metadata.Pricing.OutputPer1MTokens = 0;
        Assert.Equal(0m, TrafficCost.Estimate(route.Model, ChatContract.Usage(10, 10), "completed").Amount);
    }

    [Fact]
    public async Task CumulativeUsageTotalsCurrencyIsolationPartialCallsAndRetentionStayConsistent()
    {
        var options = ConfigurationAndTrafficTests.Options(); options.Capture.MaxCalls = 3;
        var route = new ModelRegistry(options).Models[0]; route.Model.Metadata.Pricing = Rates();
        var store = new TrafficStore(options, new(options));
        using var viewer = store.Subscribe();
        var eur = store.Start(route, []);
        store.Progress(eur, new() { ["usage"] = ChatContract.Usage(1000, 100) });
        store.Progress(eur, new() { ["usage"] = ChatContract.Usage(1000, 200) });
        store.Complete(eur, "completed", 200);
        var usdRoute = route with { Id = "test/usd", Model = new() { Metadata = new() { Pricing = Rates("USD") } } };
        var usd = store.Start(usdRoute, []);
        store.Progress(usd, new() { ["usage"] = ChatContract.Usage(1000, 100) });
        store.Complete(usd, "cancelled", 499);
        store.Start(route, []);
        var snapshot = store.Snapshot();
        Assert.Equal(new TrafficCostTotal("EUR", .004m, 1, 0), snapshot.CostTotals[0]);
        Assert.Equal(new TrafficCostTotal("USD", .003m, 1, 1), snapshot.CostTotals[1]);
        Assert.Equal(1, snapshot.UnknownCostCalls);
        Assert.Equal(snapshot.Version, await viewer.Reader.ReadAsync(TestContext.Current.CancellationToken));
        using var reconnected = store.Subscribe();
        Assert.Equal(snapshot.Version, await reconnected.Reader.ReadAsync(TestContext.Current.CancellationToken));
        store.Start(route, []);
        Assert.Equal("USD", Assert.Single(store.Snapshot().CostTotals).Currency);
        store.Clear(); Assert.Empty(store.Snapshot().CostTotals); Assert.Equal(0, store.Snapshot().UnknownCostCalls);
    }

    [Theory]
    [InlineData("negative")][InlineData("currency")][InlineData("duplicate")][InlineData("tierCurrency")]
    [InlineData("negativeThreshold")][InlineData("missingBase")][InlineData("missingTier")]
    public void InvalidPricingFailsAtStartup(string problem)
    {
        var options = ConfigurationAndTrafficTests.Options(); var model = options.Providers["test"].Models["model"];
        model.Metadata.Pricing = Rates(); model.PricingTiers = [new() { InputTokensAbove = 100, Pricing = Rates() }];
        switch (problem)
        {
            case "negative": model.Metadata.Pricing.CacheWriteInputPer1MTokens = -1; break;
            case "currency": model.Metadata.Pricing.Currency = "not-a-currency"; break;
            case "duplicate": model.PricingTiers.Add(model.PricingTiers[0]); break;
            case "tierCurrency": model.PricingTiers[0].Pricing!.Currency = "USD"; break;
            case "negativeThreshold": model.PricingTiers[0].InputTokensAbove = -1; break;
            case "missingBase": model.Metadata.Pricing = null; break;
            case "missingTier": model.PricingTiers[0].Pricing = null; break;
        }
        Assert.Throws<InvalidOperationException>(() => new ModelRegistry(options));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AnthropicCacheCountsSurviveTranslationAndCumulativeDelta(bool streaming)
    {
        const string initial = "{\"input_tokens\":500,\"cache_read_input_tokens\":300,\"cache_creation_input_tokens\":200,\"output_tokens\":0}";
        var body = streaming
            ? "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"usage\":" + initial + "}}\n\n"
                + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":100}}\n\n"
                + "data: {\"type\":\"message_stop\"}\n\n"
            : "{\"id\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"OK\"}],\"stop_reason\":\"end_turn\",\"usage\":" + initial.Replace("\"output_tokens\":0", "\"output_tokens\":100") + "}";
        var (store, route) = Fixture(); var id = store.Start(route, []);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await foreach (var chunk in new AnthropicAdapter().ReadResponse(stream, streaming, true, route, TestContext.Current.CancellationToken)) store.Progress(id, chunk);
        store.Complete(id, "completed", 200);
        var call = store.Detail(id)!.Summary;
        Assert.Equal(1000, call.Usage!["prompt_tokens"]!.GetValue<long>());
        Assert.Equal(300, call.Usage["prompt_tokens_details"]!["cached_tokens"]!.GetValue<long>());
        Assert.Equal(200, call.Usage["prompt_tokens_details"]!["cache_write_tokens"]!.GetValue<long>());
        Assert.Equal(.003m, call.Cost.Amount);
    }

    [Theory]
    [InlineData("anthropic")] [InlineData("ollama")]
    public async Task NativeResponsesWithoutReportedUsageAreUnknown(string provider)
    {
        var (store, route) = Fixture(); var id = store.Start(route, []);
        IProxyAdapter adapter = provider == "anthropic" ? new AnthropicAdapter() : new OllamaAdapter();
        var body = provider == "anthropic"
            ? """{"id":"m","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn"}"""
            : """{"message":{"role":"assistant","content":"OK"},"done":true}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await foreach (var chunk in adapter.ReadResponse(stream, false, true, route, TestContext.Current.CancellationToken)) store.Progress(id, chunk);
        store.Complete(id, "completed", 200);
        Assert.Equal("unknown", store.Detail(id)!.Summary.Cost.Status);
    }

    [Fact]
    public async Task InterruptedAnthropicStreamRetainsPartialReportedUsage()
    {
        var (store, route) = Fixture(); var id = store.Start(route, []);
        const string body = "data: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":0}}}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        await Assert.ThrowsAsync<ProxyException>(async () => {
            await foreach (var chunk in new AnthropicAdapter().ReadResponse(stream, true, true, route, TestContext.Current.CancellationToken)) store.Progress(id, chunk);
        });
        store.Complete(id, "failed", 502);
        Assert.Equal("partial", store.Detail(id)!.Summary.Cost.Status);
        Assert.Equal(.002m, store.Detail(id)!.Summary.Cost.Amount);
    }
}
