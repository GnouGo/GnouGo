using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserEmptyObservationTests
{
    private const string Blank = "<html><head><title> </title></head><body><script>window.noise = 'not observed';</script><style>body { color: black; }</style><!-- no data --></body></html>";
    private const string Populated = "<html><title>Recovered</title><body><main><p>first</p><p>first</p><a href='/item'>last</a></main></body></html>";
    private static PlaywrightBrowserHost Host() => new(Options.Create(new BrowserServerSettings
        { AllowedHosts = ["127.0.0.1"], MaxObservationRecords = 1 }), NullLogger<PlaywrightBrowserHost>.Instance);
    private static BrowserTools Tools(PlaywrightBrowserHost host) => new(host, NullLogger<BrowserTools>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlankDocumentReloadsOnceAndPublishesOnlyNewGeneration(bool remainsEmpty)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((_, count) => new(200, count == 1 || remainsEmpty ? Blank : Populated));
        await using var host = Host();
        using var activity = new Activity("empty-acquisition").Start();
        var result = await Tools(host).GetContentAsync(url: site.Url, format: "observation_complete", timeoutMs: 5000, cancellationToken: ct);
        Assert.Equal(!remainsEmpty, result.Success);
        Assert.Equal(2, site.Calls.Count(c => c.Path == "/"));
        Assert.All(site.Calls, c => Assert.Equal("GET", c.Method));
        Assert.Equal(200, result.StatusCode); Assert.Equal(site.Url, result.Url);
        var acquisition = Assert.IsType<BrowserSnapshotAcquisition>(result.Acquisition);
        Assert.Equal(2, acquisition.Attempts);
        Assert.Single(acquisition.Recoveries!, r => r.ReloadRequested);
        Assert.All(acquisition.Recoveries!, r => Assert.Equal("empty_document", r.Reason));
        Assert.Equal(result.Title, acquisition.Navigation!.Title);
        Assert.Contains(activity.Events, e => e.Name == "browser.snapshot.empty");
        Assert.DoesNotContain(activity.Events.SelectMany(e => e.Tags), t => t.Key.Contains("url", StringComparison.Ordinal) || t.Key.Contains("content", StringComparison.Ordinal));
        if (remainsEmpty)
        {
            Assert.Equal("OBSERVATION_EMPTY", result.ErrorCode); Assert.Null(result.ObservationSnapshot);
            Assert.Equal(2, acquisition.Recoveries!.Count); Assert.Empty(result.Content);
        }
        else
        {
            Assert.Equal("Recovered", result.Title);
            var snapshot = Assert.IsType<BrowserObservationSnapshot>(result.ObservationSnapshot);
            Assert.Equal(new[] { "first", "first", "last" }, snapshot.Pages.SelectMany(p => p.Records).Select(r => r.Text));
            Assert.All(snapshot.Pages, p => Assert.Equal(snapshot.Id, p.Id));
            Assert.DoesNotContain(acquisition.Invalidations, i => i.SnapshotId == snapshot.Id);
        }
        foreach (var recovery in acquisition.Recoveries!)
        {
            var old = await Tools(host).GetContentAsync(format: "observation", cursor: recovery.SnapshotId + ":page:0", cancellationToken: ct);
            Assert.Equal("SNAPSHOT_EXPIRED", old.ErrorCode);
            Assert.Equal("empty_document", Assert.Single(old.Acquisition!.Invalidations).Reason);
        }
        var json = JsonSerializer.Serialize(result, BrowserMcpJsonContext.Default.BrowserContentResult);
        Assert.Contains("empty_document", json); Assert.DoesNotContain("not observed", json);
        var recovered = JsonSerializer.Deserialize(json, BrowserMcpJsonContext.Default.BrowserContentResult)!;
        Assert.Equal(result.Acquisition.Navigation, recovered.Acquisition!.Navigation);
    }

    [Theory]
    [InlineData("<main></main><p>Elsewhere</p>")]
    [InlineData("<main></main><img alt='' src='data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7'>")]
    [InlineData("<main></main><canvas></canvas>")]
    [InlineData("<main></main><svg></svg>")]
    [InlineData("<main></main><iframe src='about:blank'></iframe>")]
    [InlineData("<main></main><button></button>")]
    [InlineData("<title>Intentionally empty</title><main></main>")]
    public async Task EmptySelectedRegionOrNonTextContentDoesNotReload(string content)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((_, _) => new(200, "<html><body>" + content + "</body></html>"));
        await using var host = Host();
        var result = await Tools(host).GetContentAsync(url: site.Url, selector: "main", format: "observation_complete", cancellationToken: ct);
        Assert.True(result.Success, result.ErrorMessage); Assert.Empty(result.ObservationSnapshot!.Pages);
        Assert.Null(result.Acquisition!.Recoveries); Assert.Equal(1, result.Acquisition.Attempts);
        Assert.Single(site.Calls, c => c.Path == "/");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task BlankHttpFailureIsPreservedWithoutReload(int status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((_, _) => new(status, Blank));
        await using var host = Host();
        var result = await Tools(host).GetContentAsync(url: site.Url, format: "observation_complete", cancellationToken: ct);
        Assert.Equal("OBSERVATION_EMPTY", result.ErrorCode); Assert.Equal(status, result.StatusCode);
        Assert.Equal(site.Url, result.Url); Assert.False(Assert.Single(result.Acquisition!.Recoveries!).ReloadRequested);
        Assert.Single(site.Calls, c => c.Path == "/");
    }

    [Theory]
    [InlineData(204)]
    [InlineData(205)]
    public async Task IntentionalNoContentDoesNotReload(int status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((request, _) => request.Url!.AbsolutePath == "/warm" ? new(200, Populated) : new(status, ""));
        await using var host = Host();
        await Tools(host).GetContentAsync(url: site.Url + "warm", format: "html", cancellationToken: ct);
        var result = await Tools(host).GetContentAsync(url: site.Url, format: "observation_complete", timeoutMs: 1000, cancellationToken: ct);
        Assert.False(result.Success); Assert.Null(result.ObservationSnapshot);
        Assert.Single(site.Calls, c => c.Path == "/");
        Assert.Equal(status, result.Acquisition!.LastResponse!.StatusCode);
        Assert.True(result.Acquisition.Recoveries is null || result.Acquisition.Recoveries.All(r => !r.ReloadRequested));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentDocumentAndPostInteractionReadsNeverReload(bool interact)
    {
        var ct = TestContext.Current.CancellationToken;
        var html = interact ? "<html><body><button onclick=\"document.body.replaceChildren()\">Clear</button></body></html>" : Blank;
        await using var site = new Site((_, _) => new(200, html));
        await using var host = Host();
        await Tools(host).GetContentAsync(url: site.Url, format: "html", cancellationToken: ct);
        if (interact) await host.ClickAsync("button", "load", 1000, ct);
        var result = await Tools(host).GetContentAsync(format: "observation_complete", cancellationToken: ct);
        Assert.Equal("OBSERVATION_EMPTY", result.ErrorCode); Assert.Null(result.StatusCode);
        Assert.False(Assert.Single(result.Acquisition!.Recoveries!).ReloadRequested);
        Assert.Single(site.Calls, c => c.Path == "/");
    }

    [Fact]
    public async Task RedirectThenReloadRetainsOnlyFinalDocumentStatusAndIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((request, count) => request.Url!.AbsolutePath == "/"
            ? new(302, "", "/destination") : new(count == 1 ? 200 : 202, count == 1 ? Blank : Populated));
        await using var host = Host();
        var result = await Tools(host).GetContentAsync(url: site.Url, format: "observation_complete", cancellationToken: ct);
        Assert.True(result.Success, result.ErrorMessage); Assert.Equal(202, result.StatusCode);
        Assert.Equal(site.Url + "destination", result.Url); Assert.Equal("Recovered", result.Title);
        Assert.Equal(200, Assert.Single(result.Acquisition!.Recoveries!).Navigation.StatusCode);
        Assert.NotEqual(result.Acquisition.Recoveries![0].Navigation.Generation, result.Acquisition.Navigation!.Generation);
        Assert.Single(site.Calls, c => c.Path == "/"); Assert.Equal(2, site.Calls.Count(c => c.Path == "/destination"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadDoesNotResetDeadlineOrIgnoreCancellation(bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var site = new Site((_, _) => new(200, Blank));
        var captures = 0;
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (_, phase, token) =>
            {
                if (phase != "capture" || ++captures != 2) return;
                if (cancel) cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        await Tools(host).GetContentAsync(url: site.Url + "warm", format: "html", cancellationToken: ct);
        var timer = Stopwatch.StartNew();
        var result = await Tools(host).GetContentAsync(url: site.Url, format: "observation_complete", timeoutMs: 750, cancellationToken: cancellation.Token);
        Assert.Equal(cancel ? "CANCELLED" : "TIMEOUT", result.ErrorCode);
        Assert.Null(result.ObservationSnapshot); Assert.Equal(2, result.Acquisition!.Attempts);
        Assert.Single(result.Acquisition.Recoveries!); Assert.Equal(2, site.Calls.Count(c => c.Path == "/"));
        Assert.InRange(timer.ElapsedMilliseconds, 0, 2000);
    }

    [Fact]
    public async Task SubmittedDocumentIsNotReloadRequestedEvenInsideAnExplicitAcquisition()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((request, _) => new(200, request.HttpMethod == "POST" ? Blank :
            "<html><body><form method='post'><button>Submit</button></form></body></html>"));
        var submitted = false;
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (page, phase, token) =>
            {
                if (submitted || phase != "capture") return;
                submitted = true;
                await page.Locator("button").ClickAsync().WaitAsync(token);
                await page.WaitForURLAsync(site.Url).WaitAsync(token);
            }
        };
        var result = await Tools(host).GetContentAsync(url: site.Url, format: "observation_complete", cancellationToken: ct);
        Assert.Equal("OBSERVATION_EMPTY", result.ErrorCode);
        Assert.Equal("POST", result.Acquisition!.Navigation!.Method);
        Assert.False(Assert.Single(result.Acquisition.Recoveries!).ReloadRequested);
        Assert.Single(site.Calls, c => c.Method == "POST");
        Assert.Single(site.Calls, c => c.Method == "GET" && c.Path == "/");
    }

    [Fact]
    public async Task EmptyRecoverySharesTheThreeAttemptCeilingWithNavigationInvalidation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((request, _) => new(200, request.Url!.AbsolutePath == "/blank" ? Blank : Populated));
        var captures = 0;
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (page, phase, token) =>
            {
                if (phase != "capture") return;
                if (++captures <= 2) await page.GotoAsync(site.Url + (captures == 1 ? "other" : "blank")).WaitAsync(token);
            }
        };
        var result = await Tools(host).GetContentAsync(url: site.Url, format: "observation_complete", cancellationToken: ct);
        Assert.Equal("OBSERVATION_EMPTY", result.ErrorCode); Assert.Equal(3, result.Acquisition!.Attempts);
        Assert.False(Assert.Single(result.Acquisition.Recoveries!).ReloadRequested);
        Assert.Single(site.Calls, c => c.Path == "/blank"); Assert.Null(result.ObservationSnapshot);
    }

    [Fact]
    public async Task UnknownSameDocumentNavigationCannotBorrowAnEarlierGetResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((_, _) => new(200, Blank));
        await using var host = Host();
        await Tools(host).GetContentAsync(url: site.Url, format: "html", cancellationToken: ct);
        var result = await Tools(host).GetContentAsync(url: site.Url + "#state", format: "observation_complete", cancellationToken: ct);
        Assert.Equal("OBSERVATION_EMPTY", result.ErrorCode); Assert.Null(result.StatusCode);
        Assert.Null(result.Acquisition!.Navigation!.Method); Assert.Null(result.Acquisition.LastResponse);
        Assert.False(Assert.Single(result.Acquisition.Recoveries!).ReloadRequested);
        Assert.Single(site.Calls, c => c.Path == "/");
    }

    [Fact]
    public async Task FailedReloadPreservesOriginalEvidenceAndDoesNotRetryAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site((_, _) => new(200, Blank));
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (page, phase, token) =>
            {
                if (phase == "capture") await page.RouteAsync("**/*", route => route.AbortAsync()).WaitAsync(token);
            }
        };
        var result = await Tools(host).GetContentAsync(url: site.Url, format: "observation_complete", cancellationToken: ct);
        Assert.Equal("BROWSER_ERROR", result.ErrorCode); Assert.Null(result.ObservationSnapshot);
        Assert.True(Assert.Single(result.Acquisition!.Recoveries!).ReloadRequested);
        Assert.Equal(200, result.Acquisition.LastResponse!.StatusCode);
        Assert.Single(site.Calls, c => c.Path == "/");
    }

    [Fact]
    public async Task DirectHostCancellationKeepsItsExceptionContractAndAcquisitionEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var site = new Site((_, _) => new(200, Blank));
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = (_, _, token) => { cancelled.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.GetContentAsync(site.Url, "load", 5000, null,
            "observation_complete", null, false, cancelled.Token));
        var result = BrowserToolFailure.Content(site.Url, null, "observation_complete", error);
        Assert.Equal("CANCELLED", result.ErrorCode); Assert.NotNull(result.Acquisition);
        Assert.Single(site.Calls, c => c.Path == "/");
    }

    [Fact]
    public void LegacyAcquisitionSerializationOmitsNewOptionalMetadata()
    {
        var result = new BrowserContentResult("", "", null, null, "body", false, null, "observation_complete", "", false, 24000)
            { Acquisition = new(1, []) };
        var json = JsonSerializer.Serialize(result, BrowserMcpJsonContext.Default.BrowserContentResult);
        Assert.DoesNotContain("recoveries", json); Assert.DoesNotContain("navigation", json); Assert.DoesNotContain("lastResponse", json);
    }

    private sealed record Response(int Status, string Html, string? Redirect = null);
    private sealed class Site : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;
        public string Url { get; }
        public ConcurrentQueue<(string Path, string Method)> Calls { get; } = new();
        public Site(Func<HttpListenerRequest, int, Response> response)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = "http://127.0.0.1:" + port + "/";
            _listener.Prefixes.Add(Url); _listener.Start();
            _serving = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                    var path = context.Request.Url!.AbsolutePath;
                    Calls.Enqueue((path, context.Request.HttpMethod));
                    var result = response(context.Request, Calls.Count(c => c.Path == path));
                    context.Response.StatusCode = result.Status;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    if (result.Redirect is not null) context.Response.RedirectLocation = result.Redirect;
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(result.Html));
                    context.Response.Close();
                }
            });
        }
        public async ValueTask DisposeAsync() { _listener.Close(); await _serving; }
    }
}
