using System.Text.Json;
using System.Text.Json.Nodes;
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

    private readonly HttpClient _http;
    private readonly ILogger<OpenAiLLMProvider> _logger;

    public OpenAiLLMProvider(
        HttpClient http,
        ILogger<OpenAiLLMProvider>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<OpenAiLLMProvider>.Instance;
        LLMHttpClientDefaults.EnsureMinimumTimeout(_http);
    }

    /// <inheritdoc />
    public string ProviderType => "openai";

    /// <inheritdoc />
    public async Task<LLMClientResponse> CallAsync(
        string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
    {
        _logger.LogInformation(
            "OpenAI provider call mode selected. Model={Model}; ProviderType={ProviderType}; UseBackgroundMode={UseBackgroundMode}",
            model,
            provider.ResolvedType,
            request.UseBackgroundMode);

        if (request.UseBackgroundMode)
        {
            return provider.RequestPolicy.BackgroundProtocol switch
            {
                LLMBackgroundProtocolMode.ChatCompletions =>
                    await CallChatCompletionsAsync(model, provider, request, ct),
                _ => await CallResponsesBackgroundAsync(model, provider, request, ct)
            };
        }

        return await CallChatCompletionsAsync(model, provider, request, ct);
    }

    private async Task<LLMClientResponse> CallChatCompletionsAsync(
        string model, ModelProviderOptions provider, LLMClientRequest request, CancellationToken ct)
    {
        var url = OpenAiEndpoints.ChatCompletions(provider.Url, provider.ApiVersion);
        var tools = MapTools(request.Tools);
        var bearerToken = await ProviderAuthenticationResolver.ResolveBearerTokenAsync(_http, provider, ResolveApiKey, ct);
        var attempt = await SendChatCompletionsAttemptAsync(
            url,
            model,
            provider,
            request,
            tools,
            bearerToken,
            ct);
        if (attempt.IsSuccess)
            return attempt.Response!;

        var safeBody = FormatProviderErrorBody(attempt.ErrorBody, provider, bearerToken, request.Prompt);
        throw BuildChatCompletionsFailure(attempt, safeBody);
    }

    private async Task<ChatCompletionAttempt> SendChatCompletionsAttemptAsync(
        string url,
        string model,
        ModelProviderOptions provider,
        LLMClientRequest request,
        IReadOnlyList<LLMToolDef>? tools,
        string? bearerToken,
        CancellationToken ct)
    {
        const string protocolMode = "standard";
        _logger.LogInformation(
            "OpenAI ChatCompletions call: model={Model}, providerType={ProviderType}, protocolMode={ProtocolMode}, httpVersion={HttpVersion}",
            model,
            provider.ResolvedType,
            protocolMode,
            _http.DefaultRequestVersion);

        byte[] payload = ChatRequestBuilder.OpenAiFull(
            model,
            request.Prompt,
            request.Temperature,
            tools,
            request.StructuredOutputSchema,
            request.StructuredOutputStrict,
            request.Reasoning,
            request.MaxOutputTokens);

        _logger.LogDebug(
            "OpenAI request body prepared ({ByteCount} bytes). ProtocolMode={ProtocolMode}",
            payload.Length,
            protocolMode);
        _logger.LogDebug("OpenAI bearer token present: {HasToken}",
            !string.IsNullOrWhiteSpace(bearerToken));

        HttpRequestMessage CreateChatRequest()
        {
            var requestMessage = HttpRequestHelper.CreateJsonPost(url, payload);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                HttpRequestHelper.SetBearerAuth(requestMessage, bearerToken);
            return requestMessage;
        }

        HttpResponseMessage resp;
        try
        {
            resp = await HttpRequestHelper.SendWithServerErrorRetryAsync(
                _http,
                CreateChatRequest,
                HttpCompletionOption.ResponseHeadersRead,
                _logger,
                "OpenAI chat completion",
                provider.RetryPolicy,
                ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "OpenAI Chat Completions transport failed before a response was available. FailureType={FailureType}",
                ex.GetType().Name);
            throw;
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
                return ChatCompletionAttempt.Failed(
                    resp.StatusCode,
                    resp.ReasonPhrase ?? "",
                    body,
                    HttpRequestHelper.GetRetryMetadata(resp));
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

            return ChatCompletionAttempt.Succeeded(new LLMClientResponse
            {
                Text = content,
                Json = jsonOutput,
                Usage = usage,
                Raw = JsonNode.Parse(root.GetRawText()),
                ToolCalls = toolCalls
            });
        }
    }

    private static HttpRequestException BuildChatCompletionsFailure(
        ChatCompletionAttempt attempt,
        string safeBody)
        => HttpRequestHelper.CreateFailure(
            $"OpenAI chat call failed: {(int)attempt.StatusCode} {attempt.ReasonPhrase} - {safeBody}",
            attempt.StatusCode,
            attempt.RetryMetadata);

    private async Task<LLMClientResponse> CallResponsesBackgroundAsync(
        string model,
        ModelProviderOptions provider,
        LLMClientRequest request,
        CancellationToken ct)
    {
        var url = OpenAiEndpoints.Responses(provider.Url, provider.ApiVersion);
        var bearerToken = await ProviderAuthenticationResolver.ResolveBearerTokenAsync(_http, provider, ResolveApiKey, ct);

        _logger.LogInformation(
            "OpenAI Responses background call starting. Model={Model}; ProviderType={ProviderType}; HttpTimeout={HttpTimeout}; HttpVersion={HttpVersion}",
            model,
            provider.ResolvedType,
            _http.Timeout,
            _http.DefaultRequestVersion);

        byte[] payload = ChatRequestBuilder.OpenAiResponsesBackground(
            model,
            request.Prompt,
            request.Temperature,
            request.Reasoning,
            request.StructuredOutputSchema,
            request.StructuredOutputStrict,
            request.MaxOutputTokens);

        HttpRequestMessage CreateBackgroundRequest()
        {
            var requestMessage = HttpRequestHelper.CreateJsonPost(url, payload);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                HttpRequestHelper.SetBearerAuth(requestMessage, bearerToken);
            return requestMessage;
        }

        using var resp = await HttpRequestHelper.SendWithServerErrorRetryAsync(
            _http,
            CreateBackgroundRequest,
            HttpCompletionOption.ResponseHeadersRead,
            _logger,
            "OpenAI background response creation",
            provider.RetryPolicy,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var safeBody = FormatProviderErrorBody(body, provider, bearerToken, request.Prompt);
            throw HttpRequestHelper.CreateFailure(
                $"OpenAI background response call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {safeBody}",
                resp);
        }

        return await AwaitResponsesApiCompletionAsync(
            url,
            bearerToken,
            body,
            request,
            provider.RetryPolicy,
            ct);
    }

    private async Task<LLMClientResponse> AwaitResponsesApiCompletionAsync(
        string responsesUrl,
        string? bearerToken,
        string responseBody,
        LLMClientRequest request,
        LLMProviderRetryPolicyOptions retryPolicy,
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
                throw new HttpRequestException(
                    $"OpenAI background response ended with status '{status}': "
                    + FormatProviderErrorBody(responseBody, sensitiveValues: [request.Prompt]));

            if (!status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("in_progress", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException(
                    $"OpenAI background response returned an unexpected status '{status}': "
                    + FormatProviderErrorBody(responseBody, sensitiveValues: [request.Prompt]));
            }

            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new HttpRequestException(
                    "OpenAI background response did not include an id: "
                    + FormatProviderErrorBody(responseBody, sensitiveValues: [request.Prompt]));

            await Task.Delay(delay, ct);
            if (delay < BackgroundMaxPollDelay)
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, BackgroundMaxPollDelay.TotalMilliseconds));

            _logger.LogDebug(
                "OpenAI Responses background polling. ResponseId={ResponseId}; Status={Status}; NextPollDelayMs={NextPollDelayMs}",
                id,
                status,
                delay.TotalMilliseconds);

            var pollUrl = responsesUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(id);
            HttpRequestMessage CreatePollRequest()
            {
                var requestMessage = HttpRequestHelper.CreateGet(pollUrl);
                if (!string.IsNullOrWhiteSpace(bearerToken))
                    HttpRequestHelper.SetBearerAuth(requestMessage, bearerToken);
                return requestMessage;
            }

            using var pollResp = await HttpRequestHelper.SendWithServerErrorRetryAsync(
                _http,
                CreatePollRequest,
                HttpCompletionOption.ResponseHeadersRead,
                _logger,
                "OpenAI background response polling",
                retryPolicy,
                ct);
            responseBody = await pollResp.Content.ReadAsStringAsync(ct);

            if (!pollResp.IsSuccessStatusCode)
            {
                var safeBody = FormatProviderErrorBody(responseBody, sensitiveValues: [bearerToken, request.Prompt]);
                throw HttpRequestHelper.CreateFailure(
                    $"OpenAI background response polling failed: {(int)pollResp.StatusCode} {pollResp.ReasonPhrase ?? ""} - {safeBody}",
                    pollResp);
            }
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

    private static string FormatProviderErrorBody(
        string? body,
        ModelProviderOptions provider,
        string? bearerToken,
        string? prompt)
        => FormatProviderErrorBody(
            body,
            [provider.ApiKey, provider.ClientSecret, provider.PrivateKeyPem, bearerToken, prompt]);

    private static string FormatProviderErrorBody(string? body, IReadOnlyList<string?> sensitiveValues)
    {
        var sanitized = body ?? string.Empty;
        foreach (var sensitiveValue in sensitiveValues)
        {
            if (string.IsNullOrWhiteSpace(sensitiveValue))
                continue;

            sanitized = sanitized.Replace(sensitiveValue, "<redacted>", StringComparison.Ordinal);
            var jsonEncodedValue = JsonEncodedText.Encode(sensitiveValue).ToString();
            if (!string.Equals(jsonEncodedValue, sensitiveValue, StringComparison.Ordinal))
                sanitized = sanitized.Replace(jsonEncodedValue, "<redacted>", StringComparison.Ordinal);
        }

        return FormatLogBody(sanitized);
    }

    private sealed record ChatCompletionAttempt(
        LLMClientResponse? Response,
        System.Net.HttpStatusCode StatusCode,
        string ReasonPhrase,
        string ErrorBody,
        LLMHttpRetryMetadata? RetryMetadata)
    {
        public bool IsSuccess => Response is not null;

        public static ChatCompletionAttempt Succeeded(LLMClientResponse response)
            => new(response, System.Net.HttpStatusCode.OK, "", "", null);

        public static ChatCompletionAttempt Failed(
            System.Net.HttpStatusCode statusCode,
            string reasonPhrase,
            string errorBody,
            LLMHttpRetryMetadata? retryMetadata)
            => new(null, statusCode, reasonPhrase, errorBody, retryMetadata);
    }

    private static List<LLMToolDef>? MapTools(IReadOnlyList<LLMToolDef>? tools)
        => tools is { Count: > 0 } ? tools as List<LLMToolDef> ?? new List<LLMToolDef>(tools) : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LLMModelDescriptor>> ListModelsAsync(ModelProviderOptions provider, CancellationToken ct)
    {
        var url = OpenAiEndpoints.Models(provider.Url, provider.ApiVersion);
        var bearerToken = await ProviderAuthenticationResolver.ResolveBearerTokenAsync(_http, provider, ResolveApiKey, ct);
        HttpRequestMessage CreateModelListRequest()
        {
            var requestMessage = HttpRequestHelper.CreateGet(url);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                HttpRequestHelper.SetBearerAuth(requestMessage, bearerToken);
            return requestMessage;
        }

        using var resp = await HttpRequestHelper.SendWithServerErrorRetryAsync(
            _http,
            CreateModelListRequest,
            HttpCompletionOption.ResponseHeadersRead,
            _logger,
            "OpenAI model discovery",
            provider.RetryPolicy,
            ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
            var safeBody = FormatProviderErrorBody(body, provider, bearerToken, prompt: null);
            throw HttpRequestHelper.CreateFailure(
                $"OpenAI model list call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {safeBody}",
                resp);
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
