using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core;

/// <summary>
/// Parses chat completion responses from OpenAI and Ollama APIs.
/// </summary>
public static class ChatResponseParser
{
    /// <summary>
    /// Extracts content from an OpenAI Chat Completions response: choices[0].message.content
    /// </summary>
    public static string ExtractOpenAiContent(JsonElement root)
        => root.GetProperty("choices")[0]
               .GetProperty("message")
               .GetProperty("content")
               .GetString()?.Trim() ?? string.Empty;

    /// <summary>
    /// Extracts content from an Ollama chat response: message.content
    /// </summary>
    public static string ExtractOllamaContent(JsonElement root)
        => root.GetProperty("message")
               .GetProperty("content")
               .GetString()?.Trim() ?? string.Empty;

    /// <summary>
    /// Extracts assistant text from an OpenAI Responses API response.
    /// Handles: output[] → message (role=assistant) → content[] → output_text → text.
    /// </summary>
    public static string ExtractResponsesApiContent(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var sb = new StringBuilder(capacity: 1024);

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeProp))
                continue;
            if (!string.Equals(typeProp.GetString(), "message", StringComparison.Ordinal))
                continue;

            if (!item.TryGetProperty("role", out var roleProp) || roleProp.GetString() != "assistant")
                continue;

            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (!part.TryGetProperty("type", out var partTypeProp))
                    continue;

                if (partTypeProp.GetString() is "output_text")
                {
                    if (part.TryGetProperty("text", out var textProp))
                        sb.Append(textProp.GetString());
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats an HTTP error response for display.
    /// </summary>
    public static string FormatHttpError(System.Net.HttpStatusCode status, string? payload)
    {
        var msg = $"OpenAI request failed ({(int)status} {status}).";
        if (!string.IsNullOrWhiteSpace(payload))
            msg += "\n" + payload;
        return msg;
    }

    /// <summary>
    /// Parses tool_calls from an OpenAI chat completion response.
    /// Returns null if no tool calls were requested.
    /// </summary>
    public static List<ToolCallResult>? ParseOpenAiToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
            return null;

        var message = choices[0].GetProperty("message");
        if (!message.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array ||
            toolCalls.GetArrayLength() == 0)
            return null;

        var results = new List<ToolCallResult>();
        foreach (var tc in toolCalls.EnumerateArray())
        {
            var fn = tc.GetProperty("function");
            results.Add(new ToolCallResult
            {
                Id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
                Name = fn.GetProperty("name").GetString() ?? "",
                Arguments = fn.TryGetProperty("arguments", out var argsProp)
                    ? JsonNode.Parse(argsProp.GetString() ?? "{}")
                    : null
            });
        }
        return results;
    }

    /// <summary>
    /// Parses tool_calls from an Ollama chat response.
    /// </summary>
    public static List<ToolCallResult>? ParseOllamaToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message))
            return null;

        if (!message.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array ||
            toolCalls.GetArrayLength() == 0)
            return null;

        var results = new List<ToolCallResult>();
        foreach (var tc in toolCalls.EnumerateArray())
        {
            var fn = tc.GetProperty("function");
            results.Add(new ToolCallResult
            {
                Id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
                Name = fn.GetProperty("name").GetString() ?? "",
                Arguments = fn.TryGetProperty("arguments", out var argsProp)
                    ? JsonNode.Parse(argsProp.GetRawText())
                    : null
            });
        }
        return results;
    }

    /// <summary>
    /// Extracts usage information from an OpenAI or Ollama response as a JsonObject.
    /// </summary>
    public static JsonObject? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
            return null;

        return JsonNode.Parse(usage.GetRawText()) as JsonObject;
    }
}
