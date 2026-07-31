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
        Assert.Contains("href=\"/bear-text\">Bear + text demo</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BearTextDemo_RendersCombinedGeneratorControls()
    {
        var response = await _client.GetAsync("/bear-text", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<h1>Bear + text playground</h1>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"lockup-controls\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"bear-animation\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"text-animation\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"bear-size\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"text-size\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"gap\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"gradient-colors\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"margin-x\"", html, StringComparison.Ordinal);
        Assert.Contains("fetch(endpoint", html, StringComparison.Ordinal);
        Assert.Contains("/bear-text.svg?", html, StringComparison.Ordinal);
        Assert.Contains("download=\"gnougnou-bear-text.svg\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BearTextDemo_QueryInitializesBothComponentOptions()
    {
        var response = await _client.GetAsync(
            "/bear-text?text=Combined&gap=42&bearSize=300&textSize=110&role=Coder&fur=Blueberry&bearAnimation=Walk&textAnimation=Bounce&marginX=28&marginY=19",
            CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"text-value\" type=\"text\" maxlength=\"128\" autocomplete=\"off\" value=\"Combined\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"gap\" type=\"range\" min=\"0\" max=\"512\" step=\"1\" value=\"42\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Coder\" selected>Coder</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Blueberry\" selected>Blueberry</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Walk\" selected>Walk</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Bounce\" selected>Bounce</option>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"margin-x\" type=\"range\" min=\"0\" max=\"4096\" step=\"1\" value=\"28\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BearTextDemo_QueryText_IsHtmlEncoded()
    {
        var response = await _client.GetAsync(
            "/bear-text?text=%22%3E%3Cscript%3Ealert(1)%3C%2Fscript%3E",
            CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("value=\"\"><script>alert(1)</script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("value=\"&quot;&gt;&lt;script&gt;alert(1)&lt;/script&gt;\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BearTextSvg_CustomStaticOptions_ReturnComposedSvg()
    {
        var response = await _client.GetAsync(
            "/bear-text.svg?text=Hello%20Go&gap=40&seed=42&bearSize=300&textSize=120&role=Coder&emotion=Proud&accessory=Laptop&state=Success&theme=Transparent&fur=Blueberry&eyes=Starry&nose=Heart&beardStyle=Cloud&beard=true&headphones=false&bowTie=true&marginX=30&marginY=20&color=%237C3AED&color=%2306B6D4&stars=3&starScale=1.2&idPrefix=combined",
            CancellationToken.None);
        var svg = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var document = XDocument.Parse(svg);
        var root = document.Root!;
        var bearPart = document.Descendants().Single(element => element.Attribute("data-part")?.Value == "bear");
        var textPart = document.Descendants().Single(element => element.Attribute("data-part")?.Value == "text");
        var bearSvg = bearPart.Elements().Single(element => element.Name.LocalName == "svg");
        var textSvg = textPart.Elements().Single(element => element.Name.LocalName == "svg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("combined-root", root.Attribute("id")?.Value);
        Assert.Equal("false", root.Attribute("data-animated")?.Value);
        Assert.Equal("300", bearSvg.Attribute("width")?.Value);
        Assert.Equal("120", textSvg.Descendants().First(element => element.Attribute("data-letter-index") is not null)
            .Attribute("font-size")?.Value);
        Assert.Equal(
            int.Parse(bearSvg.Attribute("width")!.Value) + 40 + int.Parse(textSvg.Attribute("width")!.Value),
            int.Parse(root.Attribute("width")!.Value));
        Assert.Contains("id=\"combined-bear-fur\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"combined-text-gradient\"", svg, StringComparison.Ordinal);
        Assert.Contains("#84AEE8", svg, StringComparison.Ordinal);
        Assert.Contains("stop-color=\"#7C3AED\"", svg, StringComparison.Ordinal);
        Assert.Equal(3, document.Descendants().Count(element => element.Attribute("data-part")?.Value == "star"));
    }

    [Fact]
    public async Task BearTextSvg_MixedAnimations_ReturnIndependentPresets()
    {
        var response = await _client.GetAsync(
            "/bear-text.svg?text=Animated&theme=Transparent&bearAnimation=Idle&textAnimation=Bounce",
            CancellationToken.None);
        var svg = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var root = XDocument.Parse(svg).Root!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("true", root.Attribute("data-animated")?.Value);
        Assert.Equal("idle", root.Attribute("data-bear-animation")?.Value);
        Assert.Equal("bounce", root.Attribute("data-text-animation")?.Value);
        Assert.Contains("data-animation-rig=\"true\"", svg, StringComparison.Ordinal);
        Assert.Contains("class=\"gnougnou-text-bounce\"", svg, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", svg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/bear-text.svg?gap=-1")]
    [InlineData("/bear-text.svg?bearSize=63")]
    [InlineData("/bear-text.svg?text=")]
    [InlineData("/bear-text.svg?role=Unknown")]
    [InlineData("/bear-text.svg?headphones=yes")]
    [InlineData("/bear-text.svg?textAnimation=Spin")]
    public async Task BearTextSvg_InvalidOptions_ReturnBadRequest(string path)
    {
        var response = await _client.GetAsync(path, CancellationToken.None);
        var message = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(message));
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
        Assert.Contains("id=\"margin-x\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"margin-y\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"animation\"", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Wave\">Wave</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Bounce\">Bounce</option>", html, StringComparison.Ordinal);
        Assert.Contains("id=\"preview-background\"", html, StringComparison.Ordinal);
        Assert.Contains("fetch(endpoint", html, StringComparison.Ordinal);
        Assert.Contains("/text.svg?", html, StringComparison.Ordinal);
        Assert.Contains("download=\"gnougnou-text.svg\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextDemo_QueryInitializesShareableConfiguration()
    {
        var response = await _client.GetAsync(
            "/text?text=Demo&size=200&marginX=26&marginY=34&color=%234F46E5&color=%232DD4BF&stars=3&starScale=1.5&animation=Wave",
            CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("value=\"Demo\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"text-size\" name=\"size\" type=\"range\" min=\"16\" max=\"1024\" step=\"1\" value=\"200\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"margin-x\" type=\"range\" min=\"0\" max=\"4096\" step=\"1\" value=\"26\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"margin-y\" type=\"range\" min=\"0\" max=\"4096\" step=\"1\" value=\"34\"", html, StringComparison.Ordinal);
        Assert.Equal(2, Count(html, "aria-label=\"Gradient color\" value="));
        Assert.Contains("<option value=\"Wave\" selected>Wave</option>", html, StringComparison.Ordinal);
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
            "/text.svg?text=Hello%20Go&size=144&marginX=30&marginY=40&color=%234F46E5&color=%230EA5E9&color=%232DD4BF&stars=3&starColor=%23F59E0B&starScale=1.5&animation=Idle&idPrefix=demo",
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
        Assert.Equal("30", document.Descendants().First(element => element.Attribute("data-letter-index") is not null)
            .Attribute("x")?.Value);
        Assert.Equal(["#4F46E5", "#0EA5E9", "#2DD4BF"], stops.Select(stop => stop.Attribute("stop-color")?.Value));
        Assert.Equal(3, document.Descendants().Count(element => element.Attribute("data-part")?.Value == "star"));
        Assert.Equal("#F59E0B", document.Descendants().Single(element => element.Attribute("data-part")?.Value == "stars")
            .Attribute("fill")?.Value);
        Assert.Equal("idle", document.Descendants().Single(element => element.Attribute("class")?.Value == "gnougnou-text")
            .Attribute("data-animation")?.Value);
        Assert.Contains("@keyframes demo-wave", svg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Wave", "wave", "gnougnou-text-wave-flow")]
    [InlineData("Bounce", "bounce", "gnougnou-text-bounce")]
    public async Task TextSvg_NewAnimations_ReturnSelectedPreset(
        string animation,
        string token,
        string className)
    {
        var response = await _client.GetAsync($"/text.svg?text=Play&animation={animation}", CancellationToken.None);
        var svg = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"data-animation=\"{token}\"", svg, StringComparison.Ordinal);
        Assert.Contains($"class=\"{className}\"", svg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/text.svg?size=large")]
    [InlineData("/text.svg?animation=Dance")]
    [InlineData("/text.svg?color=red&color=blue")]
    [InlineData("/text.svg?stars=9")]
    [InlineData("/text.svg?text=")]
    [InlineData("/text.svg?marginX=-1")]
    [InlineData("/text.svg?marginY=wide")]
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
