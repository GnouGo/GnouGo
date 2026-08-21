using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

internal static class LlmFailureClassifier
{
    public static WorkflowRuntimeException? Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var details = exception is WorkflowRuntimeException outer
            ? outer.Details?.DeepClone()
            : null;

        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is WorkflowRuntimeException runtime
                && runtime.Code is ErrorCodes.LlmTimeout or ErrorCodes.LlmNetwork or ErrorCodes.LlmProvider)
            {
                return runtime;
            }

            if (current is LLMClientException clientFailure)
                return FromTypedFailure(clientFailure, exception, details);

            if (current is TimeoutException or TaskCanceledException)
            {
                return FromLegacyFailure(
                    LLMClientFailureKind.Timeout,
                    retryable: true,
                    statusCode: null,
                    exception,
                    details);
            }

            if (current is not HttpRequestException httpFailure)
                continue;

            var statusCode = httpFailure.StatusCode;
            if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
            {
                return FromLegacyFailure(
                    LLMClientFailureKind.Timeout,
                    retryable: true,
                    statusCode,
                    exception,
                    details);
            }

            if (statusCode == null
                || (int)statusCode == 425
                || statusCode == HttpStatusCode.TooManyRequests
                || (int)statusCode >= 500)
            {
                var kind = statusCode switch
                {
                    null => LLMClientFailureKind.Transport,
                    HttpStatusCode.TooManyRequests or (HttpStatusCode)425 => LLMClientFailureKind.RateLimited,
                    _ => LLMClientFailureKind.ServiceUnavailable
                };
                return FromLegacyFailure(kind, retryable: true, statusCode, exception, details);
            }

            if ((int)statusCode is >= 400 and <= 499)
            {
                var kind = statusCode switch
                {
                    HttpStatusCode.Unauthorized => LLMClientFailureKind.Authentication,
                    HttpStatusCode.Forbidden => LLMClientFailureKind.Authorization,
                    HttpStatusCode.NotFound => LLMClientFailureKind.ModelUnavailable,
                    HttpStatusCode.PaymentRequired => LLMClientFailureKind.QuotaOrBilling,
                    _ => LLMClientFailureKind.InvalidRequest
                };
                return FromLegacyFailure(kind, retryable: false, statusCode, exception, details);
            }
        }

        return null;
    }

    private static WorkflowRuntimeException FromTypedFailure(
        LLMClientException failure,
        Exception original,
        JsonNode? existingDetails)
    {
        var code = failure.Kind == LLMClientFailureKind.Timeout
            ? ErrorCodes.LlmTimeout
            : failure.Retryable
                ? ErrorCodes.LlmNetwork
                : ErrorCodes.LlmProvider;
        var details = existingDetails as JsonObject ?? new JsonObject();
        details["stage"] = "llm_call";
        details["classification"] = ToContractValue(failure.Kind);
        details["retryable"] = failure.Retryable;
        details["attempt_count"] = 1;
        details["recommended_action"] = failure.Retryable ? "retry" : "correct_configuration_or_request";
        if (failure.StatusCode != null)
            details["status_code"] = failure.StatusCode.Value;
        if (!string.IsNullOrWhiteSpace(failure.SafeProviderCode))
            details["provider_code"] = failure.SafeProviderCode;

        return new WorkflowRuntimeException(
            code,
            failure.Message,
            retryable: failure.Retryable,
            inner: original,
            details: details);
    }

    private static WorkflowRuntimeException FromLegacyFailure(
        LLMClientFailureKind kind,
        bool retryable,
        HttpStatusCode? statusCode,
        Exception original,
        JsonNode? existingDetails)
        => FromTypedFailure(
            new LLMClientException(
                kind,
                BuildSafeMessage(kind),
                retryable,
                statusCode == null ? null : (int)statusCode.Value),
            original,
            existingDetails);

    private static string BuildSafeMessage(LLMClientFailureKind kind)
        => kind switch
        {
            LLMClientFailureKind.Transport => "The LLM client could not reach its provider.",
            LLMClientFailureKind.Timeout => "The LLM client request timed out.",
            LLMClientFailureKind.RateLimited => "The LLM client was temporarily rate-limited.",
            LLMClientFailureKind.ServiceUnavailable => "The LLM client provider is temporarily unavailable.",
            LLMClientFailureKind.Authentication => "The LLM client provider rejected authentication.",
            LLMClientFailureKind.Authorization => "The LLM client provider denied the request.",
            LLMClientFailureKind.QuotaOrBilling => "The LLM client provider rejected the request because quota or billing is unavailable.",
            LLMClientFailureKind.InvalidRequest => "The LLM client provider rejected the request as invalid.",
            LLMClientFailureKind.ModelUnavailable => "The requested LLM model is unavailable.",
            _ => "The LLM client failed without a retryable classification."
        };

    private static string ToContractValue(LLMClientFailureKind kind)
        => kind switch
        {
            LLMClientFailureKind.Transport => "transport",
            LLMClientFailureKind.Timeout => "timeout",
            LLMClientFailureKind.RateLimited => "rate_limited",
            LLMClientFailureKind.ServiceUnavailable => "service_unavailable",
            LLMClientFailureKind.Authentication => "authentication",
            LLMClientFailureKind.Authorization => "authorization",
            LLMClientFailureKind.QuotaOrBilling => "quota_or_billing",
            LLMClientFailureKind.InvalidRequest => "invalid_request",
            LLMClientFailureKind.ModelUnavailable => "model_unavailable",
            _ => "unknown"
        };
}
