using System.Net;

namespace GnOuGo.AI.Core;

/// <summary>Provider-neutral classification for failures produced by an LLM backend.</summary>
public enum LLMProviderFailureKind
{
    Transport,
    Timeout,
    RateLimited,
    ServiceUnavailable,
    Authentication,
    Authorization,
    QuotaOrBilling,
    InvalidRequest,
    ModelUnavailable,
    Unknown
}

/// <summary>
/// Redacted, typed provider failure. Provider implementations and the routing client must not
/// include response bodies, credentials, or prompts in this exception.
/// </summary>
public sealed class LLMProviderException : Exception
{
    public LLMProviderException(
        LLMProviderFailureKind kind,
        string message,
        bool retryable,
        int? statusCode = null,
        string? safeProviderCode = null)
        : this(
            kind,
            message,
            retryable,
            statusCode,
            safeProviderCode,
            attemptCount: 1,
            retryExhausted: false,
            retryAfterMilliseconds: null)
    {
    }

    public LLMProviderException(
        LLMProviderFailureKind kind,
        string message,
        bool retryable,
        int? statusCode,
        string? safeProviderCode,
        int attemptCount,
        bool retryExhausted,
        int? retryAfterMilliseconds)
        : base(message)
    {
        Kind = kind;
        Retryable = retryable;
        StatusCode = statusCode;
        SafeProviderCode = safeProviderCode;
        AttemptCount = Math.Max(1, attemptCount);
        RetryExhausted = retryExhausted;
        RetryAfterMilliseconds = retryAfterMilliseconds is >= 0 ? retryAfterMilliseconds : null;
    }

    public LLMProviderFailureKind Kind { get; }

    public bool Retryable { get; }

    public int? StatusCode { get; }

    public string? SafeProviderCode { get; }

    public int AttemptCount { get; }

    public bool RetryExhausted { get; }

    public int? RetryAfterMilliseconds { get; }
}

internal sealed record LLMHttpFailureClassification(
    LLMProviderFailureKind Kind,
    bool Retryable,
    string? SafeProviderCode);

internal static partial class LLMProviderFailureClassifier
{
    private static readonly (string Marker, LLMProviderFailureKind Kind, string Code)[] KnownMarkers =
    [
        ("credit_balance_exhausted", LLMProviderFailureKind.QuotaOrBilling, "credit_balance_exhausted"),
        ("insufficient_quota", LLMProviderFailureKind.QuotaOrBilling, "insufficient_quota"),
        ("insufficient quota", LLMProviderFailureKind.QuotaOrBilling, "insufficient_quota"),
        ("billing_hard_limit_reached", LLMProviderFailureKind.QuotaOrBilling, "billing_hard_limit_reached"),
        ("billing_error", LLMProviderFailureKind.QuotaOrBilling, "billing_error"),
        ("billing limit", LLMProviderFailureKind.QuotaOrBilling, "billing_limit"),
        ("payment_required", LLMProviderFailureKind.QuotaOrBilling, "payment_required"),
        ("invalid_api_key", LLMProviderFailureKind.Authentication, "invalid_api_key"),
        ("invalid api key", LLMProviderFailureKind.Authentication, "invalid_api_key"),
        ("authentication_error", LLMProviderFailureKind.Authentication, "authentication_error"),
        ("unauthorized", LLMProviderFailureKind.Authentication, "unauthorized"),
        ("authorization_error", LLMProviderFailureKind.Authorization, "authorization_error"),
        ("access_denied", LLMProviderFailureKind.Authorization, "access_denied"),
        ("forbidden", LLMProviderFailureKind.Authorization, "forbidden"),
        ("permission_denied", LLMProviderFailureKind.Authorization, "permission_denied"),
        ("model_not_found", LLMProviderFailureKind.ModelUnavailable, "model_not_found"),
        ("model not found", LLMProviderFailureKind.ModelUnavailable, "model_not_found"),
        ("rate_limit_exceeded", LLMProviderFailureKind.RateLimited, "rate_limit_exceeded")
    ];

