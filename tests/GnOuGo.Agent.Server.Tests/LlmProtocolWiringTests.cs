using System.Net;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.Agent.Server.Tests;

public sealed class LlmProtocolWiringTests
{
    [Theory]
    [InlineData(LLMBackgroundProtocolMode.Auto, "/v1/responses")]
    [InlineData(LLMBackgroundProtocolMode.Responses, "/v1/responses")]
    [InlineData(LLMBackgroundProtocolMode.ChatCompletions, "/v1/chat/completions")]
    public async Task RuntimeSelectionDispatchesCorrectProtocolAndPreservesOtherSettings(LLMBackgroundProtocolMode protocol, string path)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var options = new LLMOptions { DefaultProvider = "openai", DefaultModel = "gpt-4o", Models = new() { ["openai"] = new()
        {
            Type = "openai", Url = "https://provider.example/v1", ApiKey = "test-secret", ApiVersion = "test-version",
            RequestPolicy = new() { MaxOutputTokensCap = 1024 }, RetryPolicy = new() { MaxAttempts = 3 }
        } } };
        var store = SmartFlowTestFactory.CreateRuntimeOptionsStore(options);
        store.UpdateProvider("openai", "https://provider.example/v1", "gpt-4o", null, backgroundProtocol: protocol);
        Assert.Equal("test-secret", store.Current.Models["openai"].ApiKey);
        Assert.Equal("test-version", store.Current.Models["openai"].ApiVersion);
        Assert.Equal(1024, store.Current.Models["openai"].RequestPolicy.MaxOutputTokensCap);
        Assert.Equal(3, store.Current.Models["openai"].RetryPolicy.MaxAttempts);
        var client = new DynamicRoutingLLMClientAdapter(http, store, NullLoggerFactory.Instance);
        await client.CallAsync(new() { Prompt = "test", UseBackgroundMode = true, MaxTokens = 64 }, TestContext.Current.CancellationToken);
        Assert.Equal([path], handler.Paths);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.UpdateProvider("openai", "https://provider.example/v1", "gpt-4o", null, backgroundProtocol: (LLMBackgroundProtocolMode)999));
        Assert.Equal(protocol, store.Current.Models["openai"].RequestPolicy.BackgroundProtocol);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                request.RequestUri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal)
                    ? """{"id":"response","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"ok"}]}]}"""
                    : """{"choices":[{"message":{"content":"ok"}}]}""") });
        }
    }
}
