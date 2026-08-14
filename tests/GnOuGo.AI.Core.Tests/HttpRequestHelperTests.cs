using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core.Tests;

public sealed class HttpRequestHelperTests
{
    [Fact]
    public async Task SendWithServerErrorRetryAsync_RetriesThreeTimesWithExponentialBackoff()
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
                3 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
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
        Assert.Equal(4, attempts);
        Assert.Equal(["{}", "{}", "{}", "{}"], requestBodies);
        Assert.Equal(["Bearer secret", "Bearer secret", "Bearer secret", "Bearer secret"], authorizationHeaders);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1_000)],
            delays);
    }

    [Fact]
    public async Task SendWithServerErrorRetryAsync_ReturnsFinalServerErrorAfterRetryBudget()
    {
        var attempts = 0;
        var logger = new CapturingLogger();
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));

        using var response = await HttpRequestHelper.SendWithServerErrorRetryAsync(
            http,
            () => HttpRequestHelper.CreateGet("https://provider.example/models"),
            HttpCompletionOption.ResponseHeadersRead,
            logger,
            "test provider call",
            static (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(4, attempts);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error
            && entry.Message.Contains("StatusCode=503", StringComparison.Ordinal)
            && entry.Message.Contains("AttemptCount=4", StringComparison.Ordinal)
            && entry.Message.Contains("RetryCount=3", StringComparison.Ordinal)
            && entry.Message.Contains("ElapsedMs=", StringComparison.Ordinal));
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

    [Fact]
    public async Task SendWithServerErrorRetryAsync_InternalHttpClientTimeoutBecomesTimeoutException()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request timed out."))))
        {
            Timeout = TimeSpan.FromSeconds(17)
        };

        var failure = await Assert.ThrowsAsync<TimeoutException>(() =>
            HttpRequestHelper.SendWithServerErrorRetryAsync(
                http,
                () => HttpRequestHelper.CreateGet("https://provider.example/models"),
                HttpCompletionOption.ResponseHeadersRead,
                NullLogger.Instance,
                "test provider call",
                static (_, _) => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains("17 seconds", failure.Message, StringComparison.Ordinal);
        Assert.IsType<TaskCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task SendWithServerErrorRetryAsync_CallerCancellationRemainsCancellation()
    {
        using var http = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("No request should be sent.")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HttpRequestHelper.SendWithServerErrorRetryAsync(
                http,
                () => HttpRequestHelper.CreateGet("https://provider.example/models"),
                HttpCompletionOption.ResponseHeadersRead,
                NullLogger.Instance,
                "test provider call",
                static (_, _) => Task.CompletedTask,
                cancellation.Token));

        Assert.IsNotType<TimeoutException>(failure);
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

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
