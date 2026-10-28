using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;

namespace GnOuGo.Agent.Server.Tests;

/// <summary>Current scripted responses delivered through the real HTTP journal and provider parser.</summary>
internal sealed class JournaledPlanningModel(ILLMClient script) : ILLMClient, IDisposable
{
    private readonly Handler _handler = new();
    private HttpClient? _http;
    internal List<LLMRequest> Requests { get; } = [];
    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        _handler.Response = await script.CallAsync(request, ct);
        _http ??= new HttpClient(_handler);
        var provider = request.Provider ?? "OpenAi";
        var client = new RoutingLLMClientAdapter(new RoutingLLMClient(_http, new LLMOptions
        {
            DefaultProvider = provider, DefaultModel = request.Model,
            Models = { [provider] = new() { Type = "openai", Url = "https://example.invalid/v1/chat/completions" } }
        }));
        return await client.CallAsync(request, ct);
    }
    public void Dispose() => _http?.Dispose();
    private sealed class Handler : HttpMessageHandler
    {
        internal LLMResponse? Response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = Response ?? throw new InvalidOperationException("No scripted response was prepared.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["finish_reason"] = "stop", ["message"] = new JsonObject
                    { ["role"] = "assistant", ["content"] = response.Json?.ToJsonString() ?? response.Text } }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 }
            }.ToJsonString(), System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
