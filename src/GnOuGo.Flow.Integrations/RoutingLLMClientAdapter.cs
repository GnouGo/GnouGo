using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Integrations;

/// <summary>
/// Real LLM client that routes requests to OpenAI or Ollama using <see cref="RoutingLLMClient"/>.
/// Adapts from <see cref="ILLMClient"/> (GnOuGo.Flow) to <see cref="RoutingLLMClient"/> (GnOuGo.AI.Core).
/// </summary>
public sealed class RoutingLLMClientAdapter : ILLMClient
{
    private readonly RoutingLLMClient _inner;

    public RoutingLLMClientAdapter(RoutingLLMClient inner)
    {
        _inner = inner;
    }

    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
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

        LLMClientResponse aiResponse;
        try
        {
            aiResponse = await _inner.CallAsync(aiRequest, ct);
        }
        catch (LLMProviderException ex)
        {
            throw LLMProviderFailureMapper.Map(ex);
        }

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
