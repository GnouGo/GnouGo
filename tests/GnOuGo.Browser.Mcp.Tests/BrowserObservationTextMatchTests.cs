using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserObservationTextMatchTests
{
    [Fact]
    public async Task CompleteCaptureReturnsOnlyMatchingTextWithOriginalPositions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("""
                <main><h1 id="subject">Observed title</h1><p id="other">Unrelated original</p>
                <div id="details"><p>Exact detail</p><a href="/more">Other page</a></div></main>
                """), ct);
        }, ct);
        var settings = new BrowserServerSettings { AllowedHosts = ["127.0.0.1"], ObservationTextSelectorPrefixes = ["#subject", "#details"] };
        await using var host = new PlaywrightBrowserHost(Options.Create(settings), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin, "domcontentloaded", null, "main", "observation_complete", null, false, ct, maxRecords: 1);
        var snapshot = Assert.IsType<BrowserObservationSnapshot>(result.ObservationSnapshot);
        var view = Assert.IsType<BrowserObservationTextMatches>(snapshot.TextMatches);
        Assert.False(view.Truncated);
        Assert.Equal(["Observed title", "Exact detail"], view.Records.Select(row => row.Text));
        Assert.All(view.Records, row =>
        {
            var original = snapshot.Pages[row.Page].Records[row.Offset];
            Assert.Equal((original.Kind, original.Selector, original.Text, original.Reference), (row.Kind, row.Selector, row.Text, row.Reference));
        });
        Assert.Contains(snapshot.Pages.SelectMany(page => page.Records), record => record.Text == "Unrelated original");
    }

    [Theory]
    [InlineData(65, 1, 64)]
    [InlineData(1, 17000, 0)]
    [InlineData(2, 12, 2)]
    public void ViewReportsAnyOmittedMatchAndPreservesRepeatedRecords(int count, int length, int retained)
    {
        var records = Enumerable.Range(0, count).Select(index => new BrowserObservationRecord("text", "p", "#field", "", new string('x', length), null, null)
            { Reference = "record-" + index }).ToArray();
        var original = new BrowserObservation("snapshot", records, null, false);
        var view = BrowserObservationTextMatches.Create([original], ["#field"]);
        Assert.Equal(retained < count, view.Truncated);
        Assert.Equal(records.Take(retained).Select(record => record.Reference), view.Records.Select(row => row.Reference));
        Assert.Equal(count, original.Records.Count);
    }
}
