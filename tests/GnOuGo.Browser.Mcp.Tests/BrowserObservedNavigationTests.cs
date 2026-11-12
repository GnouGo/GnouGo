using System.Text.RegularExpressions;
using GnOuGo.Browser.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserObservedNavigationTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("entry")]
    [InlineData("pattern")]
    public void ConfigurationRequiresBothValidTrustedPatternLists(string missing)
    {
        var settings = new BrowserServerSettings
        {
            ObservedNavigationSourceUrlPatterns = missing == "source" ? [] : ["https://example.test/source"],
            ObservedNavigationEntryUrlPatterns = missing == "entry" ? [] : [missing == "pattern" ? "[" : "https://example.test/entry"]
        };
        Assert.Throws<ArgumentException>(() => Host(settings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteOwnedSourceAuthorizesExactRepeatedContentReadsWithoutChangingBaseRules(bool separateHost)
    {
        await using var site = await Site.Start();
        site.SeparateHost = separateHost;
        var settings = site.Settings(); var original = settings.AllowedNavigationUrlPatterns.ToArray();
        await using var host = Host(settings);
        var source = await Read(host, site.Origin + "/search?q=1");
        var records = source.ObservationSnapshot!.Pages.SelectMany(p => p.Records).ToArray();
        Assert.Equal(2, records.Count(r => r.Href == site.Entry));
        var first = await Read(host, site.Entry);
        var second = await Read(host, site.Entry);
        Assert.Equal(site.Entry, first.NavigationEvidence!.RequestedUrl);
        Assert.Equal(new[] { 302, 200 }, first.NavigationEvidence.Hops.Select(h => h.StatusCode));
        Assert.NotEqual(first.ObservationSnapshot!.Id, second.ObservationSnapshot!.Id);
        Assert.Equal(2, site.EntryHits);
        Assert.Equal(original, settings.AllowedNavigationUrlPatterns);
        Assert.Equal(new[] { "127.0.0.1" }, settings.AllowedHosts);
        Assert.Throws<InvalidOperationException>(() => BrowserNavigationPolicy.ValidateNavigationTarget(site.Entry, settings));
        await using var other = Host(settings);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Read(other, site.Entry));
        Assert.Equal(2, site.EntryHits);
    }

    [Theory]
    [InlineData("unobserved")]
    [InlineData("query")]
    [InlineData("fragment")]
    [InlineData("path")]
    [InlineData("close")]
    [InlineData("new_source")]
    [InlineData("failed_source")]
    [InlineData("selector")]
    [InlineData("text")]
    [InlineData("disabled")]
    [InlineData("redirect")]
    [InlineData("interaction")]
    public async Task MissingExpiredOrNonReadAuthorityNeverContactsTheEntry(string change)
    {
        await using var site = await Site.Start();
        var settings = site.Settings();
        if (change == "disabled")
        { settings.ObservedNavigationSourceUrlPatterns = []; settings.ObservedNavigationEntryUrlPatterns = []; }
        await using var host = Host(settings);
        await Read(host, site.Origin + "/search?q=1", change == "text" ? "text" : "observation_complete", change == "selector" ? "main" : null);
        var target = site.Entry;
        switch (change)
        {
            case "unobserved": target = site.Origin + "/ad/unseen?opaque=%2F"; break;
            case "query": target += "&extra=1"; break;
            case "fragment": target += "#reviews"; break;
            case "path": target = target.Replace("/ad/", "/other/../ad/", StringComparison.Ordinal); break;
            case "close": await host.CloseAsync(TestContext.Current.CancellationToken); break;
            case "new_source": await Read(host, site.Origin + "/search?q=2"); break;
            case "failed_source": site.FailSource = true; _ = await Record.ExceptionAsync(() => Read(host, site.Origin + "/search?q=2")); break;
            case "redirect": site.UnobservedRedirect = true; break;
        }
        if (change == "interaction")
            await Assert.ThrowsAnyAsync<Exception>(() => host.ClickAsync("#entry", "load", 5000, TestContext.Current.CancellationToken));
        else await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Read(host, target));
        Assert.Equal(change == "redirect" ? 1 : 0, site.EntryHits);
        Assert.Equal(0, site.UnobservedHits);
        // A failed evidence read clears the old grant, including after a denied URL.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Read(host, site.Entry));
        Assert.Equal(change == "redirect" ? 1 : 0, site.EntryHits);
    }

    [Fact]
    public async Task NewSearchDuringPublicationCannotRestoreTheOldAuthority()
    {
        await using var site = await Site.Start(); var injected = false;
        var diagnostics = new ScopeLogger();
        await using var host = new PlaywrightBrowserHost(Options.Create(site.Settings()), diagnostics)
        {
            ObservationCheckpoint = async (page, phase, token) =>
            {
                if (phase != "publish" || injected) return;
                injected = true;
                await page.GotoAsync(site.Origin + "/search?q=2").WaitAsync(token);
            }
        };
        var moved = await Read(host, site.Origin + "/search?q=1");
        Assert.Equal(site.Origin + "/search?q=2", moved.Url);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Read(host, site.Entry));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Read(host, site.Origin + "/ad/two?opaque=%2F"));
        Assert.Equal(0, site.EntryHits);
        Assert.Contains(diagnostics.Messages, message => message.Contains("source_changed_during_acquisition", StringComparison.Ordinal));
        Assert.Contains(diagnostics.Messages, message => message.Contains("unowned_source_request", StringComparison.Ordinal));
        await Read(host, site.Origin + "/search?q=2");
        Assert.True((await Read(host, site.Origin + "/ad/two?opaque=%2F")).Success);
        Assert.Contains(diagnostics.Messages, message => message.Contains("complete_owned_source", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics.Messages, message => message.Contains(site.Origin, StringComparison.Ordinal) || message.Contains("opaque=", StringComparison.Ordinal));
    }

    private static PlaywrightBrowserHost Host(BrowserServerSettings settings)
        => new(Options.Create(settings), NullLogger<PlaywrightBrowserHost>.Instance);
    private static Task<BrowserContentResult> Read(PlaywrightBrowserHost host, string url, string format = "observation_complete", string? selector = null)
        => host.GetContentAsync(url, "load", 30000, selector, format, 8000, false, TestContext.Current.CancellationToken);

    private sealed class ScopeLogger : ILogger<PlaywrightBrowserHost>
    {
        internal System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (text.StartsWith("Observed navigation scope ", StringComparison.Ordinal)) Messages.Add(text);
        }
    }

    private sealed class Site : IAsyncDisposable
    {
        private LoopbackTestServer _server = null!;
        internal string Origin => _server.Origin;
        private string EntryOrigin => SeparateHost ? Origin.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) : Origin;
        internal string Entry => EntryOrigin + "/ad/one?opaque=%2F";
        internal int EntryHits, UnobservedHits;
        internal bool FailSource, UnobservedRedirect, SeparateHost;
        internal static async Task<Site> Start()
        {
            var site = new Site();
            site._server = await LoopbackTestServer.StartAsync(async context =>
            {
                var path = context.Request.Path.Value!;
                if (path == "/ad/one")
                {
                    Interlocked.Increment(ref site.EntryHits);
                    context.Response.Redirect(site.UnobservedRedirect ? site.EntryOrigin + "/ad/unseen?opaque=%2F" : site.Origin + "/product/one"); return;
                }
                if (path == "/ad/unseen") Interlocked.Increment(ref site.UnobservedHits);
                if (path == "/search")
                {
                    if (site.FailSource) context.Response.StatusCode = 503;
                    var href = context.Request.Query["q"] == "1" ? site.Entry : site.Origin + "/ad/two?opaque=%2F";
                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync("<html><body><main><h1>Source</h1><a id='entry' href='" + href +
                        "'>One</a><a href='" + href + "'>Repeated</a></main></body></html>"); return;
                }
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync("<html><body><main><h1>Destination</h1><p>Observed content</p></main></body></html>");
            }, TestContext.Current.CancellationToken);
            return site;
        }
        internal BrowserServerSettings Settings() => new()
        {
            AllowedHosts = ["127.0.0.1"],
            AllowedNavigationUrlPatterns = [Regex.Escape(Origin) + @"/(?:search\?q=[12]|product/[^?#]+)"],
            ObservedNavigationSourceUrlPatterns = [Regex.Escape(Origin) + @"/search\?q=[12]"],
            ObservedNavigationEntryUrlPatterns = [Regex.Escape(EntryOrigin) + @"/ad/[a-z]+\?opaque=[^#]+"],
            MaxObservationRecords = 100, MaxObservationPages = 100
        };
        public ValueTask DisposeAsync() => _server.DisposeAsync();
    }
}
