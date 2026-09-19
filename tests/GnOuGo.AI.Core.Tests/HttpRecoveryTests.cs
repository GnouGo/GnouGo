using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace GnOuGo.AI.Core.Tests;

public sealed class HttpRecoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(425)][InlineData(429)][InlineData(500)][InlineData(502)][InlineData(503)][InlineData(504)]
    public async Task RetriesOnlyAllowedStatuses(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)(++calls == 1 ? status : 200)))));
        using var response = await Send(http);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(2, calls);
    }
    [Theory]
    [InlineData(400)][InlineData(401)][InlineData(402)][InlineData(403)][InlineData(404)][InlineData(408)][InlineData(422)][InlineData(501)][InlineData(505)]
    public async Task DoesNotRetryOtherStatuses(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); }));
        using var response = await Send(http);
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UncertainGenerationReservesBeforeRetryAndReplaysCompletion(bool timeout)
    {
        var journal = new Journal(); var ids = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            ids.Add(request.Headers.GetValues("X-Client-Request-Id").Single());
            Assert.Equal(ids.Count, journal.State!.Attempts.Count);
            if (ids.Count == 1) throw timeout ? new TaskCanceledException() : new HttpRequestException("Disconnected");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("complete") });
        }));
        using (new LLMHttpRetryContext("original", journal).Activate())
        using (var response = await Send(http, post: true)) Assert.Equal("complete", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, ids.Distinct().Count()); Assert.All(ids, id => Assert.StartsWith("original:", id));
        Assert.Null(journal.State!.Attempts[0].Status); Assert.Equal(200, journal.State.Attempts[1].Status);
        using (new LLMHttpRetryContext("original", journal).Activate())
        using (var response = await Send(http, post: true)) Assert.Equal("complete", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, ids.Count);
    }
    [Fact]
    public async Task UnjournaledPostNeverRetriesUncertainOrExternalEffects()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException(); }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(http, post: true)); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task ExhaustionIsDurableAcrossRestart()
    {
        var journal = new Journal(); var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new TaskCanceledException(); }));
        for (var restart = 0; restart < 2; restart++)
        {
            using var scope = new LLMHttpRetryContext("original", journal).Activate();
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => Send(http, post: true));
            var classified = LLMProviderFailureClassifier.Classify(failure);
            Assert.Equal(2, classified.AttemptCount); Assert.True(classified.RetryExhausted);
        }
        Assert.Equal(2, calls); Assert.Equal(2, journal.State!.Attempts.Count);
    }
    [Fact]
    public async Task LostCompletionIsUncertainAndNeverResendsItsIdentity()
    {
        var journal = new Journal { FailCompletion = true }; var ids = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            ids.Add(request.Headers.GetValues("X-Client-Request-Id").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        using (new LLMHttpRetryContext("original", journal).Activate())
            await Assert.ThrowsAsync<IOException>(() => Send(http, post: true));
        Assert.Null(journal.State!.Attempts[0].Status);
        journal.FailCompletion = false;
        using (new LLMHttpRetryContext("original", journal).Activate())
        using (await Send(http, post: true)) { }
        Assert.Equal(2, ids.Distinct().Count()); Assert.Null(journal.State.Attempts[0].Status);
    }
    [Fact]
    public async Task AdmissionFailureDoesNotDispatchOrMultiplyRetries()
    {
        var journal = new Journal { RejectSecond = true }; var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException(); }));
        using var scope = new LLMHttpRetryContext("original", journal).Activate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(http, post: true)); Assert.Equal(1, calls);
        Assert.Single(journal.State!.Attempts);
    }
    [Fact]
    public async Task PerAttemptTimeoutRetriesOnceAndCallerCancellationNeverRetries()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            calls++; await Task.Delay(Timeout.Infinite, token); return new HttpResponseMessage();
        }));
        await Assert.ThrowsAsync<TimeoutException>(() => Send(http, policy: new() { AttemptTimeoutMilliseconds = 10 }));
        Assert.Equal(2, calls);
        using var cancellation = new CancellationTokenSource();
        using var cancelledHttp = new HttpClient(new Handler((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }));
        var journal = new Journal();
        using (new LLMHttpRetryContext("cancelled", journal).Activate())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(cancelledHttp, post: true, ct: cancellation.Token));
        Assert.Equal("cancelled", Assert.Single(journal.State!.Attempts).Failure);
        using (new LLMHttpRetryContext("cancelled", journal).Activate())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(cancelledHttp, post: true));
        Assert.Single(journal.State.Attempts);
    }
    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError)][InlineData(HttpRequestError.UserAuthenticationError)][InlineData(HttpRequestError.ConfigurationLimitExceeded)]
    public async Task PermanentTransportErrorsDoNotRetry(HttpRequestError error)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException(error); }));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => Send(http));
        Assert.Equal(1, calls); Assert.False(LLMProviderFailureClassifier.Classify(failure).Retryable);
    }
    [Fact]
    public async Task RetryAfterDeadlineSurvivesRestartWithoutAnotherUncertainCharge()
    {
        var journal = new Journal(); var calls = 0;
        var now = new DateTimeOffset(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(++calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("Retry-After", "5"); return Task.FromResult(response);
        }));
        Task<HttpResponseMessage> Run(Func<TimeSpan, CancellationToken, Task> delay) => HttpRequestHelper.SendWithTransientRetryAsync(http,
            () => HttpRequestHelper.CreateJsonPost("https://provider.example/generate", "{}"u8.ToArray()), HttpCompletionOption.ResponseHeadersRead,
            NullLogger.Instance, "test", new(), delay, _ => throw new InvalidOperationException("Retry-After wins"), () => now, Ct);
        using (new LLMHttpRetryContext("original", journal).Activate())
            await Assert.ThrowsAsync<IOException>(() => Run((delay, _) => { Assert.Equal(TimeSpan.FromSeconds(5), delay); throw new IOException("Process stopped during backoff"); }));
        now = now.AddSeconds(3);
        using (new LLMHttpRetryContext("original", journal).Activate())
        using (await Run((delay, _) => { Assert.Equal(TimeSpan.FromSeconds(2), delay); return Task.CompletedTask; })) { }
        Assert.Equal(2, calls); Assert.All(journal.State!.Attempts, a => Assert.NotNull(a.Status));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task GenerationJournalCannotAuthorizeToolsOrBackgroundEffects(bool background)
    {
        using var scope = new LLMHttpRetryContext("request", new Journal()).Activate();
        var client = new RoutingLLMClient(new LLMOptions(), []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync(new()
        {
            UseBackgroundMode = background, Tools = background ? null : [new LLMToolDef { Name = "effect" }]
        }, Ct));
    }
    private static Task<HttpResponseMessage> Send(HttpClient http, bool post = false, LLMProviderRetryPolicyOptions? policy = null, CancellationToken? ct = null)
        => HttpRequestHelper.SendWithTransientRetryAsync(http,
            () => post ? HttpRequestHelper.CreateJsonPost("https://provider.example/generate", "{}"u8.ToArray()) : HttpRequestHelper.CreateGet("https://provider.example/models"),
            HttpCompletionOption.ResponseHeadersRead, NullLogger.Instance, "test", policy ?? new(),
            (_, _) => Task.CompletedTask, upper => upper / 2, () => DateTimeOffset.UtcNow, ct ?? Ct);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct); }
    private sealed class Journal : ILLMHttpRetryJournal
    {
        internal LLMHttpRetryState? State; internal bool RejectSecond; internal bool FailCompletion;
        private static LLMHttpRetryState Copy(LLMHttpRetryState state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, LLMHttpRetryJsonContext.Default.LLMHttpRetryState), LLMHttpRetryJsonContext.Default.LLMHttpRetryState)!;
        public Task<LLMHttpRetryState?> LoadAsync(CancellationToken ct) => Task.FromResult(State is null ? null : Copy(State));
        public Task SaveAsync(LLMHttpRetryState state, CancellationToken ct)
        {
            if (RejectSecond && state.Attempts.Count > 1) throw new InvalidOperationException("Budget exhausted");
            if (FailCompletion && state.Attempts.Last().Status is not null) throw new IOException("Receipt store unavailable");
            State = Copy(state); return Task.CompletedTask;
        }
    }
}
