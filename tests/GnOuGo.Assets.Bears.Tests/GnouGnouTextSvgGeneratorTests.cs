using System.Xml.Linq;
using GnOuGo.Assets.Bears;
using Xunit;

namespace GnOuGo.Assets.Bears.Tests;

public sealed class GnouGnouTextSvgGeneratorTests
{
    [Fact]
    public void Generate_TextAndSize_ProducesValidAutomaticallySizedSvg()
    {
        var svg = GnouGnouTextSvgGenerator.Generate("GnouGo", 128);
        var document = XDocument.Parse(svg);
        var root = document.Root!;

        Assert.True(int.Parse(root.Attribute("width")!.Value) > int.Parse(root.Attribute("height")!.Value));
        Assert.Equal("0 0 " + root.Attribute("width")!.Value + " " + root.Attribute("height")!.Value,
            root.Attribute("viewBox")?.Value);
        Assert.Equal(6, document.Descendants().Count(element => element.Attribute("data-letter-index") is not null));
        Assert.Equal("none", document.Descendants().Single(element => element.Attribute("class")?.Value == "gnougnou-text")
            .Attribute("data-animation")?.Value);
        Assert.Equal(2, document.Descendants().Count(element => element.Attribute("data-part")?.Value == "star"));
    }

    [Fact]
    public void Generate_SameOptions_ProducesSameSvg()
    {
        var options = new GnouGnouTextOptions
        {
            Text = "Deterministic",
            Size = 96,
            Animation = GnouGnouTextAnimation.Idle,
            StarCount = 3
        };

        Assert.Equal(
            GnouGnouTextSvgGenerator.Generate(options),
            GnouGnouTextSvgGenerator.Generate(options));
    }

    [Fact]
    public void Generate_TextAndSizeChangeCalculatedCanvas()
    {
        var shortDocument = XDocument.Parse(GnouGnouTextSvgGenerator.Generate("Go", 64));
        var longDocument = XDocument.Parse(GnouGnouTextSvgGenerator.Generate("GnOuGo Assets", 64));
        var largeDocument = XDocument.Parse(GnouGnouTextSvgGenerator.Generate("Go", 160));

        Assert.True(Width(longDocument) > Width(shortDocument));
        Assert.True(Width(largeDocument) > Width(shortDocument));
        Assert.True(Height(largeDocument) > Height(shortDocument));
    }

    [Fact]
    public void Generate_CustomGradient_UsesEvenlyDistributedStops()
    {
        var svg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "Colors",
            GradientColors = ["#4F46E5", "#0EA5E9", "#2DD4BF"]
        });
        var document = XDocument.Parse(svg);
        var stops = document.Descendants().Where(element => element.Name.LocalName == "stop").ToArray();

        Assert.Equal(3, stops.Length);
        Assert.Equal(["0%", "50%", "100%"], stops.Select(element => element.Attribute("offset")?.Value));
        Assert.Equal(
            ["#4F46E5", "#0EA5E9", "#2DD4BF"],
            stops.Select(element => element.Attribute("stop-color")?.Value));
    }

    [Fact]
    public void Generate_Stars_AreConfigurableAndCanBeDisabled()
    {
        var customSvg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "Stars",
            StarCount = 4,
            StarColor = "#F59E0B",
            StarScale = 1.4d
        });
        var customDocument = XDocument.Parse(customSvg);
        var noStarsSvg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "No stars",
            StarCount = 0
        });

        Assert.Equal(4, customDocument.Descendants().Count(element => element.Attribute("data-part")?.Value == "star"));
        Assert.Equal("#F59E0B", customDocument.Descendants().Single(element => element.Attribute("data-part")?.Value == "stars")
            .Attribute("fill")?.Value);
        Assert.DoesNotContain("data-part=\"stars\"", noStarsSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_IdleAnimation_AddsIndependentMotionAndLeftToRightWave()
    {
        var svg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "Wave",
            Animation = GnouGnouTextAnimation.Idle
        });

        _ = XDocument.Parse(svg);
        Assert.Contains("data-animation=\"idle\"", svg, StringComparison.Ordinal);
        Assert.Equal(4, Count(svg, "class=\"gnougnou-text-idle\""));
        Assert.Equal(4, Count(svg, "class=\"gnougnou-text-wave\""));
        Assert.Contains("@keyframes gnougnou-text-idle", svg, StringComparison.Ordinal);
        Assert.Contains("@keyframes gnougnou-text-wave", svg, StringComparison.Ordinal);
        Assert.Contains("--wave-delay:4.5s", svg, StringComparison.Ordinal);
        Assert.Contains("--wave-delay:5.52s", svg, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("transform=\"translate(", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_StaticAnimation_OmitsAnimationCss()
    {
        var svg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "Still",
            Animation = GnouGnouTextAnimation.None
        });

        Assert.DoesNotContain("@keyframes", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("gnougnou-text-idle", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("prefers-reduced-motion", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_UnicodeCombiningSequence_AnimatesAsOneTextElement()
    {
        var svg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "A\u0301B",
            Animation = GnouGnouTextAnimation.Idle
        });

        Assert.Equal(2, Count(svg, "data-letter-index="));
    }

    [Fact]
    public void Generate_UserTextAndMetadata_AreXmlEscaped()
    {
        var svg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "<script>",
            Title = "A < B & C",
            Description = "Safe \"description\""
        });

        _ = XDocument.Parse(svg);
        Assert.DoesNotContain("<script>", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;", svg, StringComparison.Ordinal);
        Assert.Contains("A &lt; B &amp; C", svg, StringComparison.Ordinal);
        Assert.Contains("Safe &quot;description&quot;", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_SvgIdPrefix_NamespacesDefinitionsStylesAndReferences()
    {
        var svg = GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "Brand",
            Animation = GnouGnouTextAnimation.Idle,
            SvgIdPrefix = "brand.one"
        });

        Assert.Contains("id=\"brand.one-root\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"brand.one-gradient\"", svg, StringComparison.Ordinal);
        Assert.Contains("url(#brand.one-gradient)", svg, StringComparison.Ordinal);
        Assert.Contains("#brand\\.one-root", svg, StringComparison.Ordinal);
        Assert.Contains("@keyframes brand-one-wave", svg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(1025)]
    public void Generate_InvalidSize_Throws(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GnouGnouTextSvgGenerator.Generate("Text", size));
    }

    [Fact]
    public void Generate_InvalidTextOrStyleOptions_Throw()
    {
        Assert.Throws<ArgumentException>(() => GnouGnouTextSvgGenerator.Generate(" ", 64));
        Assert.Throws<ArgumentException>(() => GnouGnouTextSvgGenerator.Generate("two\nlines", 64));
        Assert.Throws<ArgumentException>(() => GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "Unsafe color",
            GradientColors = ["#000000", "url(http://example.test)"]
        }));
        Assert.Throws<ArgumentException>(() => GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "One color",
            GradientColors = ["#000000"]
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "Too many stars",
            StarCount = 9
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => GnouGnouTextSvgGenerator.Generate(new GnouGnouTextOptions
        {
            Text = "Invalid animation",
            Animation = (GnouGnouTextAnimation)999
        }));
    }

    private static int Width(XDocument document) => int.Parse(document.Root!.Attribute("width")!.Value);

    private static int Height(XDocument document) => int.Parse(document.Root!.Attribute("height")!.Value);

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
