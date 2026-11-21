using System.Text.Json;
using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

public sealed class OpenAiPlanningMetadataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualProviderSerializationPreservesMaximumAllocationAndReasoningOnEachEndpoint(bool responses)
    {
        var sends = 0;
        using var http = new HttpClient(new WireHandler(async (request, ct) =>
        {
            sends++;
            Assert.EndsWith(responses ? "/responses" : "/chat/completions", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(128000, body.RootElement.GetProperty(responses ? "max_output_tokens" : "max_completion_tokens").GetInt32());
            Assert.Equal("max", responses ? body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString() : body.RootElement.GetProperty("reasoning_effort").GetString());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(responses
                ? """{"id":"fixture","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"42"}]}],"usage":{"input_tokens":10,"output_tokens":5}}"""
                : """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"42"}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""") };
        }));
        var options = new LLMOptions { DefaultProvider = "fixture", DefaultModel = "gpt-6.1-sol",
            Models = { ["fixture"] = new() { Type = "openai", Url = "https://example.invalid/v1", RequestPolicy = new()
                { BackgroundProtocol = responses ? LLMBackgroundProtocolMode.Responses : LLMBackgroundProtocolMode.ChatCompletions } } } };
        var client = new RoutingLLMClient(http, options);
        var result = await client.CallAsync(new() { Prompt = "Return 42", UseBackgroundMode = true, Reasoning = "max", MaxOutputTokens = 128000, RequireOutputTokenLimit = true }, TestContext.Current.CancellationToken);
        Assert.Equal("42", result.Text); Assert.Equal(1, sends);
    }
    private sealed class WireHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    [Theory]
    [InlineData("gpt-6-astra", 10, 50, 1, false)]
    [InlineData("gpt-6.1-sol", 2, 10, .1, false)]
    [InlineData("gpt-6.1-terra", 1, 5, .05, true)]
    [InlineData("gpt-6-luna", .1, .5, .01, true)]
    [InlineData("gpt-6-sol", 2, 10, .2, true)]
    public void OfficialMetadataRoundTripsWithoutHeuristicCapabilityChanges(string model, decimal input, decimal output, decimal cache, bool none)
    {
        Assert.True(ModelMetadataCatalog.TryGetBuiltin("openai", model, out var value));
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(value, ModelMetadataJsonContext.Default.LLMModelMetadata), ModelMetadataJsonContext.Default.LLMModelMetadata)!;
        Assert.Equal(1050000, copy.ContextWindowTokens); Assert.Equal(128000, copy.MaxOutputTokens);
        Assert.Equal(input, copy.Pricing!.InputPer1MTokens); Assert.Equal(output, copy.Pricing.OutputPer1MTokens); Assert.Equal(cache, copy.Pricing.CachedInputPer1MTokens);
        Assert.Equal(none, copy.Capabilities.SupportedReasoningEfforts!.Contains("none"));
        Assert.Equal("2026-11-05", copy.VerifiedAt); Assert.Contains(copy.SourceUrls!, url => url.EndsWith("/models/" + model, StringComparison.Ordinal));
        var request = LLMRequestSanitizer.Sanitize(new() { Model = model, Reasoning = "max", MaxOutputTokens = 128000, RequireOutputTokenLimit = true }, copy);
        Assert.Equal(128000, request.MaxOutputTokens); Assert.Equal("max", request.Reasoning);
        Assert.Equal("auto", LLMRequestSanitizer.Sanitize(new() { Reasoning = "auto" }, copy).Reasoning);
        using var wire = JsonDocument.Parse(ChatRequestBuilder.OpenAiFull(model, "Return 42", reasoning: request.Reasoning, maxOutputTokens: request.MaxOutputTokens));
        Assert.Equal("max", wire.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(128000, wire.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Throws<InvalidOperationException>(() => LLMRequestSanitizer.Sanitize(new() { Reasoning = "max", MaxOutputTokens = 128001, RequireOutputTokenLimit = true }, copy));
        Assert.Throws<InvalidOperationException>(() => LLMRequestSanitizer.Sanitize(new() { Reasoning = "high", Tools = [new() { Name = "example" }] }, copy));
        if (none)
        {
            var lowLatency = LLMRequestSanitizer.Sanitize(new() { Reasoning = "none", Temperature = .2 }, copy);
            Assert.Equal(.2, lowLatency.Temperature);
        }
        else Assert.Throws<InvalidOperationException>(() => LLMRequestSanitizer.Sanitize(new() { Reasoning = "none" }, copy));
        var clone = ModelMetadataCatalog.Clone(copy); clone.Capabilities.SupportedReasoningEfforts!.Clear();
        Assert.NotEmpty(copy.Capabilities.SupportedReasoningEfforts);
    }

    [Fact]
    public void ReportedServiceTierSurvivesProviderUsageExtraction()
    {
        using var document = JsonDocument.Parse("""{"service_tier":"priority","usage":{"input_tokens":10,"output_tokens":5}}""");
        Assert.Equal("priority", ChatResponseParser.ExtractUsage(document.RootElement)!["service_tier"]!.ToString());
    }

    [Fact]
    public void LongContextCacheAndTierPricingUseTheWholeRequestWithoutAddingReasoningAgain()
    {
        Assert.True(ModelMetadataCatalog.TryGetBuiltin("openai", "gpt-6.1-sol", out var model));
        var price = model.Pricing!;
        Assert.Equal(.554m, ModelMetadataCatalog.EstimateUsageCost(price, 272000, 1000));
        Assert.Equal(1.103004m, ModelMetadataCatalog.EstimateUsageCost(price, 272001, 1000));
        var cached = ModelMetadataCatalog.EstimateUsageCost(price, 1000, 100, cachedInput: 500);
        Assert.Equal(.00205m, cached);
        Assert.Equal(cached * 2m * 1.1m, ModelMetadataCatalog.EstimateUsageCost(price, 1000, 100, 500, "fast", true));
        Assert.Equal(.00225m, ModelMetadataCatalog.EstimateUsageCost(price, 1000, 0, cacheWriteInput: 500));
        Assert.Equal(.0035m, ModelMetadataCatalog.EstimateUsageCost(price, 1000, 0, cacheWriteInput: 500, cacheWrite12h: true));
    }
}
