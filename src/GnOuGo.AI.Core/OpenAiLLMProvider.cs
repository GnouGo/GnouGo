using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core;

/// <summary>
/// LLM provider for OpenAI-compatible APIs (OpenAI, Azure OpenAI, any /v1/chat/completions endpoint).
/// Uses the same resolved bearer token for inference and model discovery.
/// </summary>
public sealed class OpenAiLLMProvider : ILLMProvider, ILLMModelCatalogProvider
{
    private static readonly TimeSpan BackgroundInitialPollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackgroundMaxPollDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BackgroundUnsupportedCacheDuration = TimeSpan.FromMinutes(65);
    private const string BackgroundUnsupportedCacheKeyPrefix = "gnougo-ai:openai:background-unsupported:";

    private readonly HttpClient _http;
    private readonly ILogger<OpenAiLLMProvider> _logger;
    private readonly IMemoryCache? _backgroundModeCache;

    public OpenAiLLMProvider(
        HttpClient http,
        ILogger<OpenAiLLMProvider>? logger = null,
        IMemoryCache? backgroundModeCache = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<OpenAiLLMProvider>.Instance;
        _backgroundModeCache = backgroundModeCache;
        LLMHttpClientDefaults.EnsureMinimumTimeout(_http);
    }

    /// <inheritdoc />
    public string ProviderType => "openai";

    /// <inheritdoc />
    public async Task<LLMClientResponse> CallAsync(
        string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
    {
        _logger.LogInformation(
            "OpenAI provider call mode selected. Model={Model}; ProviderType={ProviderType}; UseBackgroundMode={UseBackgroundMode}; EndpointBase={EndpointBase}",
            model,
            provider.ResolvedType,
            request.UseBackgroundMode,
            provider.Url);

        if (request.UseBackgroundMode)
            return await CallResponsesBackgroundAsync(model, provider, request, ct);

        return await CallChatCompletionsAsync(model, provider, request, ct);
    }

    private async Task<LLMClientResponse> CallChatCompletionsAsync(
        string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
    {
        var url = OpenAiEndpoints.ChatCompletions(provider.Url, provider.ApiVersion);
        _logger.LogInformation("OpenAI ChatCompletions call: url={Url}, model={Model}, providerType={ProviderType}, httpVersion={HttpVersion}",
            url, model, provider.ResolvedType, _http.DefaultRequestVersion);
        var tools = MapTools(request.Tools);
        var bearerToken = await ProviderAuthenticationResolver.ResolveBearerTokenAsync(_http, provider, ResolveApiKey, ct);

        byte[] payload = ChatRequestBuilder.OpenAiFull(
            model, request.Prompt, request.Temperature, tools,
            request.StructuredOutputSchema, request.StructuredOutputStrict,
            request.Reasoning);

        _logger.LogDebug("OpenAI request body prepared ({ByteCount} bytes).",
            payload.Length);
        _logger.LogDebug("OpenAI bearer token present: {HasToken}",
            !string.IsNullOrWhiteSpace(bearerToken));

        using var req = HttpRequestHelper.CreateJsonPost(url, payload);

        if (!string.IsNullOrWhiteSpace(bearerToken))
            HttpRequestHelper.SetBearerAuth(req, bearerToken);

        _logger.LogInformation("OpenAI request headers: {Headers}",
            string.Join("; ", req.Headers.Select(h =>
                string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                    ? $"{h.Key}=<redacted>"
                    : $"{h.Key}={string.Join(",", h.Value)}")));

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                $"OpenAI chat call to '{url}' failed: {ex.Message}", ex, ex.StatusCode);
        }

        using (resp)
        {
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
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "OpenAI chat completion structured output was not valid JSON for model '{Model}'.", model);
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
    }

    private async Task<LLMClientResponse> CallResponsesBackgroundAsync(
        string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
    {
        var url = OpenAiEndpoints.Responses(provider.Url, provider.ApiVersion);
        var cacheKey = BuildBackgroundUnsupportedCacheKey(provider, url);
        if (IsBackgroundUnsupportedCached(cacheKey))
        {
            _logger.LogInformation(
                "OpenAI Responses background API previously returned unsupported; skipping background mode and using Chat Completions. ResponsesUrl={ResponsesUrl}; Model={Model}; CacheDuration={CacheDuration}",
                url,
                model,
                BackgroundUnsupportedCacheDuration);
            return await CallChatCompletionsAsync(model, provider, request, ct);
        }

        var bearerToken = await ProviderAuthenticationResolver.ResolveBearerTokenAsync(_http, provider, ResolveApiKey, ct);

        _logger.LogInformation(
            "OpenAI Responses background call starting. ResponsesUrl={ResponsesUrl}; Model={Model}; ProviderType={ProviderType}; HttpTimeout={HttpTimeout}; HttpVersion={HttpVersion}",
            url,
            model,
            provider.ResolvedType,
            _http.Timeout,
            _http.DefaultRequestVersion);

        byte[] payload = ChatRequestBuilder.OpenAiResponsesBackground(
            model, request.Prompt, request.Temperature, request.Reasoning);

        using var req = HttpRequestHelper.CreateJsonPost(url, payload);

        if (!string.IsNullOrWhiteSpace(bearerToken))
            HttpRequestHelper.SetBearerAuth(req, bearerToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            if (IsBackgroundUnsupported(resp.StatusCode, body))
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    CacheBackgroundUnsupported(cacheKey, url, model, resp.StatusCode);

                _logger.LogWarning(
                    "OpenAI Responses background API not available, falling back to Chat Completions. " +
                    "ResponsesUrl={ResponsesUrl}; Model={Model}; StatusCode={StatusCode}; ReasonPhrase={ReasonPhrase}; ResponseBody={ResponseBody}",
                    url,
                    model,
                    (int)resp.StatusCode,
                    resp.ReasonPhrase ?? "",
                    FormatLogBody(body));
                return await CallChatCompletionsAsync(model, provider, request, ct);
            }

            throw new HttpRequestException(
                $"OpenAI background response call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}");
        }

        return await AwaitResponsesApiCompletionAsync(url, bearerToken, body, request, ct);
    }

