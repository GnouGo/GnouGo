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
    private const string LegacyChatRequiredCacheKeyPrefix = "gnougo-ai:openai:legacy-chat-required:";

    private readonly HttpClient _http;
    private readonly ILogger<OpenAiLLMProvider> _logger;
    private readonly IMemoryCache? _compatibilityCache;

    public OpenAiLLMProvider(
        HttpClient http,
        ILogger<OpenAiLLMProvider>? logger = null,
        IMemoryCache? backgroundModeCache = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<OpenAiLLMProvider>.Instance;
        _compatibilityCache = backgroundModeCache;
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
                LLMBackgroundProtocolMode.Responses =>
                    await CallResponsesBackgroundAsync(model, provider, request, allowFallback: false, ct),
                _ => await CallResponsesBackgroundAsync(model, provider, request, allowFallback: true, ct)
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
        var legacyCacheKey = BuildLegacyChatRequiredCacheKey(provider, model, url);
        var useLegacyChat = request.MaxOutputTokens is > 0
                            && !IsOfficialOpenAiEndpoint(provider.Url)
                            && IsLegacyChatRequiredCached(legacyCacheKey);

        if (useLegacyChat)
        {
            _logger.LogInformation(
                "OpenAI-compatible endpoint previously required legacy Chat Completions; omitting max_completion_tokens. " +
                "Model={Model}; CacheDuration={CacheDuration}",
                model,
                BackgroundUnsupportedCacheDuration);
        }

        var attempt = await SendChatCompletionsAttemptAsync(
            url,
            model,
            provider,
            request,
            tools,
            bearerToken,
            includeMaxOutputTokens: !useLegacyChat,
            ct);
        if (attempt.IsSuccess)
            return attempt.Response!;

        var safeBody = FormatProviderErrorBody(attempt.ErrorBody, provider, bearerToken, request.Prompt);
        if (!useLegacyChat
            && IsLegacyChatRetryCandidate(attempt.StatusCode, attempt.ErrorBody, provider.Url, request.MaxOutputTokens))
        {
            _logger.LogWarning(
                "OpenAI-compatible Chat Completions rejected max_completion_tokens; retrying once without only that optional field. " +
                "Model={Model}; StatusCode={StatusCode}; ReasonPhrase={ReasonPhrase}",
                model,
                (int)attempt.StatusCode,
                attempt.ReasonPhrase);

            ChatCompletionAttempt legacyAttempt;
            try
            {
                legacyAttempt = await SendChatCompletionsAttemptAsync(
                    url,
                    model,
                    provider,
                    request,
                    tools,
                    bearerToken,
                    includeMaxOutputTokens: false,
                    ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                _logger.LogWarning(
                    "Legacy-compatible OpenAI Chat Completions fallback failed before receiving a response. " +
                    "Model={Model}; FailureType={FailureType}",
                    model,
                    ex.GetType().Name);
                throw;
            }

            if (legacyAttempt.IsSuccess)
            {
                CacheLegacyChatRequired(legacyCacheKey, url, model, attempt.StatusCode);
                _logger.LogWarning(
                    "Legacy-compatible OpenAI Chat Completions fallback succeeded after omitting max_completion_tokens. " +
                    "Model={Model}; StandardStatusCode={StandardStatusCode}",
                    model,
                    (int)attempt.StatusCode);
                return legacyAttempt.Response!;
            }

            var legacySafeBody = FormatProviderErrorBody(
                legacyAttempt.ErrorBody,
                provider,
                bearerToken,
                request.Prompt);
            _logger.LogWarning(
                "Legacy-compatible OpenAI Chat Completions fallback failed; compatibility result was not cached. " +
                "Model={Model}; StatusCode={StatusCode}; ReasonPhrase={ReasonPhrase}",
                model,
                (int)legacyAttempt.StatusCode,
                legacyAttempt.ReasonPhrase);
            throw BuildChatCompletionsFailure(legacyAttempt, legacySafeBody);
        }

        throw BuildChatCompletionsFailure(attempt, safeBody);
    }

    private async Task<ChatCompletionAttempt> SendChatCompletionsAttemptAsync(
        string url,
        string model,
        ModelProviderOptions provider,
        LLMClientRequest request,
        IReadOnlyList<LLMToolDef>? tools,
        string? bearerToken,
        bool includeMaxOutputTokens,
        CancellationToken ct)
    {
        var protocolMode = includeMaxOutputTokens
            ? "standard"
            : "legacy_without_max_completion_tokens";
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
            includeMaxOutputTokens ? request.MaxOutputTokens : null);

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
        bool allowFallback,
        CancellationToken ct)
    {
        var url = OpenAiEndpoints.Responses(provider.Url, provider.ApiVersion);
        var cacheKey = BuildBackgroundUnsupportedCacheKey(provider, url);
        if (allowFallback && IsBackgroundUnsupportedCached(cacheKey))
        {
            _logger.LogInformation(
                "OpenAI Responses background API previously returned unsupported; skipping background mode and using Chat Completions. Model={Model}; CacheDuration={CacheDuration}",
                model,
                BackgroundUnsupportedCacheDuration);
            return await CallChatCompletionsAsync(model, provider, request, ct);
        }

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
            if (IsBackgroundUnsupported(resp.StatusCode, body, provider.Url))
            {
                if (!allowFallback)
                {
                    var forcedSafeBody = FormatProviderErrorBody(body, provider, bearerToken, request.Prompt);
                    throw HttpRequestHelper.CreateFailure(
                        $"OpenAI background response call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {forcedSafeBody}",
                        resp);
                }

                var deterministicRouteFailure = IsDeterministicallyUnsupportedResponsesRoute(resp.StatusCode);
                if (deterministicRouteFailure)
                    CacheBackgroundUnsupported(cacheKey, url, model, resp.StatusCode);
                var fallbackResponse = await CallChatCompletionsAsync(model, provider, request, ct);

                if (!deterministicRouteFailure)
                    CacheBackgroundUnsupported(cacheKey, url, model, resp.StatusCode);
                _logger.LogWarning(
                    "OpenAI Responses background API not available; Chat Completions fallback succeeded. " +
                    "Model={Model}; StatusCode={StatusCode}; ReasonPhrase={ReasonPhrase}",
                    model,
                    (int)resp.StatusCode,
                    resp.ReasonPhrase ?? "");
                return fallbackResponse;
            }

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

    private bool IsBackgroundUnsupportedCached(string cacheKey)
        => _compatibilityCache?.TryGetValue(cacheKey, out bool unsupported) == true && unsupported;

    private void CacheBackgroundUnsupported(string cacheKey, string url, string model, System.Net.HttpStatusCode statusCode)
    {
        if (_compatibilityCache == null)
            return;

        _compatibilityCache.Set(cacheKey, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = BackgroundUnsupportedCacheDuration
        });
        _logger.LogInformation(
            "Cached OpenAI Responses background unsupported result. Model={Model}; StatusCode={StatusCode}; CacheDuration={CacheDuration}",
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

    private bool IsLegacyChatRequiredCached(string cacheKey)
        => _compatibilityCache?.TryGetValue(cacheKey, out bool required) == true && required;

    private void CacheLegacyChatRequired(
        string cacheKey,
        string url,
        string model,
        System.Net.HttpStatusCode statusCode)
    {
        if (_compatibilityCache == null)
            return;

        _compatibilityCache.Set(cacheKey, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = BackgroundUnsupportedCacheDuration
        });
        _logger.LogInformation(
            "Cached legacy-compatible OpenAI Chat Completions requirement. Model={Model}; StatusCode={StatusCode}; CacheDuration={CacheDuration}",
            model,
            (int)statusCode,
            BackgroundUnsupportedCacheDuration);
    }

    private static string BuildLegacyChatRequiredCacheKey(
        ModelProviderOptions provider,
        string model,
        string chatUrl)
        => string.Join("|",
            LegacyChatRequiredCacheKeyPrefix,
            provider.ResolvedType,
            provider.Url ?? "",
            provider.ApiVersion ?? "",
            model,
            chatUrl);

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

    private static bool IsBackgroundUnsupported(
        System.Net.HttpStatusCode statusCode,
        string body,
        string? endpointBase)
    {
        if (IsOfficialOpenAiEndpoint(endpointBase))
            return false;

        if (ReportsRequestSpecificProviderFailure(
                body,
                allowBackgroundParameter: statusCode is System.Net.HttpStatusCode.BadRequest
                    or System.Net.HttpStatusCode.UnprocessableEntity,
                allowRouteParameter: IsDeterministicallyUnsupportedResponsesRoute(statusCode)))
        {
            return false;
        }

        // Route-level 404/405/501 responses are deterministic protocol incompatibilities.
        // Request-specific errors above are never cached as route capability evidence.
        if (IsDeterministicallyUnsupportedResponsesRoute(statusCode))
            return true;

        if (statusCode is System.Net.HttpStatusCode.MethodNotAllowed
            or System.Net.HttpStatusCode.NotImplemented)
        {
            return true;
        }

        return statusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.UnprocessableEntity;
    }

    private static bool IsDeterministicallyUnsupportedResponsesRoute(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.MethodNotAllowed
            or System.Net.HttpStatusCode.NotImplemented;

    private static bool IsOfficialOpenAiEndpoint(string? endpointBase)
        => Uri.TryCreate(endpointBase, UriKind.Absolute, out var uri)
           && string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyChatRetryCandidate(
        System.Net.HttpStatusCode statusCode,
        string body,
        string? endpointBase,
        int? maxOutputTokens)
    {
        if (IsOfficialOpenAiEndpoint(endpointBase)
            || maxOutputTokens is not > 0
            || statusCode is not (System.Net.HttpStatusCode.BadRequest
                or System.Net.HttpStatusCode.UnprocessableEntity))
        {
            return false;
        }

        var explicitParameter = TryReadProviderErrorParameter(body);
        if (!string.IsNullOrWhiteSpace(explicitParameter))
            return IsTokenLimitParameter(explicitParameter);

        if (ContainsTokenLimitMarker(body))
            return true;

        return !ExplicitlyReportsDifferentChatFailure(body);
    }

    private static string? TryReadProviderErrorParameter(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var error = json.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement
                : json.RootElement;
            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("param", out var parameterElement)
                && parameterElement.ValueKind == JsonValueKind.String)
            {
                return parameterElement.GetString();
            }
        }
        catch (JsonException)
        {
            // Compatible proxies frequently return plain text. Inspect bounded markers below.
        }

        return null;
    }

    private static bool IsTokenLimitParameter(string parameter)
        => parameter.Equals("max_completion_tokens", StringComparison.OrdinalIgnoreCase)
           || parameter.Equals("max_tokens", StringComparison.OrdinalIgnoreCase)
           || parameter.Equals("max_output_tokens", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsTokenLimitMarker(string body)
        => body.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase)
           || body.Contains("max completion tokens", StringComparison.OrdinalIgnoreCase)
           || body.Contains("max_tokens", StringComparison.OrdinalIgnoreCase)
           || body.Contains("max_output_tokens", StringComparison.OrdinalIgnoreCase)
           || body.Contains("maximum output tokens", StringComparison.OrdinalIgnoreCase);

    private static bool ExplicitlyReportsDifferentChatFailure(string body)
        => body.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
           || body.Contains("response_format", StringComparison.OrdinalIgnoreCase)
           || body.Contains("temperature", StringComparison.OrdinalIgnoreCase)
           || body.Contains("tool_choice", StringComparison.OrdinalIgnoreCase)
           || body.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
           || body.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
           || body.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
           || body.Contains("authentication", StringComparison.OrdinalIgnoreCase)
           || body.Contains("insufficient quota", StringComparison.OrdinalIgnoreCase)
           || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
           || body.Contains("model not found", StringComparison.OrdinalIgnoreCase)
           || body.Contains("model does not exist", StringComparison.OrdinalIgnoreCase)
           || body.Contains("requested model", StringComparison.OrdinalIgnoreCase);

    private static bool ReportsRequestSpecificProviderFailure(
        string body,
        bool allowBackgroundParameter = false,
        bool allowRouteParameter = false)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var error = json.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement
                : json.RootElement;
            if (error.ValueKind == JsonValueKind.Object)
            {
                var parameter = error.TryGetProperty("param", out var paramElement)
                    ? paramElement.GetString()
                    : null;
                var isAllowedBackgroundParameter = allowBackgroundParameter
                                                   && string.Equals(
                                                       parameter,
                                                       "background",
                                                       StringComparison.OrdinalIgnoreCase);
                var isAllowedRouteParameter = allowRouteParameter
                                              && (string.Equals(parameter, "route", StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(parameter, "endpoint", StringComparison.OrdinalIgnoreCase)
                                                  || string.Equals(parameter, "path", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(parameter)
                    && !isAllowedBackgroundParameter
                    && !isAllowedRouteParameter)
                {
                    return true;
                }

                var code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
                var type = error.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                var allowInvalidRequest = isAllowedBackgroundParameter || isAllowedRouteParameter;
                if (ContainsRequestSpecificErrorMarker(code, allowInvalidRequest)
                    || ContainsRequestSpecificErrorMarker(type, allowInvalidRequest))
                    return true;
            }
        }
        catch (JsonException)
        {
            // Compatible proxies frequently return plain text. Inspect bounded markers below.
        }

        return body.Contains("model not found", StringComparison.OrdinalIgnoreCase)
               || body.Contains("model does not exist", StringComparison.OrdinalIgnoreCase)
               || body.Contains("this model", StringComparison.OrdinalIgnoreCase)
               || body.Contains("requested model", StringComparison.OrdinalIgnoreCase)
               || body.Contains("model", StringComparison.OrdinalIgnoreCase)
               || body.Contains("malformed", StringComparison.OrdinalIgnoreCase)
               || body.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
               || body.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
               || body.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
               || body.Contains("authentication", StringComparison.OrdinalIgnoreCase)
               || body.Contains("insufficient quota", StringComparison.OrdinalIgnoreCase)
               || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsRequestSpecificErrorMarker(string? value, bool allowInvalidRequest = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("model", StringComparison.OrdinalIgnoreCase)
               || value.Contains("auth", StringComparison.OrdinalIgnoreCase)
               || value.Contains("permission", StringComparison.OrdinalIgnoreCase)
               || value.Contains("quota", StringComparison.OrdinalIgnoreCase)
               || value.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
               || (!allowInvalidRequest
                   && value.Contains("invalid_request", StringComparison.OrdinalIgnoreCase));
    }

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
