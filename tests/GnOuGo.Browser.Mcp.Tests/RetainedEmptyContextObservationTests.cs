using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class RetainedEmptyContextObservationTests
{
    [Fact]
    public async Task EmptyWrapperRetainsAccessibleLabelsWithoutBorrowingChildIdentityOrDroppingLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        const string wrapper = "https://sponsored-ads.amazon.fr/clk/?fixture=collection%2FKEEP&token=A%2fb";
        const string product = "https://sponsored-ads.amazon.fr/clk/?fixture=product%2FKEEP&token=C%2fd";
        // Structural reduction of retained empty wrapper contexts 0/23 and the
        // related-brand context 86. These opaque fixture URLs are synthetic;
        // no captured advertising URL is rewritten or contacted.
        var html = $$"""
            <html><body><main>
              <div id="collection" data-asin="">
                <a id="collection-link" aria-label="Sponsored collection label" href="{{wrapper}}"><img alt="Brand mark"></a>
                <div role="list"><li><div data-asin="B01FYE0FZ6">
                  <a id="product-image" aria-label="Observed product label" href="{{product}}"><img alt="Product"></a>
                  <a id="product-title" href="{{product}}">Observed product label</a>
                </div></li></div>
              </div>
              <div id="related" data-asin="">
                <span id="related-heading" role="heading" aria-label="Related brands"></span>
                <a id="unknown-image" href="https://sponsored-ads.amazon.fr/clk/?fixture=unknown"><img alt="Unclassified"></a>
                <a id="unknown-title" href="https://sponsored-ads.amazon.fr/clk/?fixture=unknown-title">Brand campaign</a>
              </div>
              <div hidden><a aria-label="HIDDEN LABEL" href="https://example.invalid/hidden">Hidden</a></div>
            </main></body></html>
            """;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html, ct);
        }, ct);
        await using var host = new PlaywrightBrowserHost(Options.Create(new BrowserServerSettings
        {
            AllowedHosts = ["127.0.0.1"], ObservationAncestorAttributes = ["data-asin"]
        }), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin, "domcontentloaded", null, "main", "observation_complete", null, false, ct);
        Assert.True(result.Success); Assert.False(result.ObservationSnapshot!.CaptureTruncated);
        var records = result.ObservationSnapshot.Pages.SelectMany(p => p.Records).ToArray();
        var links = records.Where(r => r.Kind == "link").ToArray();
        Assert.Equal(["#collection-link", "#product-image", "#product-title", "#unknown-image", "#unknown-title"], links.Select(r => r.Selector));
        Assert.Equal(wrapper, links[0].Href); Assert.Equal(product, links[1].Href); Assert.Equal(product, links[2].Href);
        Assert.Equal("Sponsored collection label", links[0].Text);
        Assert.Equal("Observed product label", links[1].Text);
        Assert.Equal("", links[0].AncestorAttributes!["data-asin"]!.Value);
        Assert.Equal("B01FYE0FZ6", links[1].AncestorAttributes!["data-asin"]!.Value);
        Assert.Equal("", links[3].AncestorAttributes!["data-asin"]!.Value);
        Assert.Null(links[0].Occurrence); Assert.NotNull(links[1].Occurrence); Assert.Null(links[3].Occurrence);
        Assert.Contains(records, r => r.Selector == "#related-heading" && r.Kind == "heading" && r.Text == "Related brands");
        Assert.DoesNotContain(records, r => r.Text.Contains("HIDDEN LABEL", StringComparison.Ordinal));
        await host.CloseAsync(ct);
    }
}
