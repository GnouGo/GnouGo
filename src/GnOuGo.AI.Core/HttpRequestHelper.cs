using System.Security.Cryptography;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace GnOuGo.AI.Core;

/// <summary>
/// Reusable helpers for building and sending HTTP requests to AI APIs.
/// </summary>
public static class HttpRequestHelper
{
    private const int MaxErrorBodyCharacters = 64 * 1024;
    private const string ExceptionRetryMetadataKey = "gnougo.llm.retry_metadata";
    private static readonly ConditionalWeakTable<HttpResponseMessage, LLMHttpRetryMetadata> RetryMetadata = new();
    private static readonly HttpRequestOptionsKey<byte[]> LegacyReceiptBody = new("gnougo.llm.legacy_receipt_body");

    // Only providers whose older builders omitted an output bound may supply the exact old body.
    // This permits receipt-only recovery; it never authorizes sending that older unbounded body.
    internal static void AllowLegacyReceipt(HttpRequestMessage request, byte[] exactOlderBody)
        => request.Options.Set(LegacyReceiptBody, exactOlderBody);

    /// <summary>Creates a GET request.</summary>
    public static HttpRequestMessage CreateGet(string url)
        => new(HttpMethod.Get, url);

    /// <summary>Creates a POST request with JSON payload.</summary>
    public static HttpRequestMessage CreateJsonPost(string url, byte[] jsonPayload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new ByteArrayContent(jsonPayload);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return req;
    }

    /// <summary>Sets the Bearer authorization header on the request.</summary>
    public static void SetBearerAuth(HttpRequestMessage req, string apiKey)
        => req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

    /// <summary>Reads a bounded response body as a string for safe error classification.</summary>
    public static async Task<string> ReadErrorBodyAsync(HttpResponseMessage resp, CancellationToken ct = default)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var buffer = new char[4_096];
        var builder = new StringBuilder();
        while (builder.Length < MaxErrorBodyCharacters)
        {
            var remaining = Math.Min(buffer.Length, MaxErrorBodyCharacters - builder.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, remaining), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            builder.Append(buffer, 0, read);
        }

