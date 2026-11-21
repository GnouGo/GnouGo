using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Browser.Mcp.Tests;

public sealed class BrowserAncestorAttributeTests
{
    [Theory]
    [InlineData("[\"data-code\",\"data-code\",\"data-code\",\"data-code\",\"data-code\",\"data-code\",\"data-code\",\"data-code\",\"data-code\"]")]
    [InlineData("[\"invalid name\"]")]
    [InlineData("[\"\"]")]
    public void InvalidAttributeConfigurationFailsBeforeAnyBrowserStarts(string names)
    {
        var settings = JsonSerializer.Deserialize<BrowserServerSettings>("{\"ObservationAncestorAttributes\":" + names + "}")!;
        Assert.Throws<ArgumentException>(() => new PlaywrightBrowserHost(Options.Create(settings), NullLogger<PlaywrightBrowserHost>.Instance));
    }

    [Fact]
    public async Task OverlongObservedAttributeIsExplicitlyIncomplete()
    {
        var ct = TestContext.Current.CancellationToken;
        var html = "<body><main data-code='" + new string('x', 1025) + "'><a href='/item'>Observed</a></main></body>";
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(html), ct);
        }, ct);
        var settings = new BrowserServerSettings { AllowedHosts = ["127.0.0.1"], ObservationAncestorAttributes = ["data-code"] };
        await using var host = new PlaywrightBrowserHost(Options.Create(settings), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin, "domcontentloaded", null, "main", "observation", null, false, ct);
        Assert.True(result.Observation!.CaptureTruncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedLinkContextPreservesNearestEmptyMissingAndRepeatedContainerValues(bool requested)
    {
        var ct = TestContext.Current.CancellationToken;
        const string html = "<body data-code='outside'><main><div data-code=''><a href='/brand'>Brand navigation</a></div>" +
            "<ol><li><div data-code='item-a' data-private='SECRET'><a href='/first'>Image</a><a href='/first'>Title</a></div></li>" +
            "<li><div data-code='item-a'><a href='/second'>Repeated identifier</a></div></li></ol><a href='/missing'>Unannotated</a></main></body>";
        await using var site = await LoopbackTestServer.StartAsync(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(html), ct);
        }, ct);
        var settings = JsonSerializer.Deserialize<BrowserServerSettings>(requested
            ? "{\"ObservationAncestorAttributes\":[\"data-code\"]}" : "{}")!;
        settings.AllowedHosts = ["127.0.0.1"];
        await using var host = new PlaywrightBrowserHost(Options.Create(settings), NullLogger<PlaywrightBrowserHost>.Instance);
        var result = await host.GetContentAsync(site.Origin, "domcontentloaded", null, "main", "observation_complete", null, false, ct);
        Assert.False(result.ObservationSnapshot!.CaptureTruncated);
        var records = result.ObservationSnapshot.Pages.SelectMany(p => p.Records).Where(r => r.Kind == "link").ToArray();
        var wire = records.Select(r => JsonSerializer.SerializeToNode(r, BrowserMcpJsonContext.Default.BrowserObservationRecord)!).ToArray();
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result, BrowserMcpJsonContext.Default.BrowserContentResult), StringComparison.Ordinal);
        if (!requested) { Assert.All(wire, r => Assert.Null(r["ancestorAttributes"])); return; }
        Assert.NotNull(wire[0]["ancestorAttributes"]);
        Assert.Equal("", wire[0]["ancestorAttributes"]!["data-code"]!["value"]!.ToString());
        Assert.Equal("item-a", wire[1]["ancestorAttributes"]!["data-code"]!["value"]!.ToString());
        Assert.Equal(wire[1]["ancestorAttributes"]!["data-code"]!["index"]!.ToString(), wire[2]["ancestorAttributes"]!["data-code"]!["index"]!.ToString());
        Assert.NotEqual(wire[1]["ancestorAttributes"]!["data-code"]!["index"]!.ToString(), wire[3]["ancestorAttributes"]!["data-code"]!["index"]!.ToString());
        Assert.True(wire[4]["ancestorAttributes"]!.AsObject().ContainsKey("data-code"));
        Assert.Null(wire[4]["ancestorAttributes"]!["data-code"]); // Does not escape the selected root to borrow body metadata.
    }
}
