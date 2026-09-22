using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using GnOuGo.ProxyCopilot.Server.Traffic;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace GnOuGo.ProxyCopilot.Server.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task OidcIsCachedAcrossConcurrentCallsAndClientCredentialsNeverReachUpstream()
    {
        var tokenCalls = 0;
        var auth = new ConcurrentBag<string>();
        await using var upstream = await TestHost.Upstream(async context =>
        {
            if (context.Request.Path == "/.well-known/openid-configuration")
            {
                await context.Response.WriteAsync($"{{\"token_endpoint\":\"http://{context.Request.Host}/token\"}}", context.RequestAborted); return;
            }
            if (context.Request.Path == "/token")
            {
                Interlocked.Increment(ref tokenCalls);
                Assert.StartsWith("Basic ", context.Request.Headers.Authorization.ToString());
                await context.Response.WriteAsync("{\"access_token\":\"oidc-bearer-value\",\"expires_in\":3600}", context.RequestAborted); return;
            }
            auth.Add(context.Request.Headers.Authorization.ToString());
            Assert.Equal(0, context.Request.Headers.Cookie.Count);
            Assert.Equal(0, context.Request.Headers["X-Arbitrary-Credential"].Count);
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("openai", false, false), "application/json");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: new()
        {
            ["ProxyCopilot:Providers:test:Authentication"] = "OidcClientSecret",
            ["ProxyCopilot:Providers:test:Connection:Issuer"] = upstream.Url,
            ["ProxyCopilot:Providers:test:Connection:ClientId"] = "client",
            ["ProxyCopilot:Providers:test:Connection:ClientSecret"] = "oidc-client-secret",
            ["ProxyCopilot:Providers:test:Connection:Scopes"] = "model.read"
        });
        proxy.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "incoming-credential");
        proxy.Client.DefaultRequestHeaders.Add("Cookie", "sensitive-cookie=value");
        proxy.Client.DefaultRequestHeaders.Add("X-Arbitrary-Credential", "do-not-forward");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(ProtocolRoundTripTests.Request(false).ToJsonString()), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }));
        Assert.Equal(1, tokenCalls); Assert.Equal(12, auth.Count); Assert.All(auth, header => Assert.Equal("Bearer oidc-bearer-value", header));
        using var setup = await proxy.Client.GetAsync("/api/setup", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("oidc-", await setup.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnlyExplicitRetryableRejectionsAreRetriedBeforeStreaming()
    {
        var attempts = 0;
        await using var upstream = await TestHost.Upstream(async context =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "0"; return; }
            await ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("openai", true, false), "text/event-stream");
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: new() { ["ProxyCopilot:Providers:test:Connection:RetryPolicy:MaxAttempts"] = "2" });
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(ProtocolRoundTripTests.Request(true).ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(2, attempts);
        Assert.Contains("[DONE]", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UncertainConnectionFailureDoesNotRetry()
    {
        var attempts = 0;
        await using var upstream = await TestHost.Upstream(context => { Interlocked.Increment(ref attempts); context.Abort(); return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: new() { ["ProxyCopilot:Providers:test:Connection:RetryPolicy:MaxAttempts"] = "3" });
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(ProtocolRoundTripTests.Request(false).ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task FirstDeltaIsDeliveredBeforeUpstreamFinishesAndCancellationPropagates()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var upstream = await TestHost.Upstream(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"first\"},\"finish_reason\":null}]}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        });
        await using var proxy = await TestHost.Proxy(upstream.Url);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = TestHost.Json(ProtocolRoundTripTests.Request(true).ToJsonString()) };
        using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(abort.Token));
        var first = await reader.ReadLineAsync(abort.Token);
        Assert.Contains("first", first);
        var store = proxy.App.Services.GetRequiredService<ITrafficStore>();
        Assert.Equal("running", Assert.Single(store.Snapshot().Calls).Status);
        Assert.NotNull(Assert.Single(store.Snapshot().Calls).FirstTokenMs);
        abort.Cancel(); response.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TimeoutCancelsUpstreamAndProducesStructuredError()
    {
        await using var upstream = await TestHost.Upstream(async context =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); } catch (OperationCanceledException) { }
        });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: new() { ["ProxyCopilot:Providers:test:Connection:RetryPolicy:AttemptTimeoutMilliseconds"] = "150" });
        using var response = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(ProtocolRoundTripTests.Request(false).ToJsonString()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("upstream_timeout", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownModelsInvalidJsonOversizedBodiesAndCrossOriginRequestsAreRejected()
    {
        var dispatched = 0;
        await using var upstream = await TestHost.Upstream(_ => { Interlocked.Increment(ref dispatched); return Task.CompletedTask; });
        await using var proxy = await TestHost.Proxy(upstream.Url, extra: new() { ["ProxyCopilot:MaxRequestBytes"] = "512" });
        using var unknown = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json("{\"model\":\"missing\"}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var invalid = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json("{"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var large = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(new string('x', 1000)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        using var external = new HttpRequestMessage(HttpMethod.Get, "/api/traffic"); external.Headers.Add("Origin", "https://outside.test");
        using var blocked = await proxy.Client.SendAsync(external, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/traffic"); rebinding.Headers.Host = "outside.test";
        using var denied = await proxy.Client.SendAsync(rebinding, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(0, dispatched);
    }

    [Fact]
    public async Task TrafficEventsStartWithInvalidationAndHistoryCanBeCleared()
    {
        await using var upstream = await TestHost.Upstream(context => ProtocolRoundTripTests.Write(context, ProtocolRoundTripTests.Fixture("openai", false, false), "application/json"));
        await using var proxy = await TestHost.Proxy(upstream.Url);
        using var events = await proxy.Client.GetAsync("/api/traffic/events", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        Assert.Equal("event: changed", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        Assert.Contains("version", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
        using var call = await proxy.Client.PostAsync("/v1/chat/completions", TestHost.Json(ProtocolRoundTripTests.Request(false).ToJsonString()), TestContext.Current.CancellationToken);
        var store = proxy.App.Services.GetRequiredService<ITrafficStore>();
        var summary = Assert.Single(store.Snapshot().Calls);
        var detail = store.Detail(summary.Id)!;
        Assert.Equal(4, detail.Bodies.Count);
        using var clear = await proxy.Client.DeleteAsync("/api/traffic", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode); Assert.Empty(store.Snapshot().Calls);
    }
}
