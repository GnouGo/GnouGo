using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core;

/// <summary>
/// LLM provider for Ollama local API (/api/chat).
/// </summary>
public sealed class OllamaLLMProvider : ILLMProvider
{
    private readonly HttpClient _http;

    public OllamaLLMProvider(HttpClient http)
    {
        _http = http;
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
            model, request.Prompt, request.Temperature, tools, jsonMode);

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
            catch { /* not valid JSON */ }
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
}

