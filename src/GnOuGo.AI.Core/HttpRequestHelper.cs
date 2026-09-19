using System.Diagnostics;
using System.Security.Cryptography;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
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
        var context = template.Method == HttpMethod.Get ? null : LLMHttpRetryContext.Current;
        var safeToRepeat = template.Method == HttpMethod.Get || context is not null;
        var payload = template.Content is null ? "" : await template.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template.Method + "\n" + template.RequestUri + "\n" + payload)));
        var state = context is null ? new LLMHttpRetryState { Fingerprint = fingerprint }
            : await context.Journal.LoadAsync(ct).ConfigureAwait(false) ?? new LLMHttpRetryState { Fingerprint = fingerprint };
        if (state.Attempts.Count == 0 && state.Fingerprint.Length == 0) state.Fingerprint = fingerprint;
        if (state.Fingerprint != fingerprint) throw new InvalidOperationException("The reserved HTTP operation changed.");
        if (context is not null) context.State = state;
        Exception? originalFailure = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var previous = state.Attempts.LastOrDefault();
            if (previous is not null)
            {
                if (previous.Failure == "cancelled") throw new OperationCanceledException("The reserved attempt was cancelled; it cannot be retried.", ct);
                var response = previous.Status is { } code ? Restore(previous, code) : null;
                var classification = response is null ? null : LLMProviderFailureClassifier.ClassifyResponse(response.StatusCode, previous.Body ?? "");
                var uncertain = response is null;
                var retryable = response is not null
                    ? IsRetryableStatus(response.StatusCode) && classification!.Kind is not (LLMProviderFailureKind.QuotaOrBilling or LLMProviderFailureKind.Authentication or LLMProviderFailureKind.Authorization)
                    : safeToRepeat && previous.Failure is null or "transport" or "timeout";
                var exhausted = state.Attempts.Count >= retryPolicy.MaxAttempts || uncertain &&
                    state.Attempts.Count(a => a.Status is null) > retryPolicy.MaxUncertainRetries;
                var retryAfter = response is not null && retryPolicy.HonorRetryAfter ? ParseRetryAfter(response, utcNow()) : null;
                var delay = !retryable || exhausted || response?.IsSuccessStatusCode == true ? TimeSpan.Zero : previous.NotBefore is { } due ? due - utcNow() : retryAfter ?? CalculateJitterDelay(retryPolicy, state.Attempts.Count, jitter);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                exhausted |= previous.NotBefore is null && state.DelayMilliseconds + delay.TotalMilliseconds > retryPolicy.MaxTotalDelayMilliseconds;
                var metadata = new LLMHttpRetryMetadata(state.Attempts.Count, retryable && exhausted, ToMilliseconds(retryAfter),
                    classification?.Kind ?? (previous.Failure == "permanent" ? LLMProviderFailureKind.Unknown : previous.Failure == "timeout" ? LLMProviderFailureKind.Timeout : LLMProviderFailureKind.Transport), classification?.SafeProviderCode);
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
            var attempt = new LLMHttpAttempt { Id = (context?.RequestId ?? "http") + ":" + Guid.NewGuid().ToString("N") };
            state.Attempts.Add(attempt);
            // Hosts reserve possible usage atomically with this identity before dispatch.
            await SaveAsync(ct).ConfigureAwait(false);
            using var request = requestFactory();
            request.Headers.Remove("X-Client-Request-Id");
            request.Headers.TryAddWithoutValidation("X-Client-Request-Id", attempt.Id);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(retryPolicy.AttemptTimeoutMilliseconds);
            HttpResponseMessage? received = null;
            try
            {
                received = await http.SendAsync(request, completionOption, timeout.Token).ConfigureAwait(false);
                // Buffer under the attempt deadline: a truncated/timed-out body is also uncertain.
                attempt.Body = await received.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                attempt.ContentType = received.Content.Headers.ContentType?.ToString();
                attempt.RetryAfter = received.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
                attempt.Status = (int)received.StatusCode;
            }
            catch (OperationCanceledException ex)
            {
                originalFailure = ex;
                attempt.Failure = ct.IsCancellationRequested ? "cancelled" : "timeout";
            }
            catch (TimeoutException ex) { originalFailure = ex; attempt.Failure = "timeout"; }
            catch (HttpRequestException ex)
            {
                originalFailure = ex;
                attempt.Failure = IsTransientTransport(ex) ? "transport" : "permanent";
            }
            finally { received?.Dispose(); }
            // A persistence failure is not a transport failure and must never trigger a resend.
            await SaveAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        Task SaveAsync(CancellationToken token) => context?.Journal.SaveAsync(state, token) ?? Task.CompletedTask;
    }

    private static bool IsTransientTransport(HttpRequestException failure)
        => failure.StatusCode is null && failure.HttpRequestError is HttpRequestError.Unknown
            or HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
            or HttpRequestError.HttpProtocolError or HttpRequestError.ResponseEnded;

    private static HttpResponseMessage Restore(LLMHttpAttempt attempt, int status)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(attempt.Body ?? "", Encoding.UTF8) };
        if (MediaTypeHeaderValue.TryParse(attempt.ContentType, out var contentType)) response.Content.Headers.ContentType = contentType;
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

    private static bool IsRetryableStatus(HttpStatusCode statusCode)
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
                return null;
            }
        }

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
