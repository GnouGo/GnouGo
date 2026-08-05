using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core.Tests;

public sealed class HttpRequestHelperTests
{
    [Fact]
    public async Task SendWithServerErrorRetryAsync_RetriesTwiceWithExponentialBackoff()
    {
        var attempts = 0;
        var requestBodies = new List<string>();
        var authorizationHeaders = new List<string?>();
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(new StubHttpMessageHandler(async request =>
        {
            attempts++;
            requestBodies.Add(await request.Content!.ReadAsStringAsync());
            authorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return attempts switch
            {
                1 => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                2 => new HttpResponseMessage(HttpStatusCode.BadGateway),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
            };
        }));

        using var response = await HttpRequestHelper.SendWithServerErrorRetryAsync(
            http,
            () =>
            {
                var request = HttpRequestHelper.CreateJsonPost(
                    "https://provider.example/chat",
                    "{}"u8.ToArray());
                HttpRequestHelper.SetBearerAuth(request, "secret");
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            NullLogger.Instance,
            "test provider call",
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
        Assert.Equal(["{}", "{}", "{}"], requestBodies);
        Assert.Equal(["Bearer secret", "Bearer secret", "Bearer secret"], authorizationHeaders);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)],
            delays);
    }

    [Fact]
    public async Task SendWithServerErrorRetryAsync_ReturnsFinalServerErrorAfterRetryBudget()
    {
        var attempts = 0;
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));

        using var response = await HttpRequestHelper.SendWithServerErrorRetryAsync(
            http,
            () => HttpRequestHelper.CreateGet("https://provider.example/models"),
            HttpCompletionOption.ResponseHeadersRead,
            NullLogger.Instance,
            "test provider call",
            static (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SendWithServerErrorRetryAsync_DoesNotRetryNonServerErrors(HttpStatusCode statusCode)
    {
        var attempts = 0;
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }));

        using var response = await HttpRequestHelper.SendWithServerErrorRetryAsync(
            http,
            () => HttpRequestHelper.CreateGet("https://provider.example/models"),
            HttpCompletionOption.ResponseHeadersRead,
            NullLogger.Instance,
            "test provider call",
            static (_, _) => throw new InvalidOperationException("A retry was not expected."),
            TestContext.Current.CancellationToken);

        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request);
    }
}
