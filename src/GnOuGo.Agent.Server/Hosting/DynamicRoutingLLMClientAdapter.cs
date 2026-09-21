using GnOuGo.Agent.Server.Telemetry;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using Microsoft.Extensions.Logging;

namespace GnOuGo.Agent.Server.Hosting;

/// <summary>
/// An <see cref="ILLMClient"/> that resolves the latest <see cref="LLMOptions"/> from
/// <see cref="LLMRuntimeOptionsStore"/> on every call, so runtime config updates
/// (from the /llm wizard) take effect immediately without restarting the server.
/// </summary>
internal sealed class DynamicRoutingLLMClientAdapter : ILLMClient
{
    private readonly HttpClient _http;
    private readonly LLMRuntimeOptionsStore _store;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILocalLLMRuntime? _localRuntime;
    private readonly LlmTraceCapture? _capture;

    public DynamicRoutingLLMClientAdapter(
        HttpClient http,
        LLMRuntimeOptionsStore store,
        ILoggerFactory loggerFactory,
        ILocalLLMRuntime? localRuntime = null,
        LlmTraceCapture? capture = null)
    {
        _http = http;
        _store = store;
        _loggerFactory = loggerFactory;
        _localRuntime = localRuntime;
        _capture = capture;
    }

    public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        var options = _store.Current;
        return _capture is null ? DispatchAsync(request, options, ct)
            : _capture.CallAsync(request, options, (value, token) => DispatchAsync(value, options, token), ct);
    }

    private async Task<LLMResponse> DispatchAsync(LLMRequest request, LLMOptions options, CancellationToken ct)
    {
        // Always read the LATEST options — picks up any /llm wizard changes.
        var providers = RoutingLLMClient.CreateDefaultProviders(_http, _loggerFactory).AsEnumerable();
        if (_localRuntime is not null)
            providers = providers.Append(new LocalLLMProvider(_localRuntime));
        var routingClient = new RoutingLLMClient(options, providers);

        var aiRequest = RoutingLLMClientAdapter.MapRequest(request);

        LLMClientResponse aiResponse;
        try
        {
            aiResponse = await routingClient.CallAsync(aiRequest, ct);
        }
        catch (LLMProviderException ex)
        {
            throw LLMProviderFailureMapper.Map(ex);
        }

        return RoutingLLMClientAdapter.MapResponse(aiResponse);
    }
}
