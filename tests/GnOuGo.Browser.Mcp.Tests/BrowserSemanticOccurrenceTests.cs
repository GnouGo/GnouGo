using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserSemanticOccurrenceTests
{
    [Theory]
    [InlineData("html", false)]
    [InlineData("html", true)]
    [InlineData("observation_complete", false)]
    [InlineData("observation_complete", true)]
    public async Task InterruptedReadRetainsOnlyItsObservedIncompleteHops(string format, bool redirect)
    {
        var ct = TestContext.Current.CancellationToken; var requests = 0;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            if (context.Request.Path is not { Value: "/start" or "/final" }) { context.Response.StatusCode = 204; return; }
            Interlocked.Increment(ref requests);
            if (context.Request.Path == "/start") { context.Response.Redirect("/final"); return; }
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body>Observed document</body></html>", ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            NavigationReadCheckpoint = (_, _) => throw new Microsoft.Playwright.PlaywrightException("net::ERR_ABORTED")
        };
        var tools = new BrowserTools(host, NullLogger<BrowserTools>.Instance); var url = site.Origin + (redirect ? "/start" : "/final");
        var result = await tools.GetContentAsync(url: url, waitUntil: "domcontentloaded", timeoutMs: 10000, format: format, cancellationToken: ct);
        Assert.False(result.Success); Assert.Equal("BROWSER_ERROR", result.ErrorCode);
        Assert.Null(result.ObservationSnapshot); Assert.Empty(result.Content);
        var evidence = Assert.IsType<BrowserNavigationEvidence>(result.NavigationEvidence);
        Assert.Equal(url, evidence.RequestedUrl); Assert.False(evidence.Complete); Assert.Null(evidence.FinalUrl); Assert.Null(evidence.DocumentUrl);
        Assert.Equal(redirect ? new[] { 302, 200 } : [200], evidence.Hops.Select(h => h.StatusCode));
        Assert.Equal(redirect ? 2 : 1, requests); Assert.All(evidence.Hops, h => Assert.Equal("GET", h.Method));
        if (format == "observation_complete") Assert.Equal("net::ERR_ABORTED", result.Acquisition!.FailureCategory);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DeniedUncommittedNavigationPreservesOnlyTheCurrentDocumentResponse(bool post, bool changeHistory)
    {
        var ct = TestContext.Current.CancellationToken; var forbidden = 0; var attempted = false;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            if (context.Request.Path == "/forbidden") { Interlocked.Increment(ref forbidden); return; }
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><head><title>Committed source</title></head><body><main>Observed source document</main></body></html>", ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
        {
            AllowedHosts = ["127.0.0.1"],
            AllowedNavigationUrlPatterns = [System.Text.RegularExpressions.Regex.Escape(site.Origin) + @"/(?:\?canonical=1)?"]
        }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (page, phase, token) =>
            {
                if (phase != "capture" || attempted) return;
                attempted = true;
                var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Failure(object? sender, Microsoft.Playwright.IRequest request)
                {
                    if (request.IsNavigationRequest && request.Frame == page.MainFrame && request.Url.EndsWith("/forbidden", StringComparison.Ordinal))
                        failed.TrySetResult();
                }
                page.RequestFailed += Failure;
                try
                {
                    await page.EvaluateAsync(post
                        ? "() => { const f=document.createElement('form'); f.method='POST'; f.action='/forbidden'; document.body.append(f); f.submit(); }"
                        : "() => location.assign('/forbidden')").WaitAsync(token);
                    await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    if (changeHistory) await page.EvaluateAsync("history.replaceState(null, '', '/?canonical=1')").WaitAsync(token);
                }
                finally { page.RequestFailed -= Failure; }
            }
        };
        var initial = site.Origin + "/";
        var result = await host.GetContentAsync(initial, "domcontentloaded", 10000, null, "observation_complete", null, false, ct);
        Assert.True(result.Success); Assert.True(attempted); Assert.Equal(0, forbidden);
        var evidence = Assert.IsType<BrowserNavigationEvidence>(result.NavigationEvidence);
        Assert.True(evidence.Complete); Assert.Equal(initial, evidence.RequestedUrl); Assert.Equal(initial, evidence.FinalUrl);
        Assert.Equal(changeHistory ? initial + "?canonical=1" : initial, result.Url);
        Assert.Equal(changeHistory ? result.Url : null, evidence.DocumentUrl);
        var hop = Assert.Single(evidence.Hops);
        Assert.Equal(initial, hop.Url); Assert.Equal("GET", hop.Method); Assert.Equal(200, hop.StatusCode);
        Assert.Null(hop.Location); Assert.False(result.ObservationSnapshot!.CaptureTruncated);
        Assert.Equal(200, result.StatusCode); Assert.Equal("GET", result.Acquisition!.Navigation!.Method);
    }

    [Fact]
    public async Task NestedVariantLinksKeepTheirOuterOccurrenceAndPrimaryHeadingAssociation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body><main><div role='listitem' id='card'>" +
                "<ul><li><a href='/variant'>Variant</a></li></ul>" +
                "<h2 id='title'><a href='/primary'>Primary</a></h2></div>" +
                "<div role='listitem' id='second'><h2><a href='/primary'>Repeated occurrence</a></h2></div></main></body></html>", ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin, "domcontentloaded", 10000, null, "observation_complete", null, false, ct);
        var links = result.ObservationSnapshot!.Pages.SelectMany(page => page.Records).Where(record => record.Kind == "link").ToArray();
        Assert.Equal(new[] {"#card", "#card", "#second"}, links.Select(link => link.Occurrence!.Selector));
        Assert.Equal(new[] {0, 0, 1}, links.Select(link => link.Occurrence!.Index));
        Assert.Null(links[0].Heading); Assert.Equal("#title", links[1].Heading);
        Assert.NotEqual(links[0].Group, links[1].Group);
    }

    [Fact]
    public async Task DeniedChildFrameCannotContactItsDestinationOrInvalidateMainDocumentEvidence()
    {
        var ct = TestContext.Current.CancellationToken; var forbidden = 0;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            if (context.Request.Path == "/forbidden") { Interlocked.Increment(ref forbidden); return; }
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body><p>Observed main document</p><iframe src='/forbidden'></iframe></body></html>", ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
        {
            AllowedHosts = ["127.0.0.1"],
            AllowedNavigationUrlPatterns = [System.Text.RegularExpressions.Regex.Escape(site.Origin) + "/"]
        }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin, "load", 10000, null, "observation_complete", null, false, ct);
        Assert.True(result.Success); Assert.Equal(0, forbidden);
        Assert.True(result.NavigationEvidence!.Complete);
        Assert.Equal(site.Origin + "/", Assert.Single(result.NavigationEvidence.Hops).Url);
    }

    [Fact]
    public async Task RedirectedDocumentMayChangeItsHistoryAddressBeforeNavigationReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            if (context.Request.Path == "/start") { context.Response.Redirect("/final"); return; }
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body><script>history.replaceState(null, '', '/final?canonical=1')</script>Observed document</body></html>", ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin + "/start", "domcontentloaded", 10000, null, "observation_complete", null, false, ct);
        Assert.True(result.Success);
        var evidence = Assert.IsType<BrowserNavigationEvidence>(result.NavigationEvidence);
        Assert.Equal(site.Origin + "/final", evidence.FinalUrl);
        Assert.Equal(site.Origin + "/final?canonical=1", evidence.DocumentUrl);
        Assert.Equal(result.Url, evidence.DocumentUrl);
        Assert.Equal(new[] {302, 200}, evidence.Hops.Select(hop => hop.StatusCode));
    }

    [Fact]
    public async Task HistoryAddressChangesPreserveTheActualHttpResponseWithoutInventingARedirect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body>Observed document</body></html>", ct);
        }, ct);
        var changed = false;
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            ObservationCheckpoint = async (page, phase, token) =>
            {
                if (phase != "capture" || changed) return;
                changed = true;
                await page.EvaluateAsync("history.replaceState(null, '', '/?canonical=1')").WaitAsync(token);
            }
        };
        var initial = site.Origin + "/?original=1";
        var result = await host.GetContentAsync(initial, "domcontentloaded", 10000, null, "observation_complete", null, false, ct);
        Assert.True(result.Success);
        var evidence = Assert.IsType<BrowserNavigationEvidence>(result.NavigationEvidence);
        Assert.Equal(initial, evidence.RequestedUrl); Assert.Equal(initial, evidence.FinalUrl);
        Assert.Equal(site.Origin + "/?canonical=1", result.Url);
        Assert.Equal(result.Url, evidence.DocumentUrl);
        Assert.Equal(initial, Assert.Single(evidence.Hops).Url);
        Assert.Null(evidence.Hops[0].Location);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CurrentDocumentResponseSurvivesHistoryQueryEncodingAndObservedSameUrlReplacement(bool reload, bool redirect)
    {
        var ct = TestContext.Current.CancellationToken; var requests = 0;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            if (context.Request.Path == "/start") { context.Response.Redirect("/search?q=hello%20world"); return; }
            if (context.Request.Path != "/search") { context.Response.StatusCode = 204; return; }
            Interlocked.Increment(ref requests);
            Assert.Equal("hello world", context.Request.Query["q"].ToString());
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><head><title>Observed query</title></head><body><main><p>Current product</p></main></body></html>", ct);
        }, ct);
        var changed = false;
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            NavigationReadCheckpoint = async (page, token) =>
            {
                if (changed) return;
                changed = true;
                if (reload) await page.ReloadAsync(new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded }).WaitAsync(token);
                await page.EvaluateAsync("history.replaceState(null, '', '/search?' + new URLSearchParams(location.search))").WaitAsync(token);
            }
        };
        var initial = site.Origin + "/search?q=hello%20world";
        var requested = redirect ? site.Origin + "/start" : initial;
        var result = await host.GetContentAsync(requested, "domcontentloaded", 10000, null, "observation_complete", null, false, ct);
        Assert.True(result.Success); Assert.Equal(reload ? 2 : 1, requests);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("GET", result.Acquisition!.Navigation!.Method); Assert.Equal(200, result.Acquisition.Navigation.StatusCode);
        Assert.Equal(requested, result.NavigationEvidence!.RequestedUrl); Assert.Equal(initial, result.NavigationEvidence.FinalUrl);
        Assert.Equal(site.Origin + "/search?q=hello+world", result.NavigationEvidence.DocumentUrl);
        Assert.Equal(redirect ? new[] {302, 200} : new[] {200}, result.NavigationEvidence.Hops.Select(hop => hop.StatusCode));
        Assert.Equal(initial, result.NavigationEvidence.Hops[^1].Url);
        Assert.Null(result.NavigationEvidence.Hops[^1].Location);
        Assert.Equal(reload ? 1 : 0, result.NavigationEvidence.SupersededResponses?.Count ?? 0);
        if (reload) Assert.Equal(initial, Assert.Single(result.NavigationEvidence.SupersededResponses!).Url);
        Assert.False(result.ObservationSnapshot!.CaptureTruncated);
    }

    [Theory]
    [InlineData("other-get")]
    [InlineData("post")]
    public async Task ReplacementCannotBorrowAnEarlierGetForAnotherUrlOrPost(string replacement)
    {
        var ct = TestContext.Current.CancellationToken; var get = 0; var post = 0;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            if (context.Request.Method == "POST") Interlocked.Increment(ref post);
            else Interlocked.Increment(ref get);
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(context.Request.Method == "POST"
                ? "<html><body><p id='posted'>Observed POST</p></body></html>"
                : "<html><body><form id='submit' method='post' action='/search'></form><p>Observed GET</p></body></html>", ct);
        }, ct);
        var changed = false;
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance)
        {
            NavigationReadCheckpoint = async (page, token) =>
            {
                if (changed) return;
                changed = true;
                if (replacement == "other-get")
                    await page.GotoAsync(site.Origin + "/other", new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded }).WaitAsync(token);
                else
                {
                    await page.EvaluateAsync("document.getElementById('submit').submit()").WaitAsync(token);
                    await page.Locator("#posted").WaitForAsync().WaitAsync(token);
                }
            }
        };
        var error = await Assert.ThrowsAsync<BrowserNavigationException>(() => host.GetContentAsync(
            site.Origin + "/search", "domcontentloaded", 10000, null, "observation_complete", null, false, ct));
        Assert.Equal("NAVIGATION_EVIDENCE_INCOMPLETE", error.Code);
        Assert.Equal(replacement == "post" ? 1 : 2, get); Assert.Equal(replacement == "post" ? 1 : 0, post);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task NavigationPreservesCompressedResponseContent(string encoding)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/html";
            context.Response.Headers.ContentEncoding = encoding;
            await using Stream compressor = encoding == "gzip"
                ? new System.IO.Compression.GZipStream(context.Response.Body, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)
                : new System.IO.Compression.BrotliStream(context.Response.Body, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);
            await compressor.WriteAsync(Encoding.UTF8.GetBytes("<html><body>Observed compressed document</body></html>"), ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin, "domcontentloaded", 10000, null, "text", null, false, ct);
        Assert.Equal("Observed compressed document", result.Content);
    }

    [Fact]
    public async Task EscapedQueryRemainsEscapedAtTheInternalNavigationBoundary()
    {
        var ct = TestContext.Current.CancellationToken; var requests = 0;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            Interlocked.Increment(ref requests); Assert.Equal("hello world", context.Request.Query["q"].ToString());
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<html><body>Observed query</body></html>", ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
            { AllowedHosts = ["127.0.0.1"] }), NullLogger<PlaywrightBrowserHost>.Instance);
        var url = site.Origin + "/?q=hello%20world";
        var result = await host.GetContentAsync(url, "domcontentloaded", 10000, null, "observation_complete", null, false, ct);
        Assert.True(result.Success); Assert.Equal(1, requests);
        Assert.Equal(url, result.NavigationEvidence!.RequestedUrl);
        Assert.Equal(url, result.NavigationEvidence.FinalUrl);
    }

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
    public async Task AbsoluteRedirectCannotGainRouteAuthorityThroughCanonicalization()
    {
        var ct = TestContext.Current.CancellationToken; var followed = 0;
        await using var site = await LoopbackTestServer.StartAsync(context =>
        {
            if (context.Request.Path == "/start")
                context.Response.Headers.Location = "http://" + context.Request.Host + "/denied/../allowed";
            else Interlocked.Increment(ref followed);
            context.Response.StatusCode = context.Request.Path == "/start" ? 302 : 200;
            return Task.CompletedTask;
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
        {
            AllowedHosts = ["127.0.0.1"],
            AllowedNavigationUrlPatterns = [System.Text.RegularExpressions.Regex.Escape(site.Origin) + "/(?:start|allowed)"]
        }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await new BrowserTools(host, NullLogger<BrowserTools>.Instance).GetContentAsync(
            url: site.Origin + "/start", waitUntil: "domcontentloaded", timeoutMs: 10000,
            format: "observation_complete", cancellationToken: ct);
        Assert.False(result.Success); Assert.Equal("NAVIGATION_POLICY_DENIED", result.ErrorCode);
        Assert.Equal(0, followed);
        Assert.Equal(site.Origin + "/denied/../allowed", Assert.Single(result.NavigationEvidence!.Hops).Location);
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
