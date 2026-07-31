using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GnOuGo.Assets.Bears.Server.Tests;

public sealed class BearsGalleryServerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public BearsGalleryServerTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Home_RendersSeparateStaticAndAnimatedCollections()
    {
        var response = await _client.GetAsync("/?seed=42", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<h2 id=\"static-title\">Static</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"animated-title\">Animated</h2>", html, StringComparison.Ordinal);
        Assert.Equal(10, Count(html, "data-gallery-kind=\"static\""));
        Assert.Equal(12, Count(html, "data-gallery-kind=\"animated\""));
        Assert.Contains("data-animation=\"thinking\"", html, StringComparison.Ordinal);
        Assert.Contains("<h3>AI Thinking</h3>", html, StringComparison.Ordinal);
        Assert.Contains("AI thinking included", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("data-animation=\"thinking\"", StringComparison.Ordinal)
            < html.IndexOf("data-animation=\"idle\"", StringComparison.Ordinal));
        Assert.Contains("data-nose-style=\"heart\"", html, StringComparison.Ordinal);
        Assert.Contains("data-beard-style=\"long-point\"", html, StringComparison.Ordinal);
        Assert.Contains("data-beard-style=\"cloud\"", html, StringComparison.Ordinal);
        Assert.Contains("data-beard-style=\"square\"", html, StringComparison.Ordinal);
        Assert.Contains("data-beard-style=\"split\"", html, StringComparison.Ordinal);
        Assert.Contains("data-eye-style=\"starry\"", html, StringComparison.Ordinal);
        Assert.Contains("@keyframes gnougo-", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BearSvg_ThinkingAnimationExposesAiConcentrationEffects()
    {
        var response = await _client.GetAsync(
            "/bear.svg?seed=42&appearance=bright-smile&animation=Thinking",
            CancellationToken.None);
        var svg = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var document = XDocument.Parse(svg);
        var rig = document.Descendants().Single(element =>
            element.Attribute("data-animation-rig")?.Value == "true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("thinking", rig.Attribute("data-animation")?.Value);
        Assert.NotNull(rig.Descendants().Single(element =>
            element.Attribute("data-part")?.Value == "thinking-flush"));
        Assert.NotNull(rig.Descendants().Single(element =>
            element.Attribute("data-part")?.Value == "thinking-sweat"));
        Assert.NotNull(rig.Descendants().Single(element =>
            element.Attribute("data-part")?.Value == "thinking-arm-rub"));
        Assert.NotNull(rig.Descendants().Single(element =>
            element.Attribute("data-part")?.Value == "thinking-hand-rub"));
        Assert.Contains("@keyframes gnougo-think-eyes", svg, StringComparison.Ordinal);
        Assert.Contains("@keyframes gnougo-think-sweat", svg, StringComparison.Ordinal);
        Assert.Contains("@keyframes gnougo-think-arm-rub", svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BearSvg_AppearanceVariantIsPreservedInAnimatedRig()
    {
        var response = await _client.GetAsync(
            "/bear.svg?seed=42&appearance=split-beard&animation=Idle",
            CancellationToken.None);
        var svg = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var document = XDocument.Parse(svg);
        var rig = document.Descendants().Single(element =>
            element.Attribute("data-animation-rig")?.Value == "true");
        var beard = rig.Descendants().Single(element =>
            element.Attribute("data-part")?.Value == "beard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("idle", rig.Attribute("data-animation")?.Value);
        Assert.Equal("wink", rig.Attribute("data-eye-style")?.Value);
        Assert.Equal("heart", rig.Attribute("data-nose-style")?.Value);
        Assert.Equal("split", beard.Attribute("data-beard-style")?.Value);
    }

    [Fact]
    public async Task BearSvg_StaticAppearanceDoesNotEmbedMotionPreset()
    {
        var response = await _client.GetAsync(
            "/bear.svg?seed=42&appearance=starry-pride&animation=None",
            CancellationToken.None);
        var svg = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var document = XDocument.Parse(svg);
        var nose = document.Descendants().Single(element =>
            element.Attribute("data-part")?.Value == "nose");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("heart", nose.Attribute("data-nose-style")?.Value);
        Assert.DoesNotContain("data-animation-rig", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("@keyframes gnougo-", svg, StringComparison.Ordinal);
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }
}
