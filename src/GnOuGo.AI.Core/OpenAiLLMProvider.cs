using System.Text.Json;
using System.Text.Json.Nodes;

namespace GnOuGo.AI.Core;

/// <summary>
/// LLM provider for OpenAI-compatible APIs (OpenAI, Azure OpenAI, any /v1/chat/completions endpoint).
/// </summary>
public sealed class OpenAiLLMProvider : ILLMProvider, ILLMModelCatalogProvider
{
    private readonly HttpClient _http;

    public OpenAiLLMProvider(HttpClient http)
    {
        _http = http;
    }

    /// <inheritdoc />
    public string ProviderType => "openai";

    /// <inheritdoc />
    public async Task<LLMClientResponse> CallAsync(
        string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
    {
        var url = OpenAiEndpoints.ChatCompletions(provider.Url);
        var tools = MapTools(request.Tools);

        byte[] payload = ChatRequestBuilder.OpenAiFull(
            model, request.Prompt, request.Temperature, tools,
            request.StructuredOutputSchema, request.StructuredOutputStrict);

        using var req = HttpRequestHelper.CreateJsonPost(url, payload);

        var apiKey = ResolveApiKey(provider);
        if (!string.IsNullOrWhiteSpace(apiKey))
            HttpRequestHelper.SetBearerAuth(req, apiKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
            throw new HttpRequestException(
                $"OpenAI chat call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;

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

    private static List<LLMToolDef>? MapTools(IReadOnlyList<LLMToolDef>? tools)
        => tools is { Count: > 0 } ? tools as List<LLMToolDef> ?? new List<LLMToolDef>(tools) : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(ModelProviderOptions provider, CancellationToken ct)
    {
        var url = OpenAiEndpoints.Models(provider.Url);
        using var req = HttpRequestHelper.CreateGet(url);

        var apiKey = ResolveApiKey(provider);
        if (!string.IsNullOrWhiteSpace(apiKey))
            HttpRequestHelper.SetBearerAuth(req, apiKey);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
            throw new HttpRequestException(
                $"OpenAI model list call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<LLMModelDescriptor>();
        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var ownedBy = item.TryGetProperty("owned_by", out var ownedByEl) ? ownedByEl.GetString() : null;
                results.Add(new LLMModelDescriptor(id, id, ProviderType, ownedBy));
            }
        }

        return results;
    }

    internal static string? ResolveApiKey(ModelProviderOptions provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            return provider.ApiKey;

        return Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    }
}

