namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Provider-neutral failure classification exposed by an <see cref="ILLMClient"/>.</summary>
public enum LLMClientFailureKind
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
/// Redacted LLM failure contract. Implementations must not include provider response bodies,
/// credentials, prompts, or user answers in the exception message or properties.
/// </summary>
public sealed class LLMClientException : Exception
{
    public LLMClientException(
        LLMClientFailureKind kind,
        string message,
        bool retryable,
        int? statusCode = null,
        string? safeProviderCode = null)
        : base(message)
    {
        Kind = kind;
        Retryable = retryable;
        StatusCode = statusCode;
        SafeProviderCode = safeProviderCode;
    }

    public LLMClientFailureKind Kind { get; }

    public bool Retryable { get; }

    public int? StatusCode { get; }

    public string? SafeProviderCode { get; }
}
