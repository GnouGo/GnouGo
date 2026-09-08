using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Agent.Server.Tests.LiveIntentAgentGenerationTests;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LiveInferenceGatewayTests
{
    [Fact]
    public async Task PolicyHostStartsAcceptsOnlyTheHandshakeAndRestoresItsTemporaryEndpoint()
    {
        const string key = "Code__Copilot__InferenceProxyEndpoint";
        var previous = Environment.GetEnvironmentVariable(key);
        var path = Path.Combine(Path.GetTempPath(), "inference-start-" + Guid.NewGuid().ToString("N") + ".json");
        var ledger = LiveBudgetLedger.Open(path, new(new(20, "EUR"), new(0, "EUR"), ExistingConfiguration: true));
        var budget = new LLMUsageBudgetScope(new() { MaxCalls = 2 }, ledger.Snapshot, sink: ledger);
        try
        {
            await using (var gateway = new LiveInferenceGateway(budget, ledger, new NoRates(), _ => throw new InvalidOperationException("Startup requires no provider request."), new HttpClient()))
            {
                await gateway.StartHostAsync(TestContext.Current.CancellationToken);
                var endpoint = Environment.GetEnvironmentVariable(key); Assert.NotNull(endpoint); Assert.NotEqual(previous, endpoint);
                using var client = new HttpClient();
                using var invalid = await client.PostAsync(endpoint + "/ready", new StringContent("invalid"), TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Throws<InvalidOperationException>(gateway.RequireReady);
                using var accepted = await client.PostAsync(endpoint + "/ready", new StringContent("sdk-http-interception-v1"), TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode); gateway.RequireReady(); Assert.Equal(0, budget.Snapshot.Calls);
            }
            Assert.Equal(previous, Environment.GetEnvironmentVariable(key));
        }
        finally { Environment.SetEnvironmentVariable(key, previous); if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(false, 8192)]
    [InlineData(true, 8192)]
    [InlineData(false, 1024)]
    public async Task EveryDispatchReservesBeforeSendingAndPreservesUnverifiedUsageAcrossRestart(bool missingUsage, int ceiling)
    {
        var path = Path.Combine(Path.GetTempPath(), "inference-ledger-" + Guid.NewGuid().ToString("N") + ".json");
        var definition = new LiveBudgetDefinition(new(20, "EUR"), new(0, "EUR"), ExistingConfiguration: true);
        var ledger = LiveBudgetLedger.Open(path, definition);
        var budget = new LLMUsageBudgetScope(new() { MaxCalls = 2, MaxTotalTokens = 100_000, MaxEstimatedCost = new(20, "EUR") }, ledger.Snapshot, sink: ledger);
        var options = new LLMOptions { DefaultProvider = "configured", DefaultModel = "test-model",
            Models = { ["configured"] = new() { Url = "https://provider.example/v1", Type = "openai" } },
            ModelOverrides = { ["test-model"] = new() { Id = "test-model", MaxInputTokens = 100_000, MaxOutputTokens = 8192,
                Pricing = new() { Currency = "EUR", InputPer1MTokens = 1, OutputPer1MTokens = 2 } } } };
        var calls = 0;
        var client = new HttpClient(new Handler(async request =>
        {
            calls++; Assert.True(ledger.UnverifiedCostReserve > 0); Assert.Equal(1, budget.Snapshot.Calls);
            var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal(ceiling, payload["max_completion_tokens"]!.GetValue<int>());
            Assert.Equal("low", payload["reasoning_effort"]!.GetValue<string>());
            Assert.Null(payload["max_tokens"]); Assert.True(payload["stream_options"]!["include_usage"]!.GetValue<bool>());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(missingUsage ? "{}" : """{"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""") };
        }));
        try
        {
            await using var gateway = new LiveInferenceGateway(budget, ledger, new NoRates(), _ => Task.FromResult(options), client);
            Assert.Throws<InvalidOperationException>(gateway.RequireReady);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.example/v1/chat/completions")
                { Content = new StringContent(new JsonObject { ["model"] = "test-model", ["stream"] = true, ["max_tokens"] = ceiling == 8192 ? 20000 : ceiling, ["messages"] = new JsonArray() }.ToJsonString()) };
            if (missingUsage) await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ForwardAsync(request, "request", TestContext.Current.CancellationToken));
            else
            {
                using var response = await gateway.ForwardAsync(request, "request", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(15, budget.Snapshot.TotalTokens);
                Assert.Equal(0, ledger.UnverifiedCostReserve); Assert.Equal(1, gateway.CompletedCalls);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ForwardAsync(request, "request", TestContext.Current.CancellationToken));
            Assert.Equal(1, calls);
            using var outside = new HttpRequestMessage(HttpMethod.Post, "https://other.example/v1/responses") { Content = new StringContent("{}") };
            await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ForwardAsync(outside, "outside", TestContext.Current.CancellationToken));
            Assert.Equal(1, calls);
            var reopened = LiveBudgetLedger.Open(path, definition);
            Assert.Equal(1, reopened.Snapshot.Calls);
            Assert.Equal(missingUsage, reopened.UnverifiedCostReserve > 0);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void StreamingReceiptIsCountedOnceAndConflictsAreRejected()
    {
        const string entry = "data: {\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}\n";
        Assert.Equal(15, LiveInferenceGateway.Receipt(entry + entry + "data: [DONE]\n", true)!["total_tokens"]!.GetValue<long>());
        Assert.Throws<InvalidOperationException>(() => LiveInferenceGateway.Receipt(entry + entry.Replace("12", "13", StringComparison.Ordinal), true));
        Assert.Throws<InvalidOperationException>(() => LiveInferenceGateway.Receipt("""{"usage":{"input_tokens":10,"output_tokens":8193}}""", false));
        Assert.Null(LiveInferenceGateway.Receipt("data: {\"choices\":[]}\ndata: [DONE]", true));
        Assert.Equal(15, LiveInferenceGateway.Receipt("""data: {"type":"response.completed","response":{"usage":{"input_tokens":12,"output_tokens":3}}}""", true)!["total_tokens"]!.GetValue<long>());
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
    private sealed class NoRates : IExchangeRateProvider
    { public ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(string sourceCurrency, string targetCurrency, CancellationToken ct) => throw new InvalidOperationException("The test uses EUR throughout."); }
}
