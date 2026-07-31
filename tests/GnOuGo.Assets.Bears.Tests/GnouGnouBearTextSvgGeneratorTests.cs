using System.Globalization;
using System.Xml.Linq;
using GnOuGo.Assets.Bears;
using Xunit;

namespace GnOuGo.Assets.Bears.Tests;

public sealed class GnouGnouBearTextSvgGeneratorTests
{
    [Fact]
    public void Generate_DefaultOptions_ProducesAutomaticallySizedHorizontalComposite()
    {
        var svg = GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions());
        var document = XDocument.Parse(svg);
        var root = document.Root!;
        var bear = Part(document, "bear").Elements().Single(element => element.Name.LocalName == "svg");
        var text = Part(document, "text").Elements().Single(element => element.Name.LocalName == "svg");

        Assert.Equal("horizontal", root.Attribute("data-layout")?.Value);
        Assert.Equal("optical-center", root.Attribute("data-vertical-alignment")?.Value);
        Assert.Equal("false", root.Attribute("data-animated")?.Value);
        Assert.Equal(3, document.Descendants().Count(element => element.Name.LocalName == "svg"));
        Assert.Equal(
            int.Parse(bear.Attribute("width")!.Value)
            + 24
            + int.Parse(text.Attribute("width")!.Value),
            int.Parse(root.Attribute("width")!.Value));
        Assert.Equal(
            Math.Max(int.Parse(bear.Attribute("height")!.Value), int.Parse(text.Attribute("height")!.Value)),
            int.Parse(root.Attribute("height")!.Value));
        Assert.Contains("id=\"gnougnou-bear-text-bear-fur\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"gnougnou-bear-text-text-gradient\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_BearAndTextAnimations_PlayIndependently()
    {
        var svg = GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            BearOptions = new GnouGnouBearOptions
            {
                Theme = GnouGnouBearTheme.Transparent,
                Animation = GnouGnouBearAnimation.Idle
            },
            TextOptions = new GnouGnouTextOptions
            {
                Text = "Animated",
                Animation = GnouGnouTextAnimation.Bounce
            }
        });
        var document = XDocument.Parse(svg);
        var root = document.Root!;

        Assert.Equal("true", root.Attribute("data-animated")?.Value);
        Assert.Equal("idle", root.Attribute("data-bear-animation")?.Value);
        Assert.Equal("bounce", root.Attribute("data-text-animation")?.Value);
        Assert.NotNull(document.Descendants().Single(element => element.Attribute("data-animation-rig")?.Value == "true"));
        Assert.Contains("class=\"gnougnou-text-bounce\"", svg, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CustomInputs_ArePreservedAcrossBothComponents()
    {
        var svg = GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            BearOptions = new GnouGnouBearOptions
            {
                Theme = GnouGnouBearTheme.Transparent,
                FurPalette = GnouGnouBearFurPalette.Blueberry,
                EyeStyle = GnouGnouBearEyeStyle.Starry,
                EnableAnimationRig = true,
                HasBeard = true,
                BeardStyle = GnouGnouBearBeardStyle.Cloud
            },
            TextOptions = new GnouGnouTextOptions
            {
                Text = "Custom",
                GradientColors = ["#7C3AED", "#06B6D4"],
                StarCount = 4,
                HorizontalMargin = 30,
                VerticalMargin = 18
            }
        });

