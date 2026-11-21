using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Traffic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class RetryTests
{
    private const string Policy = "ProxyCopilot:Providers:test:Connection:RetryPolicy:";
    private static Dictionary<string, string?> Options(int attempts = 10, int budget = 1800000) => new()
    {
        [Policy + "MaxAttempts"] = attempts.ToString(), [Policy + "MaxTotalDelayMilliseconds"] = budget.ToString(),
        [Policy + "AttemptTimeoutMilliseconds"] = "10000"
    };
    private static ITrafficStore Store(TestHost proxy) => proxy.App.Services.GetRequiredService<ITrafficStore>();
    private static Task<HttpResponseMessage> Send(TestHost proxy, bool streaming = true, CancellationToken ct = default) => proxy.Client.SendAsync(
        new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = TestHost.Json(ProtocolRoundTripTests.Request(streaming).ToJsonString()) },
        HttpCompletionOption.ResponseHeadersRead, ct);
    private static async Task Until(Func<bool> ready)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, deadline.Token);
    }

    [Theory]
    [InlineData("150", 150)] [InlineData("0", 0)]
    [InlineData("Mon, 28 Sep 2026 12:02:30 GMT", 150)]
    [InlineData("Mon, 28 Sep 2026 11:59:00 GMT", 0)]
    public void HeaderDeltaOrDateIsNotClampedToBackoff(string header, int seconds)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", header);
        Assert.Equal(TimeSpan.FromSeconds(seconds), RetryTiming.Delay(response, 1, 1000, 30000, new ManualClock().GetUtcNow()));
    }

    [Theory]
    [InlineData(null)] [InlineData("nonsense")] [InlineData("-5")] [InlineData("1.5")]
    public void MissingOrInvalidHeadersUseBoundedJitter(string? header)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (header is not null) response.Headers.TryAddWithoutValidation("Retry-After", header);
        Assert.InRange(RetryTiming.Delay(response, 2, 1000, 1500, new ManualClock().GetUtcNow()).TotalMilliseconds, 0, 1500);
    }

    [Fact]
    public void OverflowingProviderDelayDoesNotFallBackToAnEarlyRetry()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "9999999999999999999999999");
        Assert.Equal(TimeSpan.MaxValue, RetryTiming.Delay(response, 1, 1000, 30000, new ManualClock().GetUtcNow()));
    }

    [Theory]
    [InlineData("openai", true)] [InlineData("copilot", true)] [InlineData("anthropic", true)] [InlineData("ollama", true)]
    [InlineData("openai", false)] [InlineData("anthropic", false)]
    public async Task LongWaitPreservesRequestAuthenticationToolsAndCaptures(string type, bool streaming)
    {
        var clock = new ManualClock();
        var requests = new ConcurrentQueue<string>();
        var tokens = new ConcurrentQueue<string>();
        var tokenCalls = 0;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            if (context.Request.Path == "/.well-known/openid-configuration")
            { await context.Response.WriteAsync($"{{\"token_endpoint\":\"http://{context.Request.Host}/token\"}}"); return; }
            if (context.Request.Path == "/token")
            { await context.Response.WriteAsync($"{{\"access_token\":\"private-token-{Interlocked.Increment(ref tokenCalls)}\",\"expires_in\":60}}"); return; }
            requests.Enqueue(await new StreamReader(context.Request.Body).ReadToEndAsync());
            tokens.Enqueue(context.Request.Headers.Authorization.ToString());
            if (requests.Count == 1)
            {
                context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "150";
                await context.Response.WriteAsync("{\"error\":\"private-token-1 quota\"}"); return;
            }
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture(type, streaming, true),
                streaming ? type == "ollama" ? "application/x-ndjson" : "text/event-stream" : "application/json");
        });
        var options = Options(budget: 150000);
        options["ProxyCopilot:Providers:test:Authentication"] = "OidcClientSecret";
        options["ProxyCopilot:Providers:test:Connection:Issuer"] = upstream.Url;
        options["ProxyCopilot:Providers:test:Connection:ClientId"] = "client";
        options["ProxyCopilot:Providers:test:Connection:ClientSecret"] = "secret";
        options["ProxyCopilot:Providers:test:Connection:Scopes"] = "inference";
        await using var proxy = await TestHost.Proxy(upstream.Url, type, options, clock);
        var sending = Send(proxy, streaming, TestContext.Current.CancellationToken);
        await Until(() => clock.HasTimer(TimeSpan.FromSeconds(150)));
        var pending = Assert.Single(Store(proxy).Snapshot().Calls);
        Assert.Equal("running", pending.Status); Assert.Null(pending.FirstTokenMs); Assert.Null(pending.Usage);
        Assert.Equal(clock.GetUtcNow().AddSeconds(150), pending.Retry.NextAttemptAt);
        if (streaming)
        {
            await sending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Until(() => clock.HasTimer(TimeSpan.FromSeconds(15)));
            clock.Advance(TimeSpan.FromSeconds(15));
            await Until(() => Store(proxy).Detail(pending.Id)!.Bodies["clientResponse"].Text.Split(": keep-alive").Length >= 3);
            Assert.Null(Store(proxy).Snapshot().Calls[0].FirstTokenMs);
            clock.Advance(TimeSpan.FromSeconds(134));
        }
        else { Assert.False(sending.IsCompleted); clock.Advance(TimeSpan.FromSeconds(149)); }
        Assert.Single(requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await sending;
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, ProtocolRoundTripTests.ExtractCalls(text, streaming).Count);
        Assert.Equal(2, tokenCalls); Assert.Equal(2, requests.Count);
        Assert.Equal(requests.First(), requests.Last());
        Assert.Equal(new[] { "Bearer private-token-1", "Bearer private-token-2" }, tokens.ToArray());
        await Until(() => Store(proxy).Snapshot().Calls[0].Status == "completed");
        var detail = Store(proxy).Detail(pending.Id)!;
        Assert.Equal(2, detail.Summary.Retry.Attempt); Assert.Null(detail.Summary.Retry.NextAttemptAt);
        Assert.Equal(150000, detail.Summary.Retry.WaitedMs);
        Assert.True(detail.Summary.FirstTokenMs >= 150000);
        Assert.Contains("[REDACTED]", detail.Bodies["upstreamRejections"].Text);
        Assert.DoesNotContain("quota", detail.Bodies["upstreamResponse"].Text);
        Assert.DoesNotContain("private-token", detail.Bodies["upstreamRejections"].Text);
    }

    [Theory]
    [InlineData(true, 2, 300000, 429)] [InlineData(true, 10, 150000, 503)]
    [InlineData(false, 2, 300000, 429)]
    public async Task RepeatedRejectionsStopAtAttemptOrCumulativeBudget(bool streaming, int maxAttempts, int budget, int status)
    {
        var clock = new ManualClock(); var attempts = 0;
        await using var upstream = await TestHost.Upstream(context =>
        { Interlocked.Increment(ref attempts); context.Response.StatusCode = status; context.Response.Headers.RetryAfter = "150"; return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Options(maxAttempts, budget), clock: clock);
        var sending = Send(proxy, streaming, TestContext.Current.CancellationToken);
        await Until(() => clock.HasTimer(TimeSpan.FromSeconds(150)));
        if (streaming) await sending;
        clock.Advance(TimeSpan.FromSeconds(150));
        using var response = await sending;
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(streaming ? HttpStatusCode.OK : (HttpStatusCode)status, response.StatusCode);
        Assert.Contains("upstream_rejected", text); Assert.DoesNotContain("[DONE]", text);
        Assert.Equal(2, attempts);
        await Until(() => Store(proxy).Snapshot().Calls[0].Status == "failed");
        var summary = Store(proxy).Snapshot().Calls[0];
        Assert.Equal(status, summary.StatusCode); Assert.Equal(150000, summary.Retry.WaitedMs);
        Assert.Null(summary.Retry.NextAttemptAt);
    }

    [Theory]
    [InlineData("151")] [InlineData("9999999999999999999999999")]
    public async Task DelayExceedingBudgetReturnsHttpErrorWithoutStartingSse(string delay)
    {
        var attempts = 0;
        await using var upstream = await TestHost.Upstream(context =>
        { Interlocked.Increment(ref attempts); context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = delay; return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Options(budget: 150000));
        using var response = await Send(proxy, ct: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType); Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationOrShutdownStopsWaitingWithoutRetry(bool shutdown)
    {
        var clock = new ManualClock(); var attempts = 0;
        await using var upstream = await TestHost.Upstream(context =>
        { Interlocked.Increment(ref attempts); context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "150"; return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Options(), clock: clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var response = await Send(proxy, ct: cancellation.Token);
        if (shutdown) proxy.App.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
        else { cancellation.Cancel(); response.Dispose(); }
        await Until(() => Store(proxy).Snapshot().Calls[0].Status == "cancelled");
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, attempts); Assert.Null(Store(proxy).Snapshot().Calls[0].Retry.NextAttemptAt);
    }

    [Fact]
    public async Task AttemptTimeoutRestartsAfterWaitAndIsReportedAsSseError()
    {
        var clock = new ManualClock(); var attempts = 0;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "150"; return; }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); } catch (OperationCanceledException) { }
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Options(), clock: clock);
        using var response = await Send(proxy, ct: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(150));
        await Until(() => attempts == 2 && clock.HasTimer(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal("running", Store(proxy).Snapshot().Calls[0].Status);
        clock.Advance(TimeSpan.FromSeconds(1));
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("upstream_timeout", text); Assert.DoesNotContain("[DONE]", text); Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ExactCumulativeBoundaryAllowsSuccessAndDoesNotBlockOtherCalls()
    {
        var clock = new ManualClock(); var attempts = 0;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            if (!body.Contains("independent") && Interlocked.Increment(ref attempts) <= 2)
            { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "150"; return; }
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("openai", true, false), "text/event-stream");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Options(budget: 300000), clock: clock);
        using var waiting = await Send(proxy, ct: TestContext.Current.CancellationToken);
        var independent = ProtocolRoundTripTests.Request(true); independent["messages"]![1]!["content"] = "independent";
        using var other = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(independent.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Contains("[DONE]", await other.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(150));
        await Until(() => attempts == 2 && clock.HasTimer(TimeSpan.FromSeconds(150)));
        clock.Advance(TimeSpan.FromSeconds(150));
        var text = await waiting.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("[DONE]", text); Assert.Equal(3, attempts);
        await Until(() => Store(proxy).Snapshot().Calls.All(c => c.Status == "completed"));
        Assert.Equal(300000, Store(proxy).Snapshot().Calls.Single(c => c.Retry.Attempt == 3).Retry.WaitedMs);
    }

    [Fact]
    public async Task InterruptedGenerationAfterRetryIsNeverReplayed()
    {
        var attempts = 0;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "0"; return; }
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"},\"finish_reason\":null}]}\n\n");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Options());
        using var response = await Send(proxy, ct: TestContext.Current.CancellationToken);
        try { Assert.DoesNotContain("[DONE]", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)); }
        catch (HttpRequestException) { }
        await Until(() => Store(proxy).Snapshot().Calls[0].Status == "failed");
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ZeroDelayRetriesDoNotConsumeTheWaitingBudget()
    {
        var attempts = 0;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            if (Interlocked.Increment(ref attempts) < 3) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "0"; return; }
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("openai", true, false), "text/event-stream");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: Options(budget: 0));
        using var response = await Send(proxy, ct: TestContext.Current.CancellationToken);
        Assert.Contains("[DONE]", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void RetryStateResynchronizesAndCaptureLimitsClearOrEvictActiveWaits()
    {
        var clock = new ManualClock();
        var options = ConfigurationAndTrafficTests.Options(); options.Capture.MaxBodyBytes = 32;
        var store = new TrafficStore(options, new(options), clock);
        var route = new Configuration.ModelRegistry(options).Models[0];
        var id = store.Start(route, []);
        store.UpdateRetry(id, 1, 429, clock.GetUtcNow().AddSeconds(150));
        clock.Advance(TimeSpan.FromSeconds(25));
        using var reconnect = store.Subscribe();
        Assert.True(reconnect.Reader.TryRead(out var version)); Assert.Equal(store.Snapshot().Version, version);
        Assert.Equal(25000, store.Snapshot().Calls[0].Retry.WaitedMs);
        store.Append(id, "upstreamRejections", new byte[100]);
        Assert.True(store.Detail(id)!.Bodies["upstreamRejections"].Truncated);
        store.Clear(); store.UpdateRetry(id, 2); store.Complete(id, "completed", 200);
        Assert.Empty(store.Snapshot().Calls);
        options.Capture.MaxTotalBytes = 1;
        id = store.Start(route, []); store.UpdateRetry(id, 1, 429, clock.GetUtcNow().AddSeconds(150));
        store.Append(id, "upstreamRejections", "over budget"u8);
        store.UpdateRetry(id, 2); Assert.Null(store.Detail(id));
    }
}
