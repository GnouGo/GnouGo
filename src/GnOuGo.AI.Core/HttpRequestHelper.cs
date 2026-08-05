using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace GnOuGo.AI.Core;

/// <summary>
/// Reusable helpers for building and sending HTTP requests to AI APIs.
/// </summary>
public static class HttpRequestHelper
{
    internal const int ServerErrorRetryCount = 2;
    internal static readonly TimeSpan ServerErrorInitialRetryDelay = TimeSpan.FromMilliseconds(250);

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

    /// <summary>Reads the response body as a string (for error reporting).</summary>
    public static async Task<string> ReadErrorBodyAsync(HttpResponseMessage resp, CancellationToken ct = default)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Sends a freshly-created request and retries HTTP 5xx responses twice with
    /// exponential backoff. A request factory is required because an
    /// <see cref="HttpRequestMessage"/> cannot be sent more than once.
    /// </summary>
    internal static Task<HttpResponseMessage> SendWithServerErrorRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        ILogger logger,
        string operationName,
        CancellationToken ct)
        => SendWithServerErrorRetryAsync(
            http,
            requestFactory,
            completionOption,
            logger,
            operationName,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            ct);

    internal static async Task<HttpResponseMessage> SendWithServerErrorRetryAsync(
        HttpClient http,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        ILogger logger,
        string operationName,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(delayAsync);

        for (var retry = 0; ; retry++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = requestFactory()
                ?? throw new InvalidOperationException("The HTTP request factory returned null.");
            var response = await http.SendAsync(request, completionOption, ct);

            if (!IsServerError(response.StatusCode) || retry >= ServerErrorRetryCount)
                return response;

            var delay = TimeSpan.FromMilliseconds(
                ServerErrorInitialRetryDelay.TotalMilliseconds * Math.Pow(2, retry));

            logger.LogWarning(
                "Transient HTTP server error during {OperationName}. StatusCode={StatusCode}; Retry={RetryAttempt}/{RetryCount}; BackoffMs={BackoffMs}",
                operationName,
                (int)response.StatusCode,
                retry + 1,
                ServerErrorRetryCount,
                delay.TotalMilliseconds);

            response.Dispose();
            await delayAsync(delay, ct);
        }
    }

    private static bool IsServerError(System.Net.HttpStatusCode statusCode)
        => (int)statusCode is >= 500 and <= 599;
}
