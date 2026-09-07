using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class PlanningTransportBoundaryTests
{
    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(false, 400)]
    [InlineData(true, 400)]
    [InlineData(false, 500)]
    [InlineData(true, 500)]
    public async Task HostAdaptersPreserveOutputLimitsCompletionStatusAndDisabledRetries(bool dynamic, int status)
    {
        var handler = new RecordingHandler(status); using var http = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new LLMOptions { DefaultProvider = "fixture", DefaultModel = "renamed-model", Models =
        { ["fixture"] = new() { Type = "openai", Url = "https://example.invalid/v1/chat/completions", RetryPolicy = new() { MaxAttempts = 3 } } } };
        ILLMClient adapter = dynamic
            ? new DynamicRoutingLLMClientAdapter(http, new LLMRuntimeOptionsStore(Options.Create(options), NullLogger<LLMRuntimeOptionsStore>.Instance), NullLoggerFactory.Instance, cache)
            : new SnapshotRoutingLlmClientAdapter(http, options, NullLoggerFactory.Instance, cache);
        var request = new LLMRequest { Model = "renamed-model", Prompt = "Return a plan", Reasoning = "low", MaxTokens = 8192, RequireOutputTokenLimit = true, DisableTransportRetries = true };
        if (status == 200)
        {
            var response = await adapter.CallAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal("output_limit", response.CompletionStatus); Assert.Equal("", response.Text);
        }
        else await Assert.ThrowsAsync<LLMClientException>(() => adapter.CallAsync(request, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(8192, handler.Payload!["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("low", handler.Payload["reasoning_effort"]!.GetValue<string>());
    }

    private sealed class RecordingHandler(int status) : HttpMessageHandler
    {
        public int Calls;
        public JsonObject? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            return new((HttpStatusCode)status) { Content = new StringContent(status == 200
                ? """{"choices":[{"finish_reason":"length","message":{"role":"assistant","content":""}}],"usage":{"prompt_tokens":12,"completion_tokens":8192,"total_tokens":8204}}"""
                : """{"error":{"message":"Unsupported parameter: max_completion_tokens","type":"invalid_request_error","param":"max_completion_tokens","code":"unsupported_parameter"}}""") };
        }
    }
}
