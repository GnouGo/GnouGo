namespace GnOuGo.AI.Core;

/// <summary>
/// URL builders for OpenAI-compatible API endpoints.
/// </summary>
public static class OpenAiEndpoints
{
    /// <summary>Builds the chat completions URL (appends /v1/chat/completions if needed).
    /// For Azure OpenAI-style deployment URLs (containing /deployments/), appends /chat/completions without /v1.</summary>
    public static string ChatCompletions(string endpointUrl, string? apiVersion = null)
    {
        var b = endpointUrl.TrimEnd('/');
        string path;
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            path = b;
        else if (b.Contains("/deployments/", StringComparison.OrdinalIgnoreCase))
            path = b + "/chat/completions";
        else if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            path = b + "/chat/completions";
        else
            path = b + "/v1/chat/completions";
        return AppendApiVersion(path, apiVersion);
    }
    /// <summary>Builds the Responses API URL (appends /v1/responses if needed).
    /// For Azure OpenAI-style deployment URLs (containing /deployments/), appends /responses without /v1.</summary>
    public static string Responses(string endpointUrl, string? apiVersion = null)
    {
        var b = endpointUrl.TrimEnd('/');
        string path;
        if (b.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            path = b;
        else if (b.Contains("/deployments/", StringComparison.OrdinalIgnoreCase))
            path = b + "/responses";
        else
            path = b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? b + "/responses"
                : b + "/v1/responses";
        return AppendApiVersion(path, apiVersion);
    }

    /// <summary>Builds the embeddings URL (appends /v1/embeddings if needed).</summary>
    public static string Embeddings(string endpointUrl)
    {
        var b = endpointUrl.TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? b + "/embeddings"
            : b + "/v1/embeddings";
    }

    /// <summary>Builds the models URL (appends /v1/models if needed).</summary>
    public static string Models(string endpointUrl, string? apiVersion = null)
    {
        var b = endpointUrl.TrimEnd('/');
        var path = b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? b + "/models"
            : b + "/v1/models";
        return AppendApiVersion(path, apiVersion);
    }

    /// <summary>Appends ?api-version=... to a URL if apiVersion is specified.</summary>
    internal static string AppendApiVersion(string url, string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return url;
        var separator = url.Contains('?') ? '&' : '?';
        var encodedApiVersion = global::System.Uri.EscapeDataString(apiVersion);
        return $"{url}{separator}api-version={encodedApiVersion}";
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

    /// <summary>Builds the Ollama model tags URL (/api/tags).</summary>
    public static string Tags(string baseUrl)
        => baseUrl.TrimEnd('/') + "/api/tags";
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

    /// <summary>
    /// Builds candidate URLs for model discovery.
    /// GitHub Models commonly exposes a catalog endpoint, while proxies may expose an OpenAI-style /models route.
    /// </summary>
    public static IReadOnlyList<string> ModelListCandidates(string? baseUrl = null)
    {
        var original = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl;
        var b = original.TrimEnd('/');

        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            b = b[..^"/chat/completions".Length];

        var candidates = new List<string>();

        if (b.EndsWith("/inference", StringComparison.OrdinalIgnoreCase))
        {
            var root = b[..^"/inference".Length].TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(root))
                candidates.Add(root + "/catalog/models");
        }

        if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            candidates.Add(b + "/models");
        else
            candidates.Add(b + "/models");

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

