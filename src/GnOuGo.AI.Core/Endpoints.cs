namespace GnOuGo.AI.Core;

/// <summary>
/// URL builders for OpenAI-compatible API endpoints.
/// </summary>
public static class OpenAiEndpoints
{
    /// <summary>Builds the chat completions URL (appends /v1/chat/completions if needed).</summary>
    public static string ChatCompletions(string endpointUrl)
    {
        var b = endpointUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? b + "/chat/completions"
            : b + "/v1/chat/completions";
    }

    /// <summary>Builds the embeddings URL (appends /v1/embeddings if needed).</summary>
    public static string Embeddings(string endpointUrl)
    {
        var b = endpointUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? b + "/embeddings"
            : b + "/v1/embeddings";
    }
}

/// <summary>
/// URL builders for Ollama local API endpoints.
/// </summary>
public static class OllamaEndpoints
{
    /// <summary>Builds the Ollama chat URL (/api/chat).</summary>
    public static string Chat(string baseUrl)
        => baseUrl.TrimEnd('/') + "/api/chat";

    /// <summary>Builds the Ollama embed URL (/api/embed).</summary>
    public static string Embed(string baseUrl)
        => baseUrl.TrimEnd('/') + "/api/embed";
}

/// <summary>
/// URL builders for GitHub Copilot / GitHub Models API endpoints.
/// Default base: https://models.github.ai/inference
/// </summary>
public static class CopilotEndpoints
{
    /// <summary>Default GitHub Models inference endpoint.</summary>
    public const string DefaultBase = "https://models.github.ai/inference";

    /// <summary>Builds the chat completions URL.</summary>
    public static string ChatCompletions(string? baseUrl = null)
    {
        var b = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl).TrimEnd('/');
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return b;
        return b + "/chat/completions";
    }
}

