using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserSemanticOccurrenceTests
{
    [Theory]
    [InlineData("article", "")]
    [InlineData("li", "")]
    [InlineData("div", "role='listitem'")]
    [InlineData("div", "role='article'")]
    [InlineData("div", "role='row'")]
    public async Task CompleteSnapshotPreservesDistinctOccurrencesAndAssociatedLinks(string tag, string attributes)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            var html = "<html><body><main>" + string.Concat(Enumerable.Range(0, 3).Select(index =>
                $"<{tag} {attributes} id='occurrence-{index}'><a href='/item/shared'><img alt='Photo {index}'></a>" +
                $"<h2><a href='/item/shared'>Observed {index}</a></h2><p>Occurrence {index}</p></{tag}>")) + "</main></body></html>";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(html), ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"], MaxObservationRecords = 2 }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin, "domcontentloaded", 10000, null, "observation_complete", 2400, false, ct);
        Assert.True(result.Success); Assert.False(result.Truncated);
        var snapshot = Assert.IsType<BrowserObservationSnapshot>(result.ObservationSnapshot);
        Assert.False(snapshot.CaptureTruncated); Assert.False(snapshot.ManifestTruncated);
        Assert.True(snapshot.Pages.Count > 1);
        var records = snapshot.Pages.SelectMany(page => page.Records).ToArray();
        var links = records.Where(record => record.Kind == "link").ToArray();
        Assert.Equal(6, links.Length);
        Assert.Equal(new[] { "#occurrence-0", "#occurrence-0", "#occurrence-1", "#occurrence-1", "#occurrence-2", "#occurrence-2" }, links.Select(link => link.Group));
        Assert.All(links, link => Assert.Equal(site.Origin + "/item/shared", link.Href));
        Assert.Equal(records.Length, records.Select(record => record.Reference).Distinct().Count());
        Assert.All(records, record => Assert.StartsWith(snapshot.Id + ":record:", record.Reference));
        for (var index = 0; index < 3; index++)
        {
            Assert.Contains(records, record => record.Group == "#occurrence-" + index && record.Text == "Occurrence " + index);
            Assert.All(records.Where(record => record.Group == "#occurrence-" + index), record =>
            {
                Assert.Equal(tag, record.GroupContext!.Tag);
                Assert.Equal(attributes.Length == 0 ? null : attributes[6..^1], record.GroupContext.Role);
                Assert.Equal(index, record.GroupContext.Index);
            });
        }
    }

    [Fact]
    public async Task ForbiddenRedirectDestinationIsRejectedBeforeItReceivesARequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var forbiddenRequests = 0;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            if (context.Request.Path == "/redirect")
                context.Response.Redirect("http://localhost:" + context.Connection.LocalPort + "/forbidden");
            else
            {
                Interlocked.Increment(ref forbiddenRequests);
                await context.Response.WriteAsync("<html><body>Forbidden destination</body></html>", ct);
            }
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var result = await tools.GetContentAsync(url: site.Origin + "/redirect", waitUntil: "domcontentloaded",
            timeoutMs: 10000, format: "observation_complete", cancellationToken: ct);
        Assert.False(result.Success);
        Assert.Equal("NAVIGATION_POLICY_DENIED", result.ErrorCode);
        Assert.Equal(0, Volatile.Read(ref forbiddenRequests));
        Assert.Null(result.ObservationSnapshot);
        Assert.False(result.NavigationEvidence!.Complete);
        Assert.Null(result.NavigationEvidence.FinalUrl);
        Assert.Equal(302, Assert.Single(result.NavigationEvidence.Hops).StatusCode);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("html")]
    [InlineData("observation")]
    [InlineData("observation_pages")]
    [InlineData("observation_complete")]
    public async Task RedirectEvidenceBindsEachObservedResponseToTheActualFinalDocument(string format)
    {
        var ct = TestContext.Current.CancellationToken;
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            var path = context.Request.Path.Value!; counts.AddOrUpdate(path, 1, (_, count) => count + 1);
            if (path == "/start")
            {
                context.Response.Cookies.Append("observation", "retained");
                context.Response.Redirect("/step");
            }
            else if (path == "/step")
            {
                Assert.Equal("retained", context.Request.Cookies["observation"]);
                context.Response.StatusCode = 307; context.Response.Headers.Location = "final";
            }
            else if (path == "/final")
            {
                Assert.Equal("retained", context.Request.Cookies["observation"]);
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<html><head><title>Final observation</title></head><body><article><p>Observed destination</p></article></body></html>", ct);
            }
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin + "/start", "domcontentloaded", 10000, null, format, null, false, ct);
        Assert.True(result.Success); Assert.Equal(site.Origin + "/final", result.Url);
        var evidence = Assert.IsType<BrowserNavigationEvidence>(result.NavigationEvidence);
        Assert.True(evidence.Complete); Assert.Equal(result.Url, evidence.FinalUrl);
        Assert.Equal(site.Origin + "/start", evidence.RequestedUrl);
        Assert.Equal(new[] { 302, 307, 200 }, evidence.Hops.Select(hop => hop.StatusCode));
        Assert.Equal(new[] { "/start", "/step", "/final" }, evidence.Hops.Select(hop => new Uri(hop.Url).AbsolutePath));
        Assert.Equal(new string?[] { "/step", "final", null }, evidence.Hops.Select(hop => hop.Location));
        Assert.All(evidence.Hops, hop => Assert.Equal("GET", hop.Method));
        Assert.All(new[] { "/start", "/step", "/final" }, path => Assert.Equal(1, counts[path]));
    }

    [Fact]
    public async Task ARedirectLoopRetainsItsObservedHopsWithoutRevisitingAnEarlierUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var requests = 0;
        await using var site = await LoopbackTestServer.StartAsync(context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.Redirect(context.Request.Path == "/first" ? "/second" : "/first");
            return Task.CompletedTask;
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var result = await tools.GetContentAsync(url: site.Origin + "/first", waitUntil: "domcontentloaded",
            timeoutMs: 10000, format: "observation_complete", cancellationToken: ct);
        Assert.False(result.Success); Assert.Equal("NAVIGATION_REDIRECT_LOOP", result.ErrorCode);
        Assert.Equal(2, Volatile.Read(ref requests));
        Assert.False(result.NavigationEvidence!.Complete); Assert.Null(result.NavigationEvidence.FinalUrl);
        Assert.Equal(2, result.NavigationEvidence.Hops.Count); Assert.Null(result.ObservationSnapshot);
    }

    [Fact]
    public async Task SameHostRedirectCannotEscapeConfiguredNavigationRoutes()
    {
        var ct = TestContext.Current.CancellationToken; var forbidden = 0;
        await using var site = await LoopbackTestServer.StartAsync(context =>
        {
            if (context.Request.Path == "/start") context.Response.Redirect("/forbidden");
            else Interlocked.Increment(ref forbidden);
            return Task.CompletedTask;
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
        {
            AllowedHosts = ["127.0.0.1"],
            AllowedNavigationUrlPatterns = [System.Text.RegularExpressions.Regex.Escape(site.Origin) + "/(?:start|allowed)"]
        }), NullLogger<PlaywrightBrowserHost>.Instance);
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        var result = await tools.GetContentAsync(url: site.Origin + "/start", waitUntil: "domcontentloaded",
            timeoutMs: 10000, format: "observation_complete", cancellationToken: ct);
        Assert.False(result.Success); Assert.Equal("NAVIGATION_POLICY_DENIED", result.ErrorCode);
        Assert.Equal(0, Volatile.Read(ref forbidden));
        Assert.Equal("/forbidden", Assert.Single(result.NavigationEvidence!.Hops).Location);
    }

    [Fact]
    public async Task RedirectLimitStopsBeforeSendingTheNextRequest()
    {
        var ct = TestContext.Current.CancellationToken; var requests = 0;
        await using var site = await LoopbackTestServer.StartAsync(context =>
        {
            var number = int.Parse(context.Request.Path.Value![1..], System.Globalization.CultureInfo.InvariantCulture);
            Interlocked.Increment(ref requests); context.Response.Redirect("/" + (number + 1)); return Task.CompletedTask;
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await new BrowserTools(host, NullLogger<BrowserTools>.Instance).GetContentAsync(url: site.Origin + "/0",
            waitUntil: "domcontentloaded", timeoutMs: 10000, format: "observation_complete", cancellationToken: ct);
        Assert.False(result.Success); Assert.Equal("NAVIGATION_REDIRECT_LIMIT", result.ErrorCode);
        Assert.Equal(21, requests); Assert.Equal(21, result.NavigationEvidence!.Hops.Count);
        Assert.False(result.NavigationEvidence.Complete); Assert.Null(result.NavigationEvidence.FinalUrl);
    }

    [Fact]
    public async Task APostInteractionIsNotReplayedOrFollowedThroughAnAutomaticRedirect()
    {
        var ct = TestContext.Current.CancellationToken; var posts = 0; var followed = 0;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            if (context.Request.Method == "POST")
            { Interlocked.Increment(ref posts); context.Response.StatusCode = 303; context.Response.Headers.Location = "/destination"; }
            else if (context.Request.Path == "/destination") Interlocked.Increment(ref followed);
            else
            {
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<html><body><form method='post'><button>Submit</button></form></body></html>", ct);
            }
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance);
        Assert.True((await tools.GetContentAsync(url: site.Origin, waitUntil: "domcontentloaded", cancellationToken: ct)).Success);
        var action = await tools.ClickAsync("button", timeoutMs: 1000, cancellationToken: ct);
        Assert.False(action.Success); Assert.Equal(1, posts); Assert.Equal(0, followed);
    }
}