    public static LLMProviderException Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is LLMProviderException typed)
                return typed;

            if (current is TimeoutException
                || current is TaskCanceledException)
            {
                return Create(LLMProviderFailureKind.Timeout, retryable: true);
            }

            if (current is LocalLLMException local)
            {
                return local.Kind switch
                {
                    LocalLLMFailureKind.ModelUnavailable or LocalLLMFailureKind.ModelLoad =>
                        Create(LLMProviderFailureKind.ModelUnavailable, retryable: false),
                    LocalLLMFailureKind.InvalidStructuredOutput =>
                        Create(LLMProviderFailureKind.InvalidRequest, retryable: false),
                    _ => Create(LLMProviderFailureKind.Unknown, retryable: false)
                };
            }

            if (current is HttpRequestException http)
                return ClassifyHttp(http);
        }

        return Create(LLMProviderFailureKind.Unknown, retryable: false);
    }

    private static LLMProviderException ClassifyHttp(HttpRequestException exception)
    {
        if (HttpRequestHelper.GetRetryMetadata(exception) is { } metadata)
        {
            var kind = metadata.FailureKind ?? ClassifyStatus(exception.StatusCode).Kind;
            var retryable = kind is LLMProviderFailureKind.Transport
                or LLMProviderFailureKind.Timeout
                or LLMProviderFailureKind.RateLimited
                or LLMProviderFailureKind.ServiceUnavailable;
            return Create(
                kind,
                retryable,
                exception.StatusCode,
                metadata.SafeProviderCode,
                metadata.AttemptCount,
                metadata.RetryExhausted,
                metadata.RetryAfterMilliseconds);
        }

        var marker = FindKnownMarker(exception.Message);
        if (marker != null)
        {
            var retryable = marker.Value.Kind == LLMProviderFailureKind.RateLimited;
            return Create(marker.Value.Kind, retryable, exception.StatusCode, marker.Value.Code);
        }

        return exception.StatusCode switch
        {
            null => Create(LLMProviderFailureKind.Transport, retryable: true),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                Create(LLMProviderFailureKind.Timeout, retryable: true, exception.StatusCode),
            (HttpStatusCode)425 or HttpStatusCode.TooManyRequests =>
                Create(LLMProviderFailureKind.RateLimited, retryable: true, exception.StatusCode),
            HttpStatusCode.PaymentRequired =>
                Create(LLMProviderFailureKind.QuotaOrBilling, retryable: false, exception.StatusCode),
            HttpStatusCode.Unauthorized =>
                Create(LLMProviderFailureKind.Authentication, retryable: false, exception.StatusCode),
            HttpStatusCode.Forbidden =>
                Create(LLMProviderFailureKind.Authorization, retryable: false, exception.StatusCode),
            HttpStatusCode.NotFound =>
                Create(LLMProviderFailureKind.ModelUnavailable, retryable: false, exception.StatusCode),
            { } status when (int)status >= 500 =>
                Create(LLMProviderFailureKind.ServiceUnavailable, retryable: true, status),
            { } status when (int)status >= 400 =>
                Create(LLMProviderFailureKind.InvalidRequest, retryable: false, status),
            _ => Create(LLMProviderFailureKind.Unknown, retryable: false, exception.StatusCode)
        };
    }

    internal static LLMHttpFailureClassification ClassifyResponse(HttpStatusCode statusCode, string errorBody)
    {
        var marker = FindKnownMarker(errorBody);
        if (marker != null)
        {
            return new LLMHttpFailureClassification(
                marker.Value.Kind,
                marker.Value.Kind == LLMProviderFailureKind.RateLimited,
                marker.Value.Code);
        }

        return ClassifyStatus(statusCode);
    }

    private static LLMHttpFailureClassification ClassifyStatus(HttpStatusCode? statusCode)
        => statusCode switch
        {
            null => new(LLMProviderFailureKind.Transport, true, null),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                new(LLMProviderFailureKind.Timeout, true, null),
            (HttpStatusCode)425 or HttpStatusCode.TooManyRequests =>
                new(LLMProviderFailureKind.RateLimited, true, null),
            HttpStatusCode.PaymentRequired =>
                new(LLMProviderFailureKind.QuotaOrBilling, false, null),
            HttpStatusCode.Unauthorized =>
                new(LLMProviderFailureKind.Authentication, false, null),
            HttpStatusCode.Forbidden =>
                new(LLMProviderFailureKind.Authorization, false, null),
            HttpStatusCode.NotFound =>
                new(LLMProviderFailureKind.ModelUnavailable, false, null),
            { } status when (int)status >= 500 =>
                new(LLMProviderFailureKind.ServiceUnavailable, true, null),
            { } status when (int)status >= 400 =>
                new(LLMProviderFailureKind.InvalidRequest, false, null),
            _ => new(LLMProviderFailureKind.Unknown, false, null)
        };

    private static (LLMProviderFailureKind Kind, string Code)? FindKnownMarker(string message)
    {
        foreach (var marker in KnownMarkers)
        {
            if (message.Contains(marker.Marker, StringComparison.OrdinalIgnoreCase))
                return (marker.Kind, marker.Code);
        }

        return null;
    }

    private static LLMProviderException Create(
        LLMProviderFailureKind kind,
        bool retryable,
        HttpStatusCode? statusCode = null,
        string? safeProviderCode = null,
        int attemptCount = 1,
        bool retryExhausted = false,
        int? retryAfterMilliseconds = null)
        => new(
            kind,
            BuildSafeMessage(kind),
            retryable,
            statusCode == null ? null : (int)statusCode.Value,
            safeProviderCode,
            attemptCount,
            retryExhausted,
            retryAfterMilliseconds);

    private static string BuildSafeMessage(LLMProviderFailureKind kind)
        => kind switch
        {
            LLMProviderFailureKind.Transport => "The LLM provider could not be reached.",
            LLMProviderFailureKind.Timeout => "The LLM provider request timed out.",
            LLMProviderFailureKind.RateLimited => "The LLM provider temporarily rate-limited the request.",
            LLMProviderFailureKind.ServiceUnavailable => "The LLM provider is temporarily unavailable.",
            LLMProviderFailureKind.Authentication => "The LLM provider rejected authentication.",
            LLMProviderFailureKind.Authorization => "The LLM provider denied the request.",
            LLMProviderFailureKind.QuotaOrBilling => "The LLM provider rejected the request because quota or billing is unavailable.",
            LLMProviderFailureKind.InvalidRequest => "The LLM provider rejected the request as invalid.",
            LLMProviderFailureKind.ModelUnavailable => "The requested LLM model is unavailable.",
            _ => "The LLM provider failed without a retryable classification."
        };
}
