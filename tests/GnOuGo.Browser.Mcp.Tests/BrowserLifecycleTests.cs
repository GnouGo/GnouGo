using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(LoadState.DOMContentLoaded, "before")]
    [InlineData(LoadState.DOMContentLoaded, "registration")]
    [InlineData(LoadState.DOMContentLoaded, "after")]
    [InlineData(LoadState.Load, "before")]
    [InlineData(LoadState.Load, "registration")]
    [InlineData(LoadState.Load, "after")]
    public async Task ReadinessCannotBeLostAroundWaitRegistration(LoadState state, string ordering)
    {
        await using var site = await Site.CreateAsync(state);
        var entered = Signal();
        var first = true;
        using var observation = new NavigationObservation(site.Page)
        {
            ReadinessCheckpoint = async () =>
            {
                entered.TrySetResult();
                if (ordering == "registration" && first)
                {
                    first = false;
                    await site.CompleteResourceAsync();
                }
            }
        };
        observation.Start();
        await site.Page.Locator("#go").ClickAsync();
        await site.ResourceRequested.Task.WaitAsync(Ct);
        await site.Page.WaitForFunctionAsync("document.readyState === 'interactive'");
        if (ordering == "before") await site.CompleteResourceAsync();
        var waiting = observation.WaitForLoadStateAsync(state, 5_000, Ct);
        await entered.Task.WaitAsync(Ct);
        if (ordering == "after")
        {
            Assert.False(waiting.IsCompleted);
            await site.CompleteResourceAsync();
        }
        await waiting;
        Assert.True(observation.Triggered);
        Assert.Equal(site.Origin + "/destination", site.Page.Url);
        Assert.Equal("Destination", await site.Page.TitleAsync());
        Assert.Equal(1, site.Visits.Count(p => p == "/destination"));
        Assert.Equal("complete", await site.Page.EvaluateAsync<string>("document.readyState"));
    }

    [Theory]
    [InlineData(LoadState.DOMContentLoaded)]
    [InlineData(LoadState.Load)]
    public async Task ReloadDoesNotReusePreviousDocumentReadiness(LoadState state)
    {
        await using var site = await Site.CreateAsync(state);
        site.ResourceRelease.TrySetResult();
        await site.Page.GotoAsync(site.Origin + "/destination");
        site.ResetResource();
        using var observation = new NavigationObservation(site.Page);
        observation.Start();
        await observation.WaitForLoadStateAsync(state, 5_000, Ct);
        await site.Page.ReloadAsync(new() { WaitUntil = WaitUntilState.Commit });
        await site.ResourceRequested.Task.WaitAsync(Ct);
        var waiting = observation.WaitForLoadStateAsync(state, 5_000, Ct);
        Assert.False(waiting.IsCompleted);
        await site.CompleteResourceAsync();
        await waiting;
        Assert.Equal(2, site.Visits.Count(p => p == "/destination"));
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("same_document")]
    [InlineData("no_navigation")]
    public async Task NavigationKindsPreserveReadinessAndDoNotRepeatActions(string kind)
    {
        await using var site = await Site.CreateAsync(LoadState.Load);
        site.ResourceRelease.TrySetResult();
        using var observation = new NavigationObservation(site.Page);
        observation.Start();
        if (kind == "redirect") await site.Page.Locator("#redirect").ClickAsync();
        else if (kind == "same_document") await site.Page.Locator("#fragment").ClickAsync();
        else await site.Page.Locator("#local").ClickAsync();
        await observation.WaitForLoadStateAsync(LoadState.DOMContentLoaded, 5_000, Ct);
        await observation.WaitForLoadStateAsync(LoadState.Load, 5_000, Ct);
        Assert.Equal(kind != "no_navigation", observation.Triggered);
        Assert.Equal(kind == "redirect" ? 1 : 0, site.Visits.Count(p => p == "/destination"));
        Assert.Equal(kind == "redirect" ? 1 : 0, site.Visits.Count(p => p == "/redirect"));
        if (kind == "same_document") Assert.EndsWith("/#section", site.Page.Url);
        if (kind == "no_navigation") Assert.Equal(1, await site.Page.EvaluateAsync<int>("window.actions"));
    }

    [Fact]
    public async Task NavigationDuringReadinessReadRequiresNewDocumentCompletion()
    {
        await using var site = await Site.CreateAsync(LoadState.Load);
        var first = true;
        using var observation = new NavigationObservation(site.Page)
        {
            ReadinessCheckpoint = async () =>
            {
                if (!first) return;
                first = false;
                await site.Page.GotoAsync(site.Origin + "/destination", new() { WaitUntil = WaitUntilState.Commit });
            }
        };
        observation.Start();
        var waiting = observation.WaitForLoadStateAsync(LoadState.Load, 5_000, Ct);
        await site.ResourceRequested.Task.WaitAsync(Ct);
        Assert.False(waiting.IsCompleted);
        await site.CompleteResourceAsync();
        await waiting;
        Assert.Equal(1, site.Visits.Count(p => p == "/destination"));
    }

    [Fact]
    public async Task NetworkIdleStillRequiresNetworkQuiescence()
    {
        await using var site = await Site.CreateAsync(LoadState.Load);
        using var observation = new NavigationObservation(site.Page);
        observation.Start();
        await site.Page.Locator("#go").ClickAsync();
        await site.ResourceRequested.Task.WaitAsync(Ct);
        await observation.WaitForLoadStateAsync(LoadState.DOMContentLoaded, 5_000, Ct);
        await Assert.ThrowsAsync<TimeoutException>(() => observation.WaitForLoadStateAsync(LoadState.NetworkIdle, 100, Ct));
        await site.CompleteResourceAsync();
        await observation.WaitForLoadStateAsync(LoadState.NetworkIdle, 5_000, Ct);
        Assert.Equal(1, site.Visits.Count(p => p == "/destination"));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("close")]
    [InlineData("already_closed")]
    [InlineData("dispose")]
    public async Task IncompleteDocumentsNeverBecomeSuccessfulOnTermination(string termination)
    {
        await using var site = await Site.CreateAsync(LoadState.DOMContentLoaded);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entered = Signal();
        var continueRead = Signal();
        using var observation = new NavigationObservation(site.Page)
        {
            ReadinessCheckpoint = async () =>
            {
                entered.TrySetResult();
                if (termination == "close") await continueRead.Task.WaitAsync(Ct);
            }
        };
        observation.Start();
        await site.Page.Locator("#go").ClickAsync();
        await site.ResourceRequested.Task.WaitAsync(Ct);
        if (termination == "already_closed") await site.Page.CloseAsync();
        var waiting = observation.WaitForLoadStateAsync(LoadState.DOMContentLoaded, termination == "timeout" ? 100 : 5_000, cancellation.Token);
        if (termination != "already_closed") await entered.Task.WaitAsync(Ct);
        switch (termination)
        {
            case "cancel": await cancellation.CancelAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting); break;
            case "close":
                await site.Page.CloseAsync(); continueRead.TrySetResult();
                var closedDuringRead = await Assert.ThrowsAnyAsync<PlaywrightException>(() => waiting);
                // The SDK's precise closed-target exception is internal to its assembly.
                Assert.Equal("Microsoft.Playwright.TargetClosedException", closedDuringRead.GetType().FullName);
                Assert.Equal("BROWSER_ERROR", BrowserToolFailure.Action("click", "#go", closedDuringRead).ErrorCode);
                Assert.True(site.Page.IsClosed);
                break;
            case "already_closed":
                var closedBeforeRead = await Assert.ThrowsAsync<PlaywrightException>(() => waiting);
                Assert.Equal("BROWSER_ERROR", BrowserToolFailure.Action("click", "#go", closedBeforeRead).ErrorCode);
                Assert.True(site.Page.IsClosed);
                break;
            case "dispose": observation.Dispose(); await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting); break;
            default: await Assert.ThrowsAsync<TimeoutException>(() => waiting); break;
        }
        Assert.Equal(1, site.Visits.Count(p => p == "/destination"));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Site(LoadState state) : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        private readonly List<Task> _responses = [];
        private Task _serving = Task.CompletedTask;
        private IPlaywright _playwright = null!;
        private IBrowser _browser = null!;
        internal IPage Page = null!;
        internal string Origin = "";
        internal readonly ConcurrentQueue<string> Visits = new();
        internal TaskCompletionSource ResourceRequested = Signal();
        internal TaskCompletionSource ResourceRelease = Signal();

        internal static async Task<Site> CreateAsync(LoadState state)
        {
            var site = new Site(state);
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            site.Origin = "http://127.0.0.1:" + port;
            site._listener.Prefixes.Add(site.Origin + "/"); site._listener.Start();
            site._serving = site.ServeAsync();
            site._playwright = await Playwright.CreateAsync();
            site._browser = await site._playwright.Chromium.LaunchAsync(new() { Headless = true });
            site.Page = await site._browser.NewPageAsync();
            await site.Page.GotoAsync(site.Origin);
            return site;
        }

        internal void ResetResource() { ResourceRequested = Signal(); ResourceRelease = Signal(); }

        internal async Task CompleteResourceAsync()
        {
            var loaded = Signal();
            void OnLoad(object? sender, IPage page) => loaded.TrySetResult();
            Page.Load += OnLoad;
            try { ResourceRelease.TrySetResult(); await loaded.Task.WaitAsync(Ct); }
            finally { Page.Load -= OnLoad; }
        }

        private async Task ServeAsync()
        {
            try { while (!_stop.IsCancellationRequested) _responses.Add(RespondAsync(await _listener.GetContextAsync().WaitAsync(_stop.Token))); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath; Visits.Enqueue(path);
            try
            {
                context.Response.Headers["Cache-Control"] = "no-store";
                if (path == "/redirect") { context.Response.StatusCode = 302; context.Response.RedirectLocation = "/destination"; return; }
                if (path == "/resource")
                {
                    ResourceRequested.TrySetResult(); await ResourceRelease.Task.WaitAsync(_stop.Token);
                    context.Response.ContentType = state == LoadState.Load ? "image/svg+xml" : "text/javascript";
                    await WriteAsync(state == LoadState.Load ? "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>" : "window.scriptLoaded = true;");
                    return;
                }
                context.Response.ContentType = "text/html; charset=utf-8";
                await WriteAsync(path == "/destination"
                    ? "<!doctype html><title>Destination</title><body>Observed destination" + (state == LoadState.Load ? "<img src='/resource'>" : "<script defer src='/resource'></script>")
                    : "<!doctype html><title>Origin</title><script>window.actions=0;</script><a id='go' href='/destination'>Follow</a><a id='redirect' href='/redirect'>Redirect</a><a id='fragment' href='#section'>Section</a><button id='local' onclick='actions++'>Activate</button>");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
            finally { context.Response.Close(); }

            async Task WriteAsync(string content)
            {
                var bytes = Encoding.UTF8.GetBytes(content); context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _serving; await Task.WhenAll(_responses);
            _listener.Close();
            await _browser.CloseAsync(); _playwright.Dispose(); _stop.Dispose();
        }
    }
}
