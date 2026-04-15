using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core;

/// <summary>
/// LLM provider for GitHub Copilot / GitHub Models API.
/// Uses the OpenAI-compatible chat completions endpoint exposed by GitHub.
///
/// Endpoint: https://models.github.ai/inference  (or custom via config)
/// Auth: Bearer token — API key, OIDC access token, GitHub PAT, GITHUB_TOKEN env var, or Copilot OAuth token.
///
/// Model names may use the "vendor/model" convention (e.g. "openai/gpt-4.1")
/// or plain names (e.g. "gpt-4.1", "o4-mini", "claude-sonnet-4").
/// The provider strips the vendor prefix before sending to the API.
/// </summary>
public sealed class CopilotLLMProvider : ILLMProvider, ILLMModelCatalogProvider
{
    /// <summary>
    /// Default GitHub Models inference endpoint.
    /// </summary>
    public const string DefaultEndpoint = "https://models.github.ai/inference";

    private readonly HttpClient _http;

    public CopilotLLMProvider(HttpClient http)
    {
        _http = http;
    }

    /// <inheritdoc />
    public string ProviderType => "copilot";

    /// <summary>
    /// Sends a chat completion request to the GitHub Models / Copilot provider.
    /// </summary>
    public async Task<LLMClientResponse> CallAsync(
        string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
    {
        // Resolve the base URL (default to GitHub Models endpoint)
        var baseUrl = !string.IsNullOrWhiteSpace(provider.Url)
            ? provider.Url
            : DefaultEndpoint;

        // Build the chat completions URL — GitHub Models uses /chat/completions directly
        var url = BuildChatCompletionsUrl(baseUrl);

        // Resolve the actual model name — strip vendor prefix if present (e.g. "openai/gpt-4.1" → "gpt-4.1")
        var resolvedModel = StripVendorPrefix(model);
        var bearerToken = await ProviderAuthenticationResolver.ResolveBearerTokenAsync(_http, provider, ResolveToken, ct);

        var tools = MapTools(request.Tools);

        byte[] payload = ChatRequestBuilder.OpenAiFull(
            resolvedModel, request.Prompt, request.Temperature, tools,
            request.StructuredOutputSchema, request.StructuredOutputStrict);

        using var req = HttpRequestHelper.CreateJsonPost(url, payload);

        if (!string.IsNullOrWhiteSpace(bearerToken))
            HttpRequestHelper.SetBearerAuth(req, bearerToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
            throw new HttpRequestException(
                $"Copilot/GitHub Models chat call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;

        // GitHub Models returns standard OpenAI-compatible responses
        var content = ChatResponseParser.ExtractOpenAiContent(root);
        var toolCalls = ChatResponseParser.ParseOpenAiToolCalls(root);
        var usage = ChatResponseParser.ExtractUsage(root);

        JsonNode? jsonOutput = null;
        if (request.StructuredOutputSchema != null && !string.IsNullOrWhiteSpace(content))
        {
            try { jsonOutput = JsonNode.Parse(content); }
            catch { /* not valid JSON, leave null */ }
        }

        return new LLMClientResponse
        {
            Text = content,
            Json = jsonOutput,
            Usage = usage,
            Raw = JsonNode.Parse(root.GetRawText()),
            ToolCalls = toolCalls
        };
    }

    /// <summary>
    /// Resolves fallback authentication tokens for the Copilot provider.
    /// Priority: config ApiKey → GITHUB_TOKEN env var → COPILOT_API_KEY env var.
    /// </summary>
    internal static string? ResolveToken(ModelProviderOptions provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            return provider.ApiKey;

        var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
            return envToken;

        var copilotKey = Environment.GetEnvironmentVariable("COPILOT_API_KEY");
        if (!string.IsNullOrWhiteSpace(copilotKey))
            return copilotKey;

        return null;
    }

    /// <summary>
    /// Builds the chat completions URL for GitHub Models.
    /// If the URL already ends with /chat/completions, use as-is.
    /// Otherwise, append /chat/completions.
    /// </summary>
    internal static string BuildChatCompletionsUrl(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return b;
        if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return b + "/chat/completions";
        return b + "/chat/completions";
    }

    /// <summary>
    /// Strips the vendor prefix from a model name if present.
    /// E.g. "openai/gpt-4.1" → "gpt-4.1", "anthropic/claude-sonnet-4" → "claude-sonnet-4".
    /// Plain names like "gpt-4o" are returned as-is.
    /// </summary>
    internal static string StripVendorPrefix(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return model;

        var slashIdx = model.IndexOf('/');
        if (slashIdx > 0 && slashIdx < model.Length - 1)
        {
            // Only strip if the prefix looks like a vendor (no dots, reasonable length)
            var prefix = model[..slashIdx];
            if (prefix.Length <= 30 && !prefix.Contains('.'))
                return model[(slashIdx + 1)..];
        }

        return model;
    }

    private static List<LLMToolDef>? MapTools(IReadOnlyList<LLMToolDef>? tools)
        => tools is { Count: > 0 } ? tools as List<LLMToolDef> ?? new List<LLMToolDef>(tools) : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(ModelProviderOptions provider, CancellationToken ct)
    {
        var bearerToken = await ProviderAuthenticationResolver.ResolveBearerTokenAsync(_http, provider, ResolveToken, ct);
        Exception? lastError = null;

        foreach (var candidate in CopilotEndpoints.ModelListCandidates(provider.Url))
        {
            try
            {
                using var req = GnOuGo.AI.Core.HttpRequestHelper.CreateGet(candidate);
                if (!string.IsNullOrWhiteSpace(bearerToken))
                    HttpRequestHelper.SetBearerAuth(req, bearerToken);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
                    lastError = new HttpRequestException(
                        $"Copilot/GitHub Models list call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");

                    if ((int)resp.StatusCode is 404 or 405 or 501)
                        continue;

                    throw lastError;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var models = ParseModelResponse(json.RootElement);
                return models;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (IsNonRetryableModelDiscoveryError(ex))
                    throw;

                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Unable to retrieve Copilot/GitHub Models catalog.");
    }

    internal static IReadOnlyList<LLMModelDescriptor> ParseModelResponse(JsonElement root)
    {
        IEnumerable<JsonElement> items = [];

        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root.EnumerateArray();
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                items = data.EnumerateArray();
            else if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                items = models.EnumerateArray();
        }

        var results = new List<LLMModelDescriptor>();
        foreach (var item in items)
        {
            var id = TryGetString(item, "id")
                ?? TryGetString(item, "name")
                ?? TryGetString(item, "model")
                ?? TryGetString(item, "slug");

            if (string.IsNullOrWhiteSpace(id))
                continue;

            var displayName = TryGetString(item, "name")
                ?? TryGetString(item, "friendly_name")
                ?? id;

            var ownedBy = TryGetString(item, "publisher")
                ?? TryGetString(item, "owned_by")
                ?? TryGetString(item, "vendor");

            results.Add(new LLMModelDescriptor(id, displayName, "copilot", ownedBy));
        }

        return results
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    private static string? TryGetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsNonRetryableModelDiscoveryError(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("401 Unauthorized", StringComparison.OrdinalIgnoreCase)
               || message.Contains("rate-limited", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Too many requests", StringComparison.OrdinalIgnoreCase);
    }
}