    private bool IsBackgroundUnsupportedCached(string cacheKey)
        => _backgroundModeCache?.TryGetValue(cacheKey, out bool unsupported) == true && unsupported;

    private void CacheBackgroundUnsupported(string cacheKey, string url, string model, System.Net.HttpStatusCode statusCode)
    {
        if (_backgroundModeCache == null)
            return;

        _backgroundModeCache.Set(cacheKey, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = BackgroundUnsupportedCacheDuration
        });
        _logger.LogInformation(
            "Cached OpenAI Responses background unsupported result. ResponsesUrl={ResponsesUrl}; Model={Model}; StatusCode={StatusCode}; CacheDuration={CacheDuration}",
            url,
            model,
            (int)statusCode,
            BackgroundUnsupportedCacheDuration);
    }

    private static string BuildBackgroundUnsupportedCacheKey(ModelProviderOptions provider, string responsesUrl)
        => string.Join("|",
            BackgroundUnsupportedCacheKeyPrefix,
            provider.ResolvedType,
            provider.Url ?? "",
            provider.ApiVersion ?? "",
            responsesUrl);

    private async Task<LLMClientResponse> AwaitResponsesApiCompletionAsync(
        string responsesUrl,
        string? bearerToken,
        string responseBody,
        LLMClientRequest request,
        CancellationToken ct)
    {
        var delay = BackgroundInitialPollDelay;

        while (true)
        {
            using var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;
            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "OpenAI Responses background call completed. ResponseId={ResponseId}; Status={Status}",
                    root.TryGetProperty("id", out var completedId) ? completedId.GetString() : null,
                    status ?? "completed");
                return BuildResponsesApiResponse(root, request);
            }

            if (IsTerminalResponsesStatus(status))
                throw new HttpRequestException($"OpenAI background response ended with status '{status}': {responseBody}");

            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new HttpRequestException($"OpenAI background response did not include an id: {responseBody}");

            await Task.Delay(delay, ct);
            if (delay < BackgroundMaxPollDelay)
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, BackgroundMaxPollDelay.TotalMilliseconds));

            _logger.LogDebug(
                "OpenAI Responses background polling. ResponseId={ResponseId}; Status={Status}; NextPollDelayMs={NextPollDelayMs}",
                id,
                status,
                delay.TotalMilliseconds);

            using var pollReq = HttpRequestHelper.CreateGet(responsesUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(id));
            if (!string.IsNullOrWhiteSpace(bearerToken))
                HttpRequestHelper.SetBearerAuth(pollReq, bearerToken);

            using var pollResp = await _http.SendAsync(pollReq, HttpCompletionOption.ResponseHeadersRead, ct);
            responseBody = await pollResp.Content.ReadAsStringAsync(ct);

            if (!pollResp.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"OpenAI background response polling failed: {(int)pollResp.StatusCode} {pollResp.ReasonPhrase ?? ""} - {responseBody}");
        }
    }

    private LLMClientResponse BuildResponsesApiResponse(JsonElement root, LLMClientRequest request)
    {
        var content = ChatResponseParser.ExtractResponsesApiContent(root).Trim();
        var usage = ChatResponseParser.ExtractUsage(root);

        JsonNode? jsonOutput = null;
        if (request.StructuredOutputSchema != null && !string.IsNullOrWhiteSpace(content))
        {
            try { jsonOutput = JsonNode.Parse(content); }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "OpenAI responses structured output was not valid JSON.");
            }
        }

        return new LLMClientResponse
        {
            Text = content,
            Json = jsonOutput,
            Usage = usage,
            Raw = JsonNode.Parse(root.GetRawText())
        };
    }

    private static bool IsTerminalResponsesStatus(string? status)
        => status is not null
           && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
               || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
               || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)
               || status.Equals("incomplete", StringComparison.OrdinalIgnoreCase));

    private static bool IsBackgroundUnsupported(System.Net.HttpStatusCode statusCode, string body)
        => statusCode is System.Net.HttpStatusCode.NotFound
               or System.Net.HttpStatusCode.MethodNotAllowed
               or System.Net.HttpStatusCode.NotImplemented
           || ((int)statusCode == 400
               && (body.Contains("background", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("responses", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("unsupported", StringComparison.OrdinalIgnoreCase)));

    internal static string FormatLogBody(string? body, int maxLength = 4096)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        var sanitized = body
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Trim();

        if (sanitized.Length <= maxLength)
            return sanitized;

        return sanitized[..maxLength] + $"... (truncated, {sanitized.Length} chars total)";
    }

    private static List<LLMToolDef>? MapTools(IReadOnlyList<LLMToolDef>? tools)
        => tools is { Count: > 0 } ? tools as List<LLMToolDef> ?? new List<LLMToolDef>(tools) : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(ModelProviderOptions provider, CancellationToken ct)
    {
        var url = OpenAiEndpoints.Models(provider.Url, provider.ApiVersion);
        using var req = HttpRequestHelper.CreateGet(url);
        var bearerToken = await ProviderAuthenticationResolver.ResolveBearerTokenAsync(_http, provider, ResolveApiKey, ct);

        if (!string.IsNullOrWhiteSpace(bearerToken))
            HttpRequestHelper.SetBearerAuth(req, bearerToken);

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
