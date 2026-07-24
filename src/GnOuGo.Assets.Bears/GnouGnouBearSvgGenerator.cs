using System.Text;
using GnOuGo.Assets.Bears.Layers;

namespace GnOuGo.Assets.Bears;

public static class GnouGnouBearSvgGenerator
{
    private const int MinSize = 64;
    private const int MaxSize = 1024;
    private const string DefaultTitle = "GnouGnou";
    private const string DefaultDescription = "Cute GnOuGo teddy bear mascot with blue headphones and turquoise bow tie.";
    private static readonly string[] SvgIds =
    [
        "gnougnou-title", "gnougnou-desc", "bg", "fur", "fur-light", "muzzle",
        "eye", "blue", "blue-dark", "bow", "drop", "soft"
    ];

    public static string Generate(GnouGnouBearOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Size is < MinSize or > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(options), options.Size, "Size must be between 64 and 1024.");
        if (!Enum.IsDefined(options.Animation))
            throw new ArgumentOutOfRangeException(nameof(options), options.Animation, "Unsupported GnOuGo animation.");

        ValidateSvgIdPrefix(options.SvgIdPrefix);

        var stableRandom = new StableRandom(options.Seed);
        var accessories = NormalizeAccessories(options);
        var accessoryPalette = AccessoryPalette.FromVariant(options.AccessoryColorVariant);
        var hasHeadphones = options.HasHeadphones || accessories.Contains(GnouGnouBearAccessory.Headphones);
        var hasBowTie = options.HasBowTie || accessories.Contains(GnouGnouBearAccessory.BowTie);
        var title = SvgText.Escape(options.Title ?? DefaultTitle);
        var description = SvgText.Escape(options.Description ?? DefaultDescription);

        var builder = new StringBuilder(capacity: 18000);
        builder.Append("<svg width=\"").Append(options.Size).Append("\" height=\"").Append(options.Size).Append("\" viewBox=\"0 0 256 256\" xmlns=\"http&#58;//www.w3.org/2000/svg\" role=\"img\" aria-labelledby=\"gnougnou-title gnougnou-desc\">");
        builder.AppendLine();
        builder.Append("  <title id=\"gnougnou-title\">").Append(title).AppendLine("</title>");
        builder.Append("  <desc id=\"gnougnou-desc\">").Append(description).AppendLine("</desc>");
        builder.AppendLine();
        builder.Append(GnouGnouBaseLayer.Defs(options.FurPalette, accessoryPalette));
        builder.AppendLine();
        builder.Append(GnouGnouAnimationStyleLayer.Render(options.Animation));
        builder.Append(BackgroundLayer.Render(options.Theme));
        if (options.EnableAnimationRig || options.Animation != GnouGnouBearAnimation.None)
        {
            builder.Append(RiggedGnouGnouLayer.Render(options, hasHeadphones, hasBowTie, accessoryPalette));
        }
        else
        {
            builder.Append(GnouGnouBaseLayer.OpenMascotGroup());
            builder.Append(GnouGnouBaseLayer.BodyBeforeFace(hasHeadphones, hasBowTie, accessoryPalette));
            builder.Append(EyesLayer.Render(options.Emotion, options.EyeStyle, ref stableRandom));
            builder.Append(MouthLayer.Render(options.Emotion));
            builder.Append(BeardLayer.Render(options.HasBeard, ref stableRandom));
            builder.Append(GnouGnouBaseLayer.AfterFace(hasHeadphones, hasBowTie, accessoryPalette));
            builder.Append(AccessoryLayer.Render(accessories, ref stableRandom, accessoryPalette));
            builder.Append(RoleBadgeLayer.Render(options.Role, ref stableRandom));
            builder.Append(StateLayer.Render(options.State, ref stableRandom));
            builder.Append(GnouGnouBaseLayer.CloseMascotGroup());
        }
        builder.Append("</svg>");

        return ApplySvgIdPrefix(builder.ToString(), options.SvgIdPrefix);
    }

    private static void ValidateSvgIdPrefix(string? prefix)
    {
        if (prefix is null)
            return;

        if (prefix.Length is 0 or > 64 || !IsPrefixStart(prefix[0]) || prefix.Any(static character => !IsPrefixCharacter(character)))
            throw new ArgumentException(
                "SvgIdPrefix must start with a letter or underscore and contain only letters, digits, underscores, dots, or hyphens.",
                nameof(GnouGnouBearOptions.SvgIdPrefix));
    }

    private static bool IsPrefixStart(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsPrefixCharacter(char character) =>
        IsPrefixStart(character) || character is >= '0' and <= '9' or '.' or '-';

    private static string ApplySvgIdPrefix(string svg, string? prefix)
    {
        if (prefix is null)
            return svg;

        foreach (var id in SvgIds)
        {
            var prefixedId = $"{prefix}-{id}";
            svg = svg.Replace($"id=\"{id}\"", $"id=\"{prefixedId}\"", StringComparison.Ordinal)
                .Replace($"url(#{id})", $"url(#{prefixedId})", StringComparison.Ordinal);
        }

        return svg.Replace(
            "aria-labelledby=\"gnougnou-title gnougnou-desc\"",
            $"aria-labelledby=\"{prefix}-gnougnou-title {prefix}-gnougnou-desc\"",
            StringComparison.Ordinal);
    }

    private static IReadOnlyList<GnouGnouBearAccessory> NormalizeAccessories(GnouGnouBearOptions options)
    {
        var source = options.Accessories.Count > 0
            ? options.Accessories
            : options.Accessory == GnouGnouBearAccessory.None
                ? []
                : [options.Accessory];

        return source
            .Where(static accessory => accessory != GnouGnouBearAccessory.None)
            .Distinct()
            .Take(3)
            .ToArray();
    }
}
