using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Integrations;

/// <summary>
/// Real LLM client that routes requests to OpenAI or Ollama using <see cref="RoutingLLMClient"/>.
/// Adapts from <see cref="ILLMClient"/> (GnOuGo.Flow) to <see cref="RoutingLLMClient"/> (GnOuGo.AI.Core).
/// </summary>
public sealed class RoutingLLMClientAdapter : ILLMClient, ILLMCapabilityResolver
{
    private readonly RoutingLLMClient _inner;

    public RoutingLLMClientAdapter(RoutingLLMClient inner)
    {
        _inner = inner;
    }

    public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_inner.ResolveDeclaredCapabilities(provider, model)?.SupportsStructuredOutput);
    }

    public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var capability = _inner.ResolveDeclaredCapabilities(provider, model);
        return Task.FromResult<IReadOnlyList<string>?>(capability?.SupportsReasoningEffort switch
        {
            true => capability.SupportedReasoningEfforts?.ToArray(),
            false => [],
            _ => null
        });
    }

    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        var aiRequest = MapRequest(request);

        LLMClientResponse aiResponse;
        try
        {
            aiResponse = await _inner.CallAsync(aiRequest, ct);
        }
        catch (LLMProviderException ex)
        {
            throw LLMProviderFailureMapper.Map(ex);
        }

        return MapResponse(aiResponse);
    }

    /// <summary>Shared mapping for every host adapter, including budget-critical transport options.</summary>
    public static LLMClientRequest MapRequest(LLMRequest request)
    {
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
            MaxOutputTokens = request.MaxTokens,
            RequireOutputTokenLimit = request.RequireOutputTokenLimit,
            DisableTransportRetries = request.DisableTransportRetries,
        };

        // Map tools from GnOuGo.Flow format to GnOuGo.AI.Core format
        if (request.Tools is { Count: > 0 })
        {
            aiRequest.Tools = request.Tools.Select(t => new LLMToolDef
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = t.InputSchema?.DeepClone()
            }).ToList();
        }

        return aiRequest;
    }

    /// <summary>Preserve provider-neutral completion status and verified usage across host boundaries.</summary>
    public static LLMResponse MapResponse(LLMClientResponse aiResponse)
    {
        var response = new LLMResponse
        {
            CompletionStatus = CompletionStatus(aiResponse.Raw),
            Text = aiResponse.Text,
            Json = aiResponse.Json,
            Usage = aiResponse.Usage,
            Raw = aiResponse.Raw,
        };

        // Map tool calls back to GnOuGo.Flow format
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

    internal static string? CompletionStatus(JsonNode? raw)
    {
        if (raw is not JsonObject obj) return null;
        var reason = (obj["choices"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["finish_reason"]?.ToString()
            ?? (obj["incomplete_details"] as JsonObject)?["reason"]?.ToString() ?? obj["stop_reason"]?.ToString() ?? obj["done_reason"]?.ToString();
        return reason switch { "length" or "max_output_tokens" or "max_tokens" => "output_limit", "content_filter" or "refusal" => "refused", _ => null };
    }
}
