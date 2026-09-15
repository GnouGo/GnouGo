using System.Diagnostics;
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

        var totalStopwatch = Stopwatch.StartNew();
        var cumulativeDelay = TimeSpan.Zero;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = requestFactory()
                ?? throw new InvalidOperationException("The HTTP request factory returned null.");
            var attemptStopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, completionOption, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(
                    "HTTP transport failure during {OperationName}. FailureType={FailureType}; Attempt={Attempt}; AttemptDurationMs={AttemptDurationMs}; ElapsedMs={ElapsedMs}",
                    operationName,
                    ex.GetType().Name,
                    attempt,
                    attemptStopwatch.Elapsed.TotalMilliseconds,
                    totalStopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(
                    "HTTP timeout during {OperationName}. FailureType={FailureType}; Attempt={Attempt}; AttemptDurationMs={AttemptDurationMs}; ElapsedMs={ElapsedMs}",
                    operationName,
                    ex.GetType().Name,
                    attempt,
                    attemptStopwatch.Elapsed.TotalMilliseconds,
                    totalStopwatch.Elapsed.TotalMilliseconds);
                throw new TimeoutException(
                    $"{operationName} timed out after {http.Timeout.TotalSeconds:0.###} seconds.",
                    ex);
            }

            if (response.IsSuccessStatusCode)
            {
                SetRetryMetadata(response, new LLMHttpRetryMetadata(attempt, false, null, null, null));
                return response;
            }

            var errorBody = await ReadAndRestoreErrorBodyAsync(response, ct).ConfigureAwait(false);
            var classified = LLMProviderFailureClassifier.ClassifyResponse(response.StatusCode, errorBody);
            var statusIsRetryable = IsRetryableStatus(response.StatusCode);
            var terminalProviderFailure = classified.Kind is LLMProviderFailureKind.QuotaOrBilling
                or LLMProviderFailureKind.Authentication
                or LLMProviderFailureKind.Authorization;
            var retryable = statusIsRetryable && !terminalProviderFailure;
            var retryAfter = retryPolicy.HonorRetryAfter
                ? ParseRetryAfter(response, utcNow())
                : null;

            if (!retryable)
            {
                SetRetryMetadata(response, new LLMHttpRetryMetadata(
                    attempt,
                    false,
                    ToMilliseconds(retryAfter),
                    classified.Kind,
                    classified.SafeProviderCode));
                return response;
            }

            var exhaustedAttempts = attempt >= retryPolicy.MaxAttempts;
            var delay = retryAfter ?? CalculateJitterDelay(retryPolicy, attempt, jitter);
            var maxTotalDelay = TimeSpan.FromMilliseconds(retryPolicy.MaxTotalDelayMilliseconds);
            var exhaustedDelayBudget = delay < TimeSpan.Zero || cumulativeDelay + delay > maxTotalDelay;
            if (exhaustedAttempts || exhaustedDelayBudget)
            {
                SetRetryMetadata(response, new LLMHttpRetryMetadata(
                    attempt,
                    true,
                    ToMilliseconds(retryAfter),
                    classified.Kind,
                    classified.SafeProviderCode));
                logger.LogError(
                    "Transient HTTP recovery exhausted during {OperationName}. StatusCode={StatusCode}; AttemptCount={AttemptCount}; RetryExhausted={RetryExhausted}; RetryAfterMs={RetryAfterMs}; ElapsedMs={ElapsedMs}",
                    operationName,
                    (int)response.StatusCode,
                    attempt,
                    true,
                    ToMilliseconds(retryAfter),
                    totalStopwatch.Elapsed.TotalMilliseconds);
                return response;
            }

            logger.LogWarning(
                "Transient HTTP response during {OperationName}. StatusCode={StatusCode}; Attempt={Attempt}/{MaxAttempts}; BackoffMs={BackoffMs}; RetryAfterAccepted={RetryAfterAccepted}; ElapsedMs={ElapsedMs}",
                operationName,
                (int)response.StatusCode,
                attempt,
                retryPolicy.MaxAttempts,
                delay.TotalMilliseconds,
                retryAfter != null,
                totalStopwatch.Elapsed.TotalMilliseconds);

            response.Dispose();
            await delayAsync(delay, ct).ConfigureAwait(false);
            cumulativeDelay += delay;
        }
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

    internal static LLMHttpRetryMetadata? GetRetryMetadata(HttpRequestException exception)
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

    private static async Task<string> ReadAndRestoreErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.ToString();
        var body = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
        response.Content.Dispose();
        response.Content = new StringContent(body, Encoding.UTF8);
        if (MediaTypeHeaderValue.TryParse(contentType, out var parsedContentType))
            response.Content.Headers.ContentType = parsedContentType;
        return body;
    }
}

internal sealed record LLMHttpRetryMetadata(
    int AttemptCount,
    bool RetryExhausted,
    int? RetryAfterMilliseconds,
    LLMProviderFailureKind? FailureKind,
    string? SafeProviderCode);
