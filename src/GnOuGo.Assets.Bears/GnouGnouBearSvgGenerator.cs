using System.Text;
using GnOuGo.Assets.Bears.Layers;

namespace GnOuGo.Assets.Bears;

public static class GnouGnouBearSvgGenerator
{
    private const int MinSize = 64;
    private const int MaxSize = 1024;
    private const string DefaultTitle = "GnouGnou";
    private const string DefaultDescription = "Cute GnOuGo teddy bear mascot with blue headphones and turquoise bow tie.";

    public static string Generate(GnouGnouBearOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Size is < MinSize or > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(options), options.Size, "Size must be between 64 and 1024.");

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
        builder.Append(BackgroundLayer.Render(options.Theme));
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
        builder.Append("</svg>");

        return builder.ToString();
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
