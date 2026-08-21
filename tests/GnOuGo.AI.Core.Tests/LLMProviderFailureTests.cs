using System.Net;
using GnOuGo.AI.Core;

namespace GnOuGo.AI.Core.Tests;

public sealed class LLMProviderFailureTests
{
    [Theory]
    [InlineData(429, "rate_limit_exceeded", LLMProviderFailureKind.RateLimited, true)]
    [InlineData(429, "credit_balance_exhausted", LLMProviderFailureKind.QuotaOrBilling, false)]
    [InlineData(400, "insufficient_quota", LLMProviderFailureKind.QuotaOrBilling, false)]
    [InlineData(401, "unauthorized", LLMProviderFailureKind.Authentication, false)]
    [InlineData(403, "forbidden", LLMProviderFailureKind.Authorization, false)]
    [InlineData(404, "not found", LLMProviderFailureKind.ModelUnavailable, false)]
    [InlineData(503, "server error", LLMProviderFailureKind.ServiceUnavailable, true)]
    public async Task RoutingClient_ProducesRedactedTypedFailure(
        int status,
        string providerMarker,
        LLMProviderFailureKind expectedKind,
        bool expectedRetryable)
    {
        const string secret = "never-log-this-secret";
        var provider = new ThrowingProvider(new HttpRequestException(
            $"provider rejected request: {providerMarker}; token={secret}",
            inner: null,
            statusCode: (HttpStatusCode)status));
        var client = CreateClient(provider);

        var failure = await Assert.ThrowsAsync<LLMProviderException>(() => client.CallAsync(
            new LLMClientRequest { Provider = "fake", Model = "model", Prompt = secret },
            TestContext.Current.CancellationToken));

        Assert.Equal(expectedKind, failure.Kind);
        Assert.Equal(expectedRetryable, failure.Retryable);
        Assert.Equal(status, failure.StatusCode);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task RoutingClient_ClassifiesTransportFailureAsRetryable()
    {
        var client = CreateClient(new ThrowingProvider(new HttpRequestException("connection reset")));

        var failure = await Assert.ThrowsAsync<LLMProviderException>(() => client.CallAsync(
            new LLMClientRequest { Provider = "fake", Model = "model", Prompt = "hello" },
            TestContext.Current.CancellationToken));

        Assert.Equal(LLMProviderFailureKind.Transport, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.Null(failure.StatusCode);
    }

    [Fact]
    public async Task RoutingClient_ClassifiesTimeoutAsRetryableWithoutLeakingMessage()
    {
        const string secret = "timeout-provider-body";
        var client = CreateClient(new ThrowingProvider(new TimeoutException(secret)));

        var failure = await Assert.ThrowsAsync<LLMProviderException>(() => client.CallAsync(
            new LLMClientRequest { Provider = "fake", Model = "model", Prompt = "hello" },
            TestContext.Current.CancellationToken));

        Assert.Equal(LLMProviderFailureKind.Timeout, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoutingClient_ClassifiesInvalidRequestAsTerminal()
    {
        var client = CreateClient(new ThrowingProvider(new HttpRequestException(
            "provider rejected malformed request",
            inner: null,
            statusCode: HttpStatusCode.BadRequest)));

        var failure = await Assert.ThrowsAsync<LLMProviderException>(() => client.CallAsync(
            new LLMClientRequest { Provider = "fake", Model = "model", Prompt = "hello" },
            TestContext.Current.CancellationToken));

        Assert.Equal(LLMProviderFailureKind.InvalidRequest, failure.Kind);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public async Task RoutingClient_ClassifiesUnknownFailureAsTerminalAndRedacted()
    {
        const string secret = "unknown-provider-body";
        var client = CreateClient(new ThrowingProvider(new InvalidOperationException(secret)));

        var failure = await Assert.ThrowsAsync<LLMProviderException>(() => client.CallAsync(
            new LLMClientRequest { Provider = "fake", Model = "model", Prompt = "hello" },
            TestContext.Current.CancellationToken));

        Assert.Equal(LLMProviderFailureKind.Unknown, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
    }

    private static RoutingLLMClient CreateClient(ILLMProvider provider)
        => new(
            new LLMOptions
            {
                DefaultProvider = "fake",
                DefaultModel = "model",
                Models = { ["fake"] = new ModelProviderOptions { Type = "fake" } }
            },
            [provider]);

    private sealed class ThrowingProvider(Exception exception) : ILLMProvider
    {
        public string ProviderType => "fake";

        public Task<LLMClientResponse> CallAsync(
            string model,
            ModelProviderOptions provider,
            LLMClientRequest request,
            CancellationToken ct)
            => Task.FromException<LLMClientResponse>(exception);
    }
}
