using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core;

/// <summary>
/// LLM provider for Ollama local API (/api/chat).
/// </summary>
public sealed class OllamaLLMProvider : ILLMProvider, ILLMModelCatalogProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaLLMProvider> _logger;

    public OllamaLLMProvider(HttpClient http, ILogger<OllamaLLMProvider>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<OllamaLLMProvider>.Instance;
        LLMHttpClientDefaults.EnsureMinimumTimeout(_http);
    }

    /// <inheritdoc />
    public string ProviderType => "ollama";

    /// <inheritdoc />
    public async Task<LLMClientResponse> CallAsync(
        string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
    {
        var url = OllamaEndpoints.Chat(provider.Url);
        var tools = MapTools(request.Tools);
        var jsonMode = request.StructuredOutputSchema != null;

        byte[] payload = ChatRequestBuilder.OllamaFull(
            model, request.Prompt, request.Temperature, tools, jsonMode,
            request.Reasoning);

        using var req = HttpRequestHelper.CreateJsonPost(url, payload);

        // Ollama can also have an API key (e.g. behind a proxy)
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            HttpRequestHelper.SetBearerAuth(req, provider.ApiKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
            throw new HttpRequestException(
                $"Ollama chat call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;

        var content = ChatResponseParser.ExtractOllamaContent(root);
        var toolCalls = ChatResponseParser.ParseOllamaToolCalls(root);
        var usage = ChatResponseParser.ExtractUsage(root);

        JsonNode? jsonOutput = null;
        if (jsonMode && !string.IsNullOrWhiteSpace(content))
        {
            try { jsonOutput = JsonNode.Parse(content); }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Ollama structured output was not valid JSON for model '{Model}'.", model);
            }
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

    private static List<LLMToolDef>? MapTools(IReadOnlyList<LLMToolDef>? tools)
        => tools is { Count: > 0 } ? tools as List<LLMToolDef> ?? new List<LLMToolDef>(tools) : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(ModelProviderOptions provider, CancellationToken ct)
    {
        var url = OllamaEndpoints.Tags(provider.Url);
        using var req = HttpRequestHelper.CreateGet(url);

        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            HttpRequestHelper.SetBearerAuth(req, provider.ApiKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
            throw new HttpRequestException(
                $"Ollama model list call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<LLMModelDescriptor>();
        if (json.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in models.EnumerateArray())
            {
                var id = item.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var resolvedId = string.IsNullOrWhiteSpace(id) ? name : id;
                if (string.IsNullOrWhiteSpace(resolvedId))
                    continue;

                results.Add(new LLMModelDescriptor(
                    resolvedId,
                    string.IsNullOrWhiteSpace(name) ? resolvedId : name,
                    ProviderType,
                    OwnedBy: "ollama"));
            }
        }

        return results;
    }
}

