using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserCompleteObservationTests
{
    [Theory]
    [InlineData("capture", false)]
    [InlineData("publish", false)]
    [InlineData("publish", true)]
    public async Task NavigationDiscardsWholeGenerationAndRetriesWithoutRenavigation(string phase, bool sameUrl)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site();
        var injected = false;
        using var activity = new Activity("snapshot-acquisition-test").Start();
        await using var host = new PlaywrightBrowserHost(Options.Create(Settings()), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (page, checkpoint, token) =>
            {
                if (injected || checkpoint != phase) return;
                injected = true;
                site.Label = "new";
                if (sameUrl) await page.ReloadAsync().WaitAsync(token);
                else await page.GotoAsync(site.Origin + "/new").WaitAsync(token);
            }
        };
        var result = await host.GetContentAsync(site.Origin, "load", 5000, "main", "observation_complete", 2400, false, ct, maxRecords: 2);
        Assert.True(result.Success); Assert.False(result.Truncated); Assert.Empty(result.Content);
        Assert.Null(result.ObservationManifest); Assert.Null(result.Observation); Assert.Equal(200, result.StatusCode);
        Assert.Equal(result.Url, result.Acquisition!.Navigation!.Url);
        Assert.Equal("GET", result.Acquisition.Navigation.Method);
        var acquisition = Assert.IsType<BrowserSnapshotAcquisition>(result.Acquisition);
        Assert.Equal(2, acquisition.Attempts);
        var discarded = Assert.Single(acquisition.Invalidations);
        Assert.Equal("navigation", discarded.Reason);
        Assert.Contains(activity.Events, e => e.Name == "browser.snapshot.invalidated" && e.Tags.Any(t => t.Key == "browser.snapshot.reason" && Equals(t.Value, "navigation")));
        Assert.DoesNotContain(activity.Events.SelectMany(e => e.Tags), t => t.Key.Contains("url", StringComparison.Ordinal) || t.Key.Contains("content", StringComparison.Ordinal));
        var snapshot = Assert.IsType<BrowserObservationSnapshot>(result.ObservationSnapshot);
        Assert.NotEqual(discarded.SnapshotId, snapshot.Id);
        Assert.Equal(9, snapshot.RecordCount); Assert.True(snapshot.Pages.Count > 3);
        var records = snapshot.Pages.SelectMany(p => p.Records).ToArray();
        Assert.Equal(Enumerable.Range(0, 9).Select(i => "new " + i), records.Select(r => r.Text));
        Assert.All(snapshot.Pages, p => { Assert.Equal(snapshot.Id, p.Id); Assert.Null(p.NextCursor); Assert.False(p.CaptureTruncated); });
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var expired = await tools.GetContentAsync(format: "observation", cursor: discarded.SnapshotId + ":page:0", cancellationToken: ct);
        Assert.Equal("SNAPSHOT_EXPIRED", expired.ErrorCode);
        Assert.Equal(discarded, Assert.Single(expired.Acquisition!.Invalidations));
        Assert.Equal(sameUrl ? 2 : 1, site.Visits.Count(p => p == "/")); // explicit reload only, never recovery re-navigation
    }

    [Fact]
    public async Task RepeatedNavigationStopsAfterThreeAttemptsWithNoPartialResult()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site();
        var transitions = 0;
        await using var host = new PlaywrightBrowserHost(Options.Create(Settings()), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (page, phase, token) =>
            {
                if (phase == "publish") await page.GotoAsync(site.Origin + "/move" + ++transitions).WaitAsync(token);
            }
        };
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var result = await tools.GetContentAsync(url: site.Origin, format: "observation_complete", timeoutMs: 5000, cancellationToken: ct);
        Assert.False(result.Success); Assert.Equal("SNAPSHOT_EXPIRED", result.ErrorCode);
        Assert.Null(result.ObservationSnapshot); Assert.Empty(result.Content);
        Assert.Equal(3, result.Acquisition!.Attempts); Assert.Equal(3, transitions);
        Assert.Equal(3, result.Acquisition.Invalidations.Select(i => i.SnapshotId).Distinct().Count());
        Assert.Equal(1, site.Visits.Count(p => p == "/"));
    }

    [Fact]
    public async Task AcquisitionDeadlineIsSharedAndCancellationIsNotRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site();
        await using var host = new PlaywrightBrowserHost(Options.Create(Settings()), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = (_, _, token) => Task.Delay(Timeout.Infinite, token)
        };
        // Launch before measuring the bounded acquisition.
        await host.GetContentAsync(site.Origin, "load", 5000, "main", "text", 1000, false, ct);
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var timer = Stopwatch.StartNew();
        var result = await tools.GetContentAsync(format: "observation_complete", timeoutMs: 200, cancellationToken: ct);
        Assert.Equal("TIMEOUT", result.ErrorCode); Assert.Equal(1, result.Acquisition!.Attempts);
        Assert.InRange(timer.ElapsedMilliseconds, 100, 2000);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancellation = await tools.GetContentAsync(format: "observation_complete", cancellationToken: cancelled.Token);
        Assert.Equal("CANCELLED", cancellation.ErrorCode);
        Assert.Null(cancellation.ObservationSnapshot);
    }

    [Fact]
    public async Task RestartSharesDeadlineAndNeverFallsBackToBroaderSelector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site();
        var moved = false;
        await using var host = new PlaywrightBrowserHost(Options.Create(Settings()), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (page, phase, token) =>
            {
                if (moved) await Task.Delay(Timeout.Infinite, token);
                else if (phase == "publish") { moved = true; await page.GotoAsync(site.Origin + "/new").WaitAsync(token); }
            }
        };
        await host.GetContentAsync(site.Origin, "load", 5000, "main", "text", 1000, false, ct);
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var timer = Stopwatch.StartNew();
        var result = await tools.GetContentAsync(format: "observation_complete", timeoutMs: 600, cancellationToken: ct);
        Assert.Equal("TIMEOUT", result.ErrorCode); Assert.Equal(2, result.Acquisition!.Attempts);
        Assert.Equal("navigation", Assert.Single(result.Acquisition.Invalidations).Reason);
        Assert.InRange(timer.ElapsedMilliseconds, 400, 1800);
        var missing = await tools.GetContentAsync(format: "observation_complete", selector: "#absent", timeoutMs: 150, cancellationToken: ct);
        Assert.False(missing.Success); Assert.Null(missing.ObservationSnapshot);
        Assert.Equal(1, missing.Acquisition!.Attempts);
    }

    [Fact]
    public async Task ClosureIsNotRetriedAndRedirectedReadsStillRequireAllowedHost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site();
        foreach (var cause in new[] { "closure", "disallowed" })
        {
            await using var host = new PlaywrightBrowserHost(Options.Create(Settings()), NullLogger<PlaywrightBrowserHost>.Instance)
            {
                ObservationCheckpoint = async (page, phase, token) =>
                {
                    if (phase != "publish") return;
                    if (cause == "closure") await page.CloseAsync().WaitAsync(token);
                    else await page.GotoAsync("about:blank").WaitAsync(token);
                }
            };
            var result = await new BrowserTools(host, NullLogger<BrowserTools>.Instance).GetContentAsync(url: site.Origin,
                format: "observation_complete", timeoutMs: 5000, cancellationToken: ct);
            Assert.False(result.Success); Assert.Null(result.ObservationSnapshot);
            Assert.Equal(cause == "closure" ? "SNAPSHOT_EXPIRED" : "INVALID_INPUT", result.ErrorCode);
            Assert.Equal(cause == "closure" ? 1 : 2, result.Acquisition!.Attempts);
        }
    }

    [Theory]
    [InlineData("empty", true)]
    [InlineData("limit", false)]
    [InlineData("capture", false)]
    [InlineData("oversized", false)]
    public async Task CompletenessAndOriginalLimitsAreEnforced(string variant, bool succeeds)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site();
        if (variant == "capture") site.Body = "<p>" + new string('x', 2_000_100) + "</p>";
        if (variant == "oversized") site.Body = "<p>" + new string('x', 4000) + "</p>";
        if (variant == "empty") site.Body = "";
        var settings = Settings(); if (variant == "limit") settings.MaxObservationPages = 2;
        await using var host = new PlaywrightBrowserHost(Options.Create(settings), NullLogger<PlaywrightBrowserHost>.Instance);
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var result = await tools.GetContentAsync(url: site.Origin, format: "observation_complete", selector: "main", maxRecords: 1, maxCharacters: 2400, cancellationToken: ct);
        Assert.Equal(succeeds, result.Success);
        if (succeeds) { Assert.Empty(result.ObservationSnapshot!.Pages); Assert.Equal(0, result.ObservationSnapshot.RecordCount); }
        else { Assert.Null(result.ObservationSnapshot); Assert.Empty(result.Content); Assert.NotNull(result.ErrorCode); }
        Assert.Equal(1, site.Visits.Count(p => p == "/"));
    }

    [Theory]
    [InlineData("interaction")]
    [InlineData("closure")]
    [InlineData("replacement")]
    public async Task KnownExpirationRetainsCauseWhileUnknownCursorsRemainInvalid(string cause)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site();
        await using var host = new PlaywrightBrowserHost(Options.Create(Settings()), NullLogger<PlaywrightBrowserHost>.Instance);
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var manifest = await tools.GetContentAsync(url: site.Origin, format: "observation_pages", selector: "main", cancellationToken: ct);
        var cursor = manifest.ObservationManifest!.Pages[0].Cursor;
        if (cause == "interaction") await host.ClickAsync("button", "load", 1000, ct);
        else if (cause == "closure") await host.CloseAsync(ct);
        else await tools.GetContentAsync(format: "observation_pages", cancellationToken: ct);
        var expired = await tools.GetContentAsync(format: "observation", cursor: cursor, cancellationToken: ct);
        Assert.Equal("SNAPSHOT_EXPIRED", expired.ErrorCode);
        Assert.Equal(cause, Assert.Single(expired.Acquisition!.Invalidations).Reason);
        Assert.Equal(manifest.ObservationManifest.Id, expired.Acquisition.Invalidations[0].SnapshotId);
        foreach (var invalid in new[] { "invalid", Guid.NewGuid().ToString("N") + ":page:0" })
            Assert.Equal("INVALID_INPUT", (await tools.GetContentAsync(format: "observation", cursor: invalid, cancellationToken: ct)).ErrorCode);
        await using var other = new PlaywrightBrowserHost(Options.Create(Settings()), NullLogger<PlaywrightBrowserHost>.Instance);
        Assert.Equal("INVALID_INPUT", (await new BrowserTools(other, NullLogger<BrowserTools>.Instance).GetContentAsync(format: "observation", cursor: cursor, cancellationToken: ct)).ErrorCode);
        var json = JsonSerializer.Serialize(expired, BrowserMcpJsonContext.Default.BrowserContentResult);
        Assert.Contains("SNAPSHOT_EXPIRED", json); Assert.Contains(cause, json); Assert.DoesNotContain("old 0", json);
    }

    [Theory]
    [InlineData(100, 1, 100)]
    [InlineData(24000, 0, 100)]
    [InlineData(24000, 1, 0)]
    public async Task InvalidLimitsFailBeforeNavigation(int characters, int records, int pages)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = new Site();
        var settings = Settings(); settings.MaxObservationPages = pages;
        await using var host = new PlaywrightBrowserHost(Options.Create(settings), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await new BrowserTools(host, NullLogger<BrowserTools>.Instance).GetContentAsync(url: site.Origin,
            format: "observation_complete", maxCharacters: characters, maxRecords: records, cancellationToken: ct);
        Assert.Equal("INVALID_INPUT", result.ErrorCode); Assert.Equal(0, result.Acquisition!.Attempts);
        Assert.Empty(site.Visits);
    }

    private static BrowserServerSettings Settings() => new() { AllowedHosts = ["127.0.0.1"], MaxObservationRecords = 2 };

    private sealed class Site : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;
        public string Origin { get; }
        public string Label { get; set; } = "old";
        public string? Body { get; set; }
        public List<string> Visits { get; } = [];
        public int TotalRequests => Visits.Count;
        public Site()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Origin = "http://127.0.0.1:" + port;
            _listener.Prefixes.Add(Origin + "/"); _listener.Start();
            _serving = Task.Run(async () =>
            {
                try
                {
                    while (_listener.IsListening)
                    {
                        var context = await _listener.GetContextAsync();
                        Visits.Add(context.Request.Url!.AbsolutePath);
                        var body = Body ?? string.Concat(Enumerable.Range(0, 9).Select(i => "<p>" + Label + " " + i + "</p>"));
                        var html = "<html><head><title>Observed " + Label + "</title></head><body><main>" + body + "</main><button>Change</button></body></html>";
                        context.Response.ContentType = "text/html; charset=utf-8";
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html)); context.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
            });
        }
        public async ValueTask DisposeAsync() { _listener.Close(); await _serving; }
    }
}