        Assert.Contains("#84AEE8", svg, StringComparison.Ordinal);
        Assert.Contains("data-eye-style=\"starry\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-beard-style=\"cloud\"", svg, StringComparison.Ordinal);
        Assert.Contains("stop-color=\"#7C3AED\"", svg, StringComparison.Ordinal);
        Assert.Equal(4, Count(svg, "data-part=\"star\""));
    }

    [Fact]
    public void Generate_Gap_ChangesOnlyCompositeWidth()
    {
        var compact = XDocument.Parse(GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            Gap = 10
        }));
        var spacious = XDocument.Parse(GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            Gap = 110
        }));

        Assert.Equal(100, Width(spacious) - Width(compact));
        Assert.Equal(Height(compact), Height(spacious));
    }

    [Fact]
    public void Generate_AlignsTextOpticallyWithBearCenterOfGravity()
    {
        var document = XDocument.Parse(GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            BearOptions = new GnouGnouBearOptions
            {
                Size = 320,
                Theme = GnouGnouBearTheme.Transparent
            },
            TextOptions = new GnouGnouTextOptions
            {
                Text = "Center",
                Size = 64,
                StarCount = 0,
                HorizontalMargin = 0,
                VerticalMargin = 0
            }
        }));
        var bearTransform = Part(document, "bear").Attribute("transform")!.Value;
        var textTransform = Part(document, "text").Attribute("transform")!.Value;

        Assert.Equal("translate(0 0)", bearTransform);
        Assert.Equal("translate(344 146.88)", textTransform);
    }

    [Fact]
    public void Generate_VerticalTextMargin_DoesNotChangeArtworkAlignment()
    {
        var compact = XDocument.Parse(GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            BearOptions = new GnouGnouBearOptions { Size = 320, Theme = GnouGnouBearTheme.Transparent },
            TextOptions = new GnouGnouTextOptions
            {
                Text = "Aligned",
                Size = 64,
                StarCount = 0,
                VerticalMargin = 0
            }
        }));
        var spacious = XDocument.Parse(GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            BearOptions = new GnouGnouBearOptions { Size = 320, Theme = GnouGnouBearTheme.Transparent },
            TextOptions = new GnouGnouTextOptions
            {
                Text = "Aligned",
                Size = 64,
                StarCount = 0,
                VerticalMargin = 80
            }
        }));

        Assert.Equal(FirstLetterAbsoluteBaseline(compact), FirstLetterAbsoluteBaseline(spacious), precision: 3);
    }

    [Fact]
    public void Generate_SameOptions_ProducesSameSvg()
    {
        var options = new GnouGnouBearTextOptions
        {
            BearOptions = new GnouGnouBearOptions { Seed = 42, Theme = GnouGnouBearTheme.Transparent },
            TextOptions = new GnouGnouTextOptions { Text = "Deterministic", Animation = GnouGnouTextAnimation.Wave },
            Gap = 36
        };

        Assert.Equal(
            GnouGnouBearTextSvgGenerator.Generate(options),
            GnouGnouBearTextSvgGenerator.Generate(options));
    }

    [Fact]
    public void Generate_AccessibleMetadata_IsEscapedAndNestedArtworkIsHidden()
    {
        var svg = GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            Title = "Bear < Text & Friends",
            Description = "Safe \"lockup\""
        });

        _ = XDocument.Parse(svg);
        Assert.Contains("Bear &lt; Text &amp; Friends", svg, StringComparison.Ordinal);
        Assert.Contains("Safe &quot;lockup&quot;", svg, StringComparison.Ordinal);
        Assert.Equal(2, Count(svg, "aria-hidden=\"true\" focusable=\"false\""));
    }

    [Fact]
    public void Generate_CompositePrefix_OverridesNestedPrefixes()
    {
        var svg = GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            SvgIdPrefix = "lockup.one",
            BearOptions = new GnouGnouBearOptions { SvgIdPrefix = "ignored-bear" },
            TextOptions = new GnouGnouTextOptions { SvgIdPrefix = "ignored-text" }
        });

        Assert.Contains("id=\"lockup.one-root\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"lockup.one-bear-fur\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"lockup.one-text-gradient\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored-bear", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("ignored-text", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_InvalidCompositeOptions_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => GnouGnouBearTextSvgGenerator.Generate((GnouGnouBearTextOptions)null!));
        Assert.Throws<ArgumentNullException>(() => GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            BearOptions = null!
        }));
        Assert.Throws<ArgumentNullException>(() => GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            TextOptions = null!
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            Gap = -1
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            Gap = double.PositiveInfinity
        }));
        Assert.Throws<ArgumentException>(() => GnouGnouBearTextSvgGenerator.Generate(new GnouGnouBearTextOptions
        {
            SvgIdPrefix = "invalid prefix"
        }));
    }

    private static XElement Part(XDocument document, string part) =>
        document.Descendants().Single(element => element.Attribute("data-part")?.Value == part);

    private static int Width(XDocument document) =>
        int.Parse(document.Root!.Attribute("width")!.Value, CultureInfo.InvariantCulture);

    private static int Height(XDocument document) =>
        int.Parse(document.Root!.Attribute("height")!.Value, CultureInfo.InvariantCulture);

    private static double FirstLetterAbsoluteBaseline(XDocument document)
    {
        var textPart = Part(document, "text");
        var transform = textPart.Attribute("transform")!.Value;
        var separator = transform.LastIndexOf(' ');
        var translationY = double.Parse(transform[(separator + 1)..^1], CultureInfo.InvariantCulture);
        var baseline = double.Parse(
            textPart.Descendants().First(element => element.Attribute("data-letter-index") is not null)
                .Attribute("y")!.Value,
            CultureInfo.InvariantCulture);
        return translationY + baseline;
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
