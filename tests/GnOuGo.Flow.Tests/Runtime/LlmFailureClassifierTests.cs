using System.Net;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class LlmFailureClassifierTests
{
    [Fact]
    public void Classify_InternalTimeout_IsRetryableTimeout()
    {
        var classified = LlmFailureClassifier.Classify(new TimeoutException("timed out"));

        Assert.NotNull(classified);
        Assert.Equal(ErrorCodes.LlmTimeout, classified.Code);
        Assert.True(classified.Retryable);
    }

    [Theory]
    [InlineData(408, "LLM_TIMEOUT", true)]
    [InlineData(504, "LLM_TIMEOUT", true)]
    [InlineData(425, "LLM_NETWORK", true)]
    [InlineData(429, "LLM_NETWORK", true)]
    [InlineData(500, "LLM_NETWORK", true)]
    [InlineData(503, "LLM_NETWORK", true)]
    [InlineData(400, "LLM_PROVIDER", false)]
    [InlineData(401, "LLM_PROVIDER", false)]
    [InlineData(404, "LLM_PROVIDER", false)]
    public void Classify_HttpFailure_UsesStatusContract(int status, string expectedCode, bool retryable)
    {
        var failure = new HttpRequestException(
            "provider failure",
            inner: null,
            statusCode: (HttpStatusCode)status);

        var classified = LlmFailureClassifier.Classify(failure);

        Assert.NotNull(classified);
        Assert.Equal(expectedCode, classified.Code);
        Assert.Equal(retryable, classified.Retryable);
        Assert.DoesNotContain("provider failure", classified.Message, StringComparison.Ordinal);
        Assert.Equal(status, classified.Details!["status_code"]!.GetValue<int>());
        Assert.Equal(1, classified.Details["attempt_count"]!.GetValue<int>());
    }

    [Fact]
    public void Classify_TransportFailureWithoutStatus_IsRetryableNetwork()
    {
        var classified = LlmFailureClassifier.Classify(new HttpRequestException("connection reset"));

        Assert.NotNull(classified);
        Assert.Equal(ErrorCodes.LlmNetwork, classified.Code);
        Assert.True(classified.Retryable);
    }

    [Theory]
    [InlineData(LLMClientFailureKind.Transport, true, "LLM_NETWORK")]
    [InlineData(LLMClientFailureKind.Timeout, true, "LLM_TIMEOUT")]
    [InlineData(LLMClientFailureKind.RateLimited, true, "LLM_NETWORK")]
    [InlineData(LLMClientFailureKind.ServiceUnavailable, true, "LLM_NETWORK")]
    [InlineData(LLMClientFailureKind.QuotaOrBilling, false, "LLM_PROVIDER")]
    [InlineData(LLMClientFailureKind.Authentication, false, "LLM_PROVIDER")]
    [InlineData(LLMClientFailureKind.Authorization, false, "LLM_PROVIDER")]
    [InlineData(LLMClientFailureKind.InvalidRequest, false, "LLM_PROVIDER")]
    [InlineData(LLMClientFailureKind.ModelUnavailable, false, "LLM_PROVIDER")]
    [InlineData(LLMClientFailureKind.Unknown, false, "LLM_PROVIDER")]
    public void Classify_TypedClientFailure_PreservesDispositionAndRedactedMetadata(
        LLMClientFailureKind kind,
        bool retryable,
        string expectedCode)
    {
        var classified = LlmFailureClassifier.Classify(new LLMClientException(
            kind,
            "redacted failure",
            retryable,
            statusCode: 400,
            safeProviderCode: "safe_code"));

        Assert.NotNull(classified);
        Assert.Equal(expectedCode, classified.Code);
        Assert.Equal(retryable, classified.Retryable);
        Assert.Equal(400, classified.Details!["status_code"]!.GetValue<int>());
        Assert.Equal("safe_code", classified.Details["provider_code"]!.GetValue<string>());
        Assert.Equal(1, classified.Details["attempt_count"]!.GetValue<int>());
    }
}
