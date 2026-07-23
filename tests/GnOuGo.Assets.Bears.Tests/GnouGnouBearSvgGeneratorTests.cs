using GnOuGo.Assets.Bears;
using Xunit;

namespace GnOuGo.Assets.Bears.Tests;

public sealed class GnouGnouBearSvgGeneratorTests
{
    [Fact]
    public void Generate_SameOptions_ProducesSameSvg()
    {
        var options = new GnouGnouBearOptions
        {
            Seed = 42,
            Role = GnouGnouBearRole.Coder,
            Emotion = GnouGnouBearEmotion.Happy,
            Accessory = GnouGnouBearAccessory.Laptop,
            State = GnouGnouBearState.Running,
            Theme = GnouGnouBearTheme.Default
        };

        var first = GnouGnouBearSvgGenerator.Generate(options);
        var second = GnouGnouBearSvgGenerator.Generate(options);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Generate_DifferentRole_ChangesSvg()
    {
        var first = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Role = GnouGnouBearRole.Coder });
        var second = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Role = GnouGnouBearRole.Reviewer });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Generate_DifferentEmotion_ChangesSvg()
    {
        var first = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Emotion = GnouGnouBearEmotion.Happy });
        var second = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Emotion = GnouGnouBearEmotion.Thinking });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Generate_DifferentAccessory_ChangesSvg()
    {
        var first = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Accessory = GnouGnouBearAccessory.None });
        var second = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Accessory = GnouGnouBearAccessory.Glasses });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Generate_DifferentState_ChangesSvg()
    {
        var first = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { State = GnouGnouBearState.Idle });
        var second = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { State = GnouGnouBearState.Success });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Generate_DifferentFurPalette_ChangesSvg()
    {
        var first = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { FurPalette = GnouGnouBearFurPalette.Classic });
        var second = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { FurPalette = GnouGnouBearFurPalette.Blueberry });

        Assert.NotEqual(first, second);
        Assert.Contains("#84AEE8", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DifferentEyeStyle_ChangesSvg()
    {
        var first = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { EyeStyle = GnouGnouBearEyeStyle.Default });
        var second = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { EyeStyle = GnouGnouBearEyeStyle.Starry });

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Generate_CanHideHeadphonesAndBowTie()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            HasHeadphones = false,
            HasBowTie = false
        });

        Assert.DoesNotContain("rx=\"22\" ry=\"32\" fill=\"#243B86\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("fill=\"url(#bow)\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_UmbrellaAccessory_RendersUmbrella()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            Accessory = GnouGnouBearAccessory.Umbrella
        });

        Assert.Contains("C72 125 111 117 140 142", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_MultipleAccessories_RendersFirstThreeDistinctAccessories()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            Accessories =
            [
                GnouGnouBearAccessory.Necktie,
                GnouGnouBearAccessory.SoccerBall,
                GnouGnouBearAccessory.Crown,
                GnouGnouBearAccessory.Rocket
            ]
        });

        Assert.Contains("L141 215 L128 229", svg, StringComparison.Ordinal);
        Assert.Contains("M190 199 L199 206", svg, StringComparison.Ordinal);
        Assert.Contains("M96 54 L111 34", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("C205 158 207 184", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_AccessoryColorVariant_ChangesAccessoryPalette()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            Accessory = GnouGnouBearAccessory.Laptop,
            AccessoryColorVariant = 2
        });

        Assert.Contains("#527FE8", svg, StringComparison.Ordinal);
        Assert.Contains("#315CB9", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Beard_RendersDeterministicBeard()
    {
        var options = new GnouGnouBearOptions { Seed = 42, HasBeard = true };

        var first = GnouGnouBearSvgGenerator.Generate(options);
        var second = GnouGnouBearSvgGenerator.Generate(options);

        Assert.Equal(first, second);
        Assert.Contains("opacity=\"0.99\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_HeadphonesAccessory_ShowsHiddenHeadphones()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            HasHeadphones = false,
            Accessories = [GnouGnouBearAccessory.Headphones]
        });

        Assert.Contains("rx=\"22\" ry=\"32\" fill=\"#243B86\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_AllEnumValues_GenerateValidNonEmptySvg()
    {
        foreach (var role in Enum.GetValues<GnouGnouBearRole>())
        foreach (var emotion in Enum.GetValues<GnouGnouBearEmotion>())
        foreach (var accessory in Enum.GetValues<GnouGnouBearAccessory>())
        foreach (var state in Enum.GetValues<GnouGnouBearState>())
        foreach (var theme in Enum.GetValues<GnouGnouBearTheme>())
        foreach (var furPalette in Enum.GetValues<GnouGnouBearFurPalette>())
        foreach (var eyeStyle in Enum.GetValues<GnouGnouBearEyeStyle>())
        {
            var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
            {
                Role = role,
                Emotion = emotion,
                Accessory = accessory,
                State = state,
                Theme = theme,
                FurPalette = furPalette,
                EyeStyle = eyeStyle
            });

            Assert.False(string.IsNullOrWhiteSpace(svg));
            Assert.Contains("<svg", svg, StringComparison.Ordinal);
            Assert.Contains("</svg>", svg, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Generate_OutputContainsRequiredSvgElements()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions());

        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("<title", svg, StringComparison.Ordinal);
        Assert.Contains("<desc", svg, StringComparison.Ordinal);
        Assert.Contains("</svg>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_OutputExcludesUnsafeSvgContent()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            Title = "<script>alert(1)</script>",
            Description = "javascript: http://example.test https://example.test data:image"
        });

        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("foreignObject", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:image", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(1024)]
    public void Generate_ValidSize_Works(int size)
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Size = size });

        Assert.Contains($"width=\"{size}\"", svg, StringComparison.Ordinal);
        Assert.Contains($"height=\"{size}\"", svg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(1025)]
    public void Generate_InvalidSize_Throws(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Size = size }));
    }

    [Fact]
    public void Generate_TitleAndDescription_AreEscaped()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            Title = "Gnou <Bear> & \"Friend\"",
            Description = "Soft 'safe' mascot"
        });

        Assert.Contains("Gnou &lt;Bear&gt; &amp; &quot;Friend&quot;", svg, StringComparison.Ordinal);
        Assert.Contains("Soft &apos;safe&apos; mascot", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DefaultOutput_IncludesCanonicalIdentityMarkers()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions());

        Assert.Contains(">GnouGnou</title>", svg, StringComparison.Ordinal);
        Assert.Contains("stroke=\"url(#blue)\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"url(#bow)\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"url(#muzzle)\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"url(#eye)\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#F79AA0\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_SvgIdPrefix_NamespacesDefinitionsAndReferences()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            SvgIdPrefix = "actor-master"
        });

        Assert.Contains("id=\"actor-master-gnougnou-title\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"actor-master-fur\"", svg, StringComparison.Ordinal);
        Assert.Contains("url(#actor-master-fur)", svg, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"actor-master-gnougnou-title actor-master-gnougnou-desc\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"fur\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithoutSvgIdPrefix_PreservesLegacyIdsAndReferences()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions());

        Assert.Contains("id=\"gnougnou-title\"", svg, StringComparison.Ordinal);
        Assert.Contains("id=\"fur\"", svg, StringComparison.Ordinal);
        Assert.Contains("url(#fur)", svg, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby=\"gnougnou-title gnougnou-desc\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_AnimationRig_ExposesSemanticBodyParts()
    {
        var svg = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            EnableAnimationRig = true,
            HasBeard = true
        });

        Assert.Contains("data-animation-rig=\"true\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"head\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"ear-left\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"ear-right\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"cheek-left\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"cheek-right\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"arm-left\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"arm-right\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"leg-left\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"leg-right\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"pupil-left\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"mouth\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-part=\"beard\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("data-part=\"ground-shadow\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_DisabledAnimationRig_PreservesDefaultOutput()
    {
        var implicitDefault = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions { Seed = 27 });
        var explicitDefault = GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            Seed = 27,
            EnableAnimationRig = false
        });

        Assert.Equal(implicitDefault, explicitDefault);
        Assert.DoesNotContain("data-animation-rig", implicitDefault, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("9actor")]
    [InlineData("actor<script>")]
    public void Generate_InvalidSvgIdPrefix_Throws(string prefix)
    {
        Assert.Throws<ArgumentException>(() => GnouGnouBearSvgGenerator.Generate(new GnouGnouBearOptions
        {
            SvgIdPrefix = prefix
        }));
    }
}
