using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Integrations;

/// <summary>
/// Maps the AI provider failure contract to Flow's independent client failure contract.
/// Host-specific LLM adapters must use this boundary instead of leaking AI exceptions
/// into Flow.Core.
/// </summary>
public static class LLMProviderFailureMapper
{
    public static LLMClientException Map(LLMProviderException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new LLMClientException(
            MapKind(failure.Kind),
            failure.Message,
            failure.Retryable,
            failure.StatusCode,
            failure.SafeProviderCode);
    }

    public static LLMClientFailureKind MapKind(LLMProviderFailureKind kind)
        => kind switch
        {
            LLMProviderFailureKind.Transport => LLMClientFailureKind.Transport,
            LLMProviderFailureKind.Timeout => LLMClientFailureKind.Timeout,
            LLMProviderFailureKind.RateLimited => LLMClientFailureKind.RateLimited,
            LLMProviderFailureKind.ServiceUnavailable => LLMClientFailureKind.ServiceUnavailable,
            LLMProviderFailureKind.Authentication => LLMClientFailureKind.Authentication,
            LLMProviderFailureKind.Authorization => LLMClientFailureKind.Authorization,
            LLMProviderFailureKind.QuotaOrBilling => LLMClientFailureKind.QuotaOrBilling,
            LLMProviderFailureKind.InvalidRequest => LLMClientFailureKind.InvalidRequest,
            LLMProviderFailureKind.ModelUnavailable => LLMClientFailureKind.ModelUnavailable,
            _ => LLMClientFailureKind.Unknown
        };
}
