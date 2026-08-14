using System.Net;
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

            if (current is TimeoutException or TaskCanceledException)
            {
                return new WorkflowRuntimeException(
                    ErrorCodes.LlmTimeout,
                    current.Message,
                    retryable: true,
                    inner: exception,
                    details: details);
            }

            if (current is not HttpRequestException httpFailure)
                continue;

            var statusCode = httpFailure.StatusCode;
            if (statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
            {
                return new WorkflowRuntimeException(
                    ErrorCodes.LlmTimeout,
                    httpFailure.Message,
                    retryable: true,
                    inner: exception,
                    details: details);
            }

            if (statusCode == null
                || (int)statusCode == 425
                || statusCode == HttpStatusCode.TooManyRequests
                || (int)statusCode >= 500)
            {
                return new WorkflowRuntimeException(
                    ErrorCodes.LlmNetwork,
                    httpFailure.Message,
                    retryable: true,
                    inner: exception,
                    details: details);
            }

            if ((int)statusCode is >= 400 and <= 499)
            {
                return new WorkflowRuntimeException(
                    ErrorCodes.LlmProvider,
                    httpFailure.Message,
                    retryable: false,
                    inner: exception,
                    details: details);
            }
        }

        return null;
    }
}
