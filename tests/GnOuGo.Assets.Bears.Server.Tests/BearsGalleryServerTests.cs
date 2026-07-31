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
        Assert.Contains("href=\"/text\">Text SVG demo</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDemo_RendersInteractiveLiveGeneratorControls()
    {
        var response = await _client.GetAsync("/text", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<h1>Text SVG playground</h1>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"text-controls\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"gradient-colors\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"star-count\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"animation\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"preview-background\"", html, StringComparison.Ordinal);
        Assert.Contains("fetch(endpoint", html, StringComparison.Ordinal);
        Assert.Contains("/text.svg?", html, StringComparison.Ordinal);
        Assert.Contains("download=\"gnougnou-text.svg\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDemo_QueryInitializesShareableConfiguration()
    {
        var response = await _client.GetAsync(
            "/text?text=Demo&size=200&color=%234F46E5&color=%232DD4BF&stars=3&starScale=1.5&animation=Idle",
            CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("value=\"Demo\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"text-size\" name=\"size\" type=\"range\" min=\"16\" max=\"1024\" step=\"1\" value=\"200\"", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, "aria-label=\"Gradient color\" value="));
        Assert.Contains("<option value=\"Idle\" selected>Idle</option>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDemo_QueryText_IsHtmlEncoded()
    {
        var response = await _client.GetAsync(
            "/text?text=%22%3E%3Cscript%3Ealert(1)%3C%2Fscript%3E",
            CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("value=\"\"><script>alert(1)</script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("value=\"&quot;&gt;&lt;script&gt;alert(1)&lt;/script&gt;\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextSvg_CustomOptions_ReturnConfiguredStandaloneSvg()
    {
        var response = await _client.GetAsync(
            "/text.svg?text=Hello%20Go&size=144&color=%234F46E5&color=%230EA5E9&color=%232DD4BF&stars=3&starColor=%23F59E0B&starScale=1.5&animation=Idle&idPrefix=demo",
            CancellationToken.None);
        var svg = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var document = XDocument.Parse(svg);
        var root = document.Root!;
        var stops = document.Descendants().Where(element => element.Name.LocalName == "stop").ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("demo-root", root.Attribute("id")?.Value);
        Assert.True(int.Parse(root.Attribute("width")!.Value) > int.Parse(root.Attribute("height")!.Value));
        Assert.Equal(["#4F46E5", "#0EA5E9", "#2DD4BF"], stops.Select(stop => stop.Attribute("stop-color")?.Value));
        Assert.Equal(3, document.Descendants().Count(element => element.Attribute("data-part")?.Value == "star"));
        Assert.Equal("#F59E0B", document.Descendants().Single(element => element.Attribute("data-part")?.Value == "stars")
            .Attribute("fill")?.Value);
        Assert.Equal("idle", document.Descendants().Single(element => element.Attribute("class")?.Value == "gnougnou-text")
            .Attribute("data-animation")?.Value);
        Assert.Contains("@keyframes demo-wave", svg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/text.svg?size=large")]
    [InlineData("/text.svg?animation=Dance")]
    [InlineData("/text.svg?color=red&color=blue")]
    [InlineData("/text.svg?stars=9")]
    [InlineData("/text.svg?text=")]
    public async Task TextSvg_InvalidOptions_ReturnBadRequest(string path)
    {
        var response = await _client.GetAsync(path, CancellationToken.None);
        var message = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(message));
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
