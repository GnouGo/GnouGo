using System.Text.Json.Nodes;
using GnOuGo.AI.Core;

namespace GnOuGo.Flow.Core.Runtime;

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

        var aiResponse = await _inner.CallAsync(aiRequest, ct);

        var response = new LLMResponse
        {
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
}

