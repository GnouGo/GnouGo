using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Runtime;
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

    public DynamicRoutingLLMClientAdapter(HttpClient http, LLMRuntimeOptionsStore store, ILoggerFactory loggerFactory)
    {
        _http = http;
        _store = store;
        _loggerFactory = loggerFactory;
    }

    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        // Always read the LATEST options — picks up any /llm wizard changes.
        var options = _store.Current;
        var routingClient = new RoutingLLMClient(_http, options, _loggerFactory);

        var aiRequest = new LLMClientRequest
        {
            Provider = request.Provider,
            Model = request.Model,
            Prompt = request.Prompt,
            Temperature = request.Temperature,
            StructuredOutputSchema = request.StructuredOutputSchema,
            StructuredOutputStrict = request.StructuredOutputStrict,
            Reasoning = request.Reasoning,
            UseBackgroundMode = request.UseBackgroundMode,
        };

        if (request.Tools is { Count: > 0 })
        {
            aiRequest.Tools = request.Tools.Select(t => new LLMToolDef
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.InputSchema?.DeepClone()
            }).ToList();
        }

        var aiResponse = await routingClient.CallAsync(aiRequest, ct);

        var response = new LLMResponse
        {
            Text = aiResponse.Text,
            Json = aiResponse.Json,
            Usage = aiResponse.Usage,
            Raw = aiResponse.Raw,
        };

        if (aiResponse.ToolCalls is { Count: > 0 })
        {
            response.ToolCalls = aiResponse.ToolCalls.Select(tc => new LLMToolCall
            {
                Id = tc.Id,
                Name = tc.Name,
                Arguments = tc.Arguments
            }).ToList();
        }

        return response;
    }
}