        return builder.ToString();
    }

    public static Task<HttpResponseMessage> SendGenerationAsync(HttpClient http, Func<HttpRequestMessage> requestFactory,
        LLMProviderRetryPolicyOptions retryPolicy, CancellationToken ct = default)
        => SendWithServerErrorRetryAsync(http, requestFactory, HttpCompletionOption.ResponseHeadersRead,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "native model generation", retryPolicy, ct);

    internal static Task<HttpResponseMessage> SendWithServerErrorRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        ILogger logger,
        string operationName,
        CancellationToken ct)
        => SendWithTransientRetryAsync(
            http,
            requestFactory,
            completionOption,
            logger,
            operationName,
            new LLMProviderRetryPolicyOptions(),
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            static upperBound => TimeSpan.FromMilliseconds(Random.Shared.NextInt64(0, (long)upperBound.TotalMilliseconds + 1)),
            static () => DateTimeOffset.UtcNow,
            ct);

    internal static Task<HttpResponseMessage> SendWithServerErrorRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        ILogger logger,
        string operationName,
        LLMProviderRetryPolicyOptions retryPolicy,
        CancellationToken ct)
        => SendWithTransientRetryAsync(
            http,
            requestFactory,
            completionOption,
            logger,
            operationName,
            retryPolicy,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            static upperBound => TimeSpan.FromMilliseconds(Random.Shared.NextInt64(0, (long)upperBound.TotalMilliseconds + 1)),
            static () => DateTimeOffset.UtcNow,
            ct);

    // Deterministic seam retained for focused tests.
    internal static Task<HttpResponseMessage> SendWithServerErrorRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        ILogger logger,
        string operationName,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken ct)
        => SendWithTransientRetryAsync(
            http,
            requestFactory,
            completionOption,
            logger,
            operationName,
            new LLMProviderRetryPolicyOptions(),
            delayAsync,
            static upperBound => upperBound,
            static () => DateTimeOffset.UtcNow,
            ct);

    internal static Task<HttpResponseMessage> SendWithTransientRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        ILogger logger,
        string operationName,
        LLMProviderRetryPolicyOptions retryPolicy,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<TimeSpan, TimeSpan> jitter,
        Func<DateTimeOffset> utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(retryPolicy);
        return SendCoreAsync(
            http,
            requestFactory,
            completionOption,
            logger,
            operationName,
            retryPolicy,
            delayAsync,
            jitter,
            utcNow,
            ct);
    }

    private static async Task<HttpResponseMessage> SendCoreAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        ILogger logger,
        string operationName,
        LLMProviderRetryPolicyOptions retryPolicy,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<TimeSpan, TimeSpan> jitter,
        Func<DateTimeOffset> utcNow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(delayAsync);
        ArgumentNullException.ThrowIfNull(jitter);
        ArgumentNullException.ThrowIfNull(utcNow);

        if (retryPolicy.MaxAttempts < 1 || retryPolicy.MaxUncertainRetries < 0 || retryPolicy.AttemptTimeoutMilliseconds <= 0)
            throw new ArgumentException("Invalid HTTP retry limits.");
        using var template = requestFactory();
        template.Options.TryGetValue(LLMTransportResolver.SelectionKey, out var transport);
        var streaming = transport?.IsStreaming == true;
        var context = template.Method == HttpMethod.Get ? null : LLMHttpRetryContext.Current;
        var confirmedOnly = context?.PolicyOverride is not null;
        if (context?.PolicyOverride is { } capturedPolicy)
        {
            var attemptLimit = retryPolicy.MaxAttempts;
            retryPolicy = capturedPolicy.Clone();
            // An explicit single-attempt request remains stricter than the captured allowance.
            retryPolicy.MaxAttempts = Math.Min(attemptLimit, retryPolicy.MaxAttempts);
        }
        var safeToRepeat = template.Method == HttpMethod.Get || context is not null;
        var payload = template.Content is null ? "" : await template.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template.Method + "\n" + template.RequestUri + "\n" + payload)));
        var state = context is null ? new LLMHttpRetryState { Fingerprint = fingerprint }
            : await context.LoadAsync(ct).ConfigureAwait(false) ?? new LLMHttpRetryState { Fingerprint = fingerprint };
        if (state.Attempts.Count == 0 && state.Fingerprint.Length == 0) state.Fingerprint = fingerprint;
        if (state.Fingerprint != fingerprint)
        {
            if (!streaming && state.Transport is null && template.Options.TryGetValue(LegacyReceiptBody, out var olderBody) &&
                state.Fingerprint == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template.Method + "\n" + template.RequestUri + "\n" + Encoding.UTF8.GetString(olderBody)))) &&
                state.Attempts.LastOrDefault() is { Status: >= 200 and < 300, Failure: null, Body: not null } completed &&
                completed.ResponseBodyComplete != false && completed.PartialBody is null && completed.PartialBodyBase64 is null)
            {
                var retained = Restore(completed, completed.Status.Value);
                SetRetryMetadata(retained, new(state.Attempts.Count, false, null, null, null));
                return retained; // No journal rewrite, dispatch, retry or settlement here.
            }
            throw new InvalidOperationException("The reserved HTTP operation changed.");
        }
        if (state.Attempts.Count == 0)
        {
            state.RequestMethod = template.Method.Method;
            // Authentication headers, URL credentials and query strings never enter inspection evidence.
            state.Endpoint = template.RequestUri?.GetLeftPart(UriPartial.Path);
            if (template.RequestUri is { } endpoint && !string.IsNullOrEmpty(endpoint.UserInfo))
                state.Endpoint = new UriBuilder(endpoint) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path);
            state.RequestBody = payload;
            state.Transport = transport;
        }
        var backgroundStreaming = streaming && transport!.Protocol == LLMTransportResolver.Responses &&
            JsonNode.Parse(payload)?["background"]?.GetValue<bool>() == true;
        Exception? originalFailure = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var previous = state.Attempts.LastOrDefault();
            if (previous is not null)
            {
                if (backgroundStreaming && previous.ProviderResponseId is not null && previous.ProtocolComplete != true && previous.Failure != "cancelled")
                    await RecoverBackgroundResponseAsync(http, template, previous, retryPolicy, () => SaveAsync(CancellationToken.None), delayAsync, utcNow, originalFailure is null, ct).ConfigureAwait(false);
                if (previous.Failure == "cancelled") throw new OperationCanceledException("The reserved attempt was cancelled; it cannot be retried.", ct);
                if (confirmedOnly && previous.Status is not null && (previous.Body is null || previous.Failure is not null))
                    throw new InvalidOperationException("A confirmed HTTP response requires a complete durably recorded body and outcome.");
                if (streaming && previous.ObservedStatus is >= 200 and < 300 &&
                    (previous.ProtocolComplete != true || previous.AssembledBody is null))
                {
                    var incomplete = new HttpRequestException("The native stream is incomplete; exact receipt recovery is required before independent generation.", originalFailure);
                    incomplete.Data[ExceptionRetryMetadataKey] = new LLMHttpRetryMetadata(state.Attempts.Count, false, null,
                        previous.Failure == "timeout" ? LLMProviderFailureKind.Timeout : LLMProviderFailureKind.Transport, previous.ProviderErrorCode);
                    throw incomplete;
                }
                var response = previous.Status is { } code ? Restore(previous, code) : null;
                var classification = response is null ? null : LLMProviderFailureClassifier.ClassifyResponse(response.StatusCode, previous.Body ?? "", confirmedOnly);
                var uncertain = response is null;
                var retryAfter = response is not null ? ParseRetryAfter(response, utcNow()) : null;
                var retryable = response is not null
                    ? safeToRepeat && (confirmedOnly ? ConfirmedTransient(response.StatusCode, classification!, retryAfter)
                        : IsRetryableStatus(response.StatusCode) && classification!.Kind is not (LLMProviderFailureKind.QuotaOrBilling or LLMProviderFailureKind.Authentication or LLMProviderFailureKind.Authorization))
                    : !streaming && !confirmedOnly && safeToRepeat && previous.Failure is null or "transport" or "timeout";
                var exhausted = state.Attempts.Count >= retryPolicy.MaxAttempts || uncertain &&
                    state.Attempts.Count(a => a.Status is null) > retryPolicy.MaxUncertainRetries;
                var delay = !retryable || exhausted || response?.IsSuccessStatusCode == true ? TimeSpan.Zero : previous.NotBefore is { } due ? due - utcNow() : retryAfter ?? CalculateJitterDelay(retryPolicy, state.Attempts.Count, jitter);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                exhausted |= delay.TotalMilliseconds > retryPolicy.MaxTotalDelayMilliseconds ||
                    previous.NotBefore is null && state.DelayMilliseconds + delay.TotalMilliseconds > retryPolicy.MaxTotalDelayMilliseconds;
                var metadata = new LLMHttpRetryMetadata(state.Attempts.Count, retryable && exhausted, ToMilliseconds(retryAfter),
                    classification?.Kind ?? (previous.Failure == "permanent" ? LLMProviderFailureKind.Unknown : previous.Failure == "timeout" ? LLMProviderFailureKind.Timeout : LLMProviderFailureKind.Transport), classification?.SafeProviderCode);
                var decision = response?.IsSuccessStatusCode == true ? "completed; no retry" : uncertain && confirmedOnly ? "blocked; uncertain outcome" :
                    !retryable ? "blocked; nonretryable outcome" : exhausted ? "blocked; captured retry allowance exhausted" : "retry; captured transient-response policy";
                if (previous.RetryDecision != decision)
                { previous.RetryDecision = decision; await SaveAsync(ct).ConfigureAwait(false); }
                if (response is not null && (response.IsSuccessStatusCode || !retryable || exhausted))
                {
                    SetRetryMetadata(response, metadata);
                    if (exhausted && retryable) logger.LogError("Transient HTTP recovery exhausted during {OperationName}. StatusCode={StatusCode}; AttemptCount={AttemptCount}; RetryExhausted={RetryExhausted}; ElapsedMs={ElapsedMs}",
                        operationName, previous.Status, state.Attempts.Count, true, state.DelayMilliseconds);
                    return response;
                }
                response?.Dispose();
                if (!retryable || exhausted)
                {
                    if (previous.Failure == "cancelled") throw new OperationCanceledException("The reserved attempt was cancelled; it cannot be retried.", ct);
                    Exception failure = previous.Failure == "timeout"
                        ? new TimeoutException($"{operationName} timed out after {Math.Min(http.Timeout == Timeout.InfiniteTimeSpan ? double.MaxValue : http.Timeout.TotalMilliseconds, retryPolicy.AttemptTimeoutMilliseconds) / 1000:0.###} seconds.", originalFailure)
                        : new HttpRequestException("HTTP transport recovery stopped.", originalFailure);
                    failure.Data[ExceptionRetryMetadataKey] = metadata;
                    throw failure;
                }
                if (previous.NotBefore is null)
                {
                    previous.NotBefore = utcNow() + delay;
                    state.DelayMilliseconds += delay.TotalMilliseconds;
                    await SaveAsync(ct).ConfigureAwait(false);
                }
                logger.LogWarning("Retrying transient HTTP failure during {OperationName}. Attempt={Attempt}; BackoffMs={BackoffMs}; Uncertain={Uncertain}", operationName, state.Attempts.Count, delay.TotalMilliseconds, uncertain);
                try { await delayAsync(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    previous.Failure = "cancelled";
                    await SaveAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }

            ct.ThrowIfCancellationRequested();
            var attempt = new LLMHttpAttempt { Id = (context?.RequestId ?? "http") + ":" + Guid.NewGuid().ToString("N"), StartedAtUtc = utcNow() };
            state.Attempts.Add(attempt);
            // Hosts reserve possible usage atomically with this identity before dispatch.
            await SaveAsync(ct).ConfigureAwait(false);
            using var request = requestFactory();
            request.Headers.Remove("X-Client-Request-Id");
            request.Headers.TryAddWithoutValidation("X-Client-Request-Id", attempt.Id);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // ResponseHeadersRead ends HttpClient's own timeout at the headers. Retain
            // its stricter caller deadline throughout body consumption as well.
            timeout.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromMilliseconds(retryPolicy.AttemptTimeoutMilliseconds)
                : TimeSpan.FromMilliseconds(Math.Min(http.Timeout.TotalMilliseconds, retryPolicy.AttemptTimeoutMilliseconds)));
            HttpResponseMessage? received = null;
            var partial = new StringBuilder();
            CapturedResponseStream? capturedStream = null;
            try
            {
                received = await http.SendAsync(request, context is null && !streaming ? completionOption : HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                attempt.ObservedStatus = (int)received.StatusCode;
                attempt.ContentType = received.Content.Headers.ContentType?.ToString();
                if (streaming && received.IsSuccessStatusCode)
                {
                    capturedStream = new CapturedResponseStream(
                        await received.Content.ReadAsStreamAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false),
                        attempt, token => SaveAsync(token), utcNow);
                    var media = received.Content.Headers.ContentType?.MediaType;
                    if (transport!.Protocol == LLMTransportResolver.OllamaChat
                        ? media is not ("application/x-ndjson" or "application/ndjson" or "application/json")
                        : media != "text/event-stream")
                    {
                        // Preserve a returned rejection/JSON envelope without silently treating it as streaming success.
                        var rejected = new byte[8192];
                        while (await capturedStream.ReadAsync(rejected, timeout.Token).ConfigureAwait(false) > 0) { }
                        attempt.TerminationStatus = "unexpected_content_type";
                        throw new InvalidDataException("The configured provider did not return the requested native streaming protocol.");
                    }
                    var assembled = await NativeStreamingResponse.ReadAsync(capturedStream, transport.Protocol, attempt,
                        () => capturedStream.CheckpointAsync(timeout.Token), timeout.Token).ConfigureAwait(false);
                    timeout.Token.ThrowIfCancellationRequested();
                    attempt.AssembledBody = JsonSerializer.Serialize(assembled, StreamingJsonContext.Default.JsonObject);
                    attempt.Body = capturedStream.CompleteText();
                    attempt.PartialBody = null;
                    attempt.PartialBodyBase64 = null;
                    attempt.ProtocolComplete = true;
                    attempt.ResponseBodyComplete = true;
                }
                else
                {
                    using var bodyReader = new StreamReader(await received.Content.ReadAsStreamAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false));
                    var buffer = new char[4096];
                    int read;
                    while ((read = await bodyReader.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false)) > 0) partial.Append(buffer, 0, read);
                    attempt.Body = partial.ToString();
                    attempt.ResponseBodyComplete = true;
                }
                attempt.RetryAfter = received.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
                attempt.Status = (int)received.StatusCode;
            }
            catch (OperationCanceledException ex)
            {
                originalFailure = ex;
                attempt.Failure = ct.IsCancellationRequested ? "cancelled" : "timeout";
            }
            catch (TimeoutException ex) { originalFailure = ex; attempt.Failure = ct.IsCancellationRequested ? "cancelled" : "timeout"; }
            catch (Exception ex) when (streaming && ex is IOException or InvalidDataException or JsonException)
            {
                originalFailure = ex;
                attempt.Failure = ct.IsCancellationRequested ? "cancelled" : "transport";
            }
            catch (HttpRequestException ex)
            {
                originalFailure = ex;
                attempt.Failure = ct.IsCancellationRequested ? "cancelled" : IsTransientTransport(ex) ? "transport" : "permanent";
            }
            catch (Exception ex) { originalFailure = ex; attempt.Failure = ct.IsCancellationRequested ? "cancelled" : streaming && attempt.ObservedStatus is >= 200 and < 300 ? "transport" : "permanent"; }
            finally
            {
                attempt.CompletedAtUtc = utcNow();
                if (streaming)
                {
                    System.Diagnostics.Activity.Current?.SetTag("gnougo.llm.stream.protocol_complete", attempt.ProtocolComplete == true);
                    System.Diagnostics.Activity.Current?.SetTag("gnougo.llm.stream.termination", attempt.TerminationStatus);
                    System.Diagnostics.Activity.Current?.SetTag("gnougo.llm.stream.usage_known", attempt.UsageKnown);
                }
                if (streaming && attempt.ProtocolComplete != true && attempt.ObservedStatus is >= 200 and < 300)
                    attempt.RetryDecision = "blocked; incomplete native stream; exact receipt recovery required";
                if (ct.IsCancellationRequested) attempt.RetryDecision = "cancelled; no retry";
                if (attempt.Body is null)
                {
                    attempt.ResponseBodyComplete = false;
                    if (capturedStream is not null) capturedStream.Snapshot();
                    else attempt.PartialBody = partial.Length == 0 ? null : partial.ToString();
                }
                capturedStream?.Dispose();
                received?.Dispose();
                System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("gnougo.llm.transport.attempt",
                    tags: new System.Diagnostics.ActivityTagsCollection {
                        ["gnougo.llm.transport.attempt"] = state.Attempts.Count,
                        ["gnougo.llm.transport.request_id"] = attempt.Id,
                        ["http.request.method"] = request.Method.Method,
                        ["http.response.status_code"] = attempt.Status,
                        ["error.type"] = attempt.Failure }));
            }
            // A persistence failure is not a transport failure and must never trigger a resend.
            await SaveAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        Task SaveAsync(CancellationToken token) => context?.SaveAsync(state, token) ?? Task.CompletedTask;
    }

    // Receipt retrieval continues the same response id. It never creates or reserves another generation.
    private static async Task RecoverBackgroundResponseAsync(HttpClient http, HttpRequestMessage original, LLMHttpAttempt attempt,
        LLMProviderRetryPolicyOptions policy, Func<Task> save, Func<TimeSpan, CancellationToken, Task> delay,
        Func<DateTimeOffset> utcNow, bool allowExpiredReceipt, CancellationToken ct)
    {
        var endpoint = new UriBuilder(original.RequestUri!) { Path = original.RequestUri!.AbsolutePath.TrimEnd('/') + "/" + Uri.EscapeDataString(attempt.ProviderResponseId!) };
        attempt.ReceiptRetrievals ??= [];
        attempt.StreamFailure ??= attempt.Failure;
        var deadline = (attempt.StartedAtUtc ?? utcNow()).AddMilliseconds(policy.AttemptTimeoutMilliseconds);
        // Historical exact retrieval remains read-only; it cannot finance another POST.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = deadline - utcNow();
        if (remaining <= TimeSpan.Zero && !allowExpiredReceipt) throw new TimeoutException("The captured generation deadline expired before receipt recovery.");
        timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(30));
        if (attempt.ReceiptRetrievals.Any(r => r.Status is >= 400 && !IsRetryableStatus((HttpStatusCode)r.Status.Value)))
            throw new HttpRequestException("The recorded non-transient receipt retrieval cannot be retried.");
        var token = timeout.Token;
        while (attempt.ReceiptRetrievals.Count < 128 && attempt.ReceiptRetrievals.Count(r => r.Failure is not null || r.Status is >= 400) < policy.MaxAttempts)
        {
            token.ThrowIfCancellationRequested();
            var retrieval = new LLMHttpReceiptRetrieval { StartedAtUtc = utcNow() };
            attempt.ReceiptRetrievals.Add(retrieval);
            await save().ConfigureAwait(false);
            try
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, endpoint.Uri);
                foreach (var header in original.Headers)
                    if (header.Key is not ("Accept" or "X-Client-Request-Id")) get.Headers.TryAddWithoutValidation(header.Key, header.Value);
                using var response = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                retrieval.Status = (int)response.StatusCode;
                // Bounded full receipt retrieval; never interpret connection EOF as native stream completion.
                using var body = new StreamReader(await response.Content.ReadAsStreamAsync(token).WaitAsync(token).ConfigureAwait(false));
                var text = new StringBuilder(); var buffer = new char[4096]; int count;
                while ((count = await body.ReadAsync(buffer.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false)) > 0)
                {
                    if (text.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("Retrieved response exceeds the size limit.");
                    text.Append(buffer, 0, count);
                }
                retrieval.Body = text.ToString();
                if (!response.IsSuccessStatusCode)
                {
                    if (!IsRetryableStatus(response.StatusCode)) throw new InvalidDataException("The configured Responses route rejected receipt retrieval.");
                }
                else
                {
                    var result = JsonNode.Parse(retrieval.Body) as JsonObject ?? throw new InvalidDataException("Invalid retrieved response.");
                    if (result["id"]?.ToString() != attempt.ProviderResponseId) throw new InvalidDataException("Retrieved response identity mismatch.");
                    var status = result["status"]?.ToString();
                    if (status is "completed" or "incomplete" or "failed")
                    {
                        if (status != "failed" && result["output"] is not JsonArray) throw new InvalidDataException("Retrieved response output is missing.");
                        attempt.Body = retrieval.Body;
                        attempt.AssembledBody = retrieval.Body;
                        attempt.Status = (int)response.StatusCode;
                        attempt.Failure = null;
                        attempt.ProtocolComplete = true;
                        attempt.ResponseBodyComplete = true;
                        attempt.TerminationStatus = status;
                        attempt.UsageKnown = result["usage"]?["input_tokens"] is JsonValue i && i.TryGetValue<long>(out var input) && input >= 0 &&
                            result["usage"]?["output_tokens"] is JsonValue o && o.TryGetValue<long>(out var output) && output >= 0;
                        return;
                    }
                    if (status is not ("queued" or "in_progress")) throw new InvalidDataException("Unexpected retrieved response status.");
                }
            }
            catch (Exception ex)
            {
                retrieval.Failure = ct.IsCancellationRequested ? "cancelled" : ex is OperationCanceledException ? "timeout" : "retrieval failed";
                throw;
            }
            finally
            {
                retrieval.CompletedAtUtc = utcNow();
                await save().ConfigureAwait(false);
            }
            await delay(TimeSpan.FromSeconds(Math.Min(10, Math.Pow(1.5, attempt.ReceiptRetrievals.Count - 1))), token).ConfigureAwait(false);
        }
        throw new HttpRequestException("The captured response receipt retrieval allowance is exhausted; no generation was replayed.");
    }

    private static bool ConfirmedTransient(HttpStatusCode status, LLMHttpFailureClassification classification, TimeSpan? retryAfter)
    {
        if (classification.Kind is LLMProviderFailureKind.QuotaOrBilling or LLMProviderFailureKind.Authentication or
            LLMProviderFailureKind.Authorization or LLMProviderFailureKind.InvalidRequest or LLMProviderFailureKind.ModelUnavailable)
            return false;
        return status is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
            status == HttpStatusCode.TooManyRequests && (classification.SafeProviderCode == "rate_limit_exceeded" || retryAfter is not null);
    }

    private static bool IsTransientTransport(HttpRequestException failure)
        => failure.StatusCode is null && failure.InnerException is not System.Security.Authentication.AuthenticationException &&
            failure.HttpRequestError is HttpRequestError.Unknown or HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                or HttpRequestError.HttpProtocolError or HttpRequestError.ResponseEnded;

    private static HttpResponseMessage Restore(LLMHttpAttempt attempt, int status)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(attempt.AssembledBody ?? attempt.Body ?? "", Encoding.UTF8) };
        if (attempt.AssembledBody is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        else if (MediaTypeHeaderValue.TryParse(attempt.ContentType, out var contentType)) response.Content.Headers.ContentType = contentType;
        if (attempt.RetryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", attempt.RetryAfter);
        return response;
    }

    internal static LLMHttpRetryMetadata? GetRetryMetadata(HttpResponseMessage response)
        => RetryMetadata.TryGetValue(response, out var metadata) ? metadata : null;

    internal static HttpRequestException CreateFailure(
        string safeMessage,
        HttpResponseMessage response,
        Exception? inner = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        return CreateFailure(safeMessage, response.StatusCode, GetRetryMetadata(response), inner);
    }

    internal static HttpRequestException CreateFailure(
        string safeMessage,
        HttpStatusCode statusCode,
        LLMHttpRetryMetadata? retryMetadata,
        Exception? inner = null)
    {
        var exception = new HttpRequestException(safeMessage, inner, statusCode);
        if (retryMetadata != null)
            exception.Data[ExceptionRetryMetadataKey] = retryMetadata;
        return exception;
    }

    internal static LLMHttpRetryMetadata? GetRetryMetadata(Exception exception)
        => exception.Data[ExceptionRetryMetadataKey] as LLMHttpRetryMetadata;

    private static void SetRetryMetadata(HttpResponseMessage response, LLMHttpRetryMetadata metadata)
    {
        RetryMetadata.Remove(response);
        RetryMetadata.Add(response, metadata);
    }

    internal static bool IsRetryableStatus(HttpStatusCode statusCode)
        => statusCode is (HttpStatusCode)425
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan CalculateJitterDelay(
        LLMProviderRetryPolicyOptions retryPolicy,
        int completedAttempt,
        Func<TimeSpan, TimeSpan> jitter)
    {
        var multiplier = Math.Pow(2, Math.Max(0, completedAttempt - 1));
        var upperBoundMilliseconds = Math.Min(
            retryPolicy.MaxDelayMilliseconds,
            retryPolicy.BaseDelayMilliseconds * multiplier);
        var upperBound = TimeSpan.FromMilliseconds(upperBoundMilliseconds);
        var selected = jitter(upperBound);
        if (selected < TimeSpan.Zero)
            return TimeSpan.Zero;
        return selected > upperBound ? upperBound : selected;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;

        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
        {
            try
            {
                return TimeSpan.FromSeconds(seconds);
            }
            catch (OverflowException)
            {
                return TimeSpan.MaxValue; // Honor an unrepresentably long delay by exhausting the budget.
            }
        }
        if (raw.All(char.IsAsciiDigit)) return TimeSpan.MaxValue;

        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var date))
        {
            return null;
        }

        return date <= now ? TimeSpan.Zero : date - now;
    }

    private static int? ToMilliseconds(TimeSpan? delay)
        => delay is null
            ? null
            : (int)Math.Min(int.MaxValue, Math.Max(0, Math.Ceiling(delay.Value.TotalMilliseconds)));


}

internal sealed record LLMHttpRetryMetadata(
    int AttemptCount,
    bool RetryExhausted,
    int? RetryAfterMilliseconds,
    LLMProviderFailureKind? FailureKind,
    string? SafeProviderCode);
