using System.Globalization;
using System.Text;
using System.Xml.Linq;
using GnOuGo.Assets.Bears.Layers;

namespace GnOuGo.Assets.Bears;

/// <summary>
/// Generates an automatically sized horizontal SVG lockup containing a
/// GnouGnou mascot followed by rounded gradient text.
/// </summary>
public static class GnouGnouBearTextSvgGenerator
{
    private const double MaxGap = 4096d;
    private const double BearOpticalCenterRatio = 0.555d;
    private const double TextOpticalCenterFromBaselineRatio = 0.34d;
    private const string DefaultDescription =
        "GnOuGo lockup combining the GnouGnou teddy bear mascot with rounded gradient text.";

    /// <summary>
    /// Generates a composite from complete bear and text options.
    /// </summary>
    public static string Generate(
        GnouGnouBearOptions bearOptions,
        GnouGnouTextOptions textOptions,
        double gap = 24d) =>
        Generate(new GnouGnouBearTextOptions
        {
            BearOptions = bearOptions,
            TextOptions = textOptions,
            Gap = gap
        });

    /// <summary>
    /// Generates a standalone composite SVG from the supplied options.
    /// </summary>
    public static string Generate(GnouGnouBearTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BearOptions);
        ArgumentNullException.ThrowIfNull(options.TextOptions);

        if (!double.IsFinite(options.Gap) || options.Gap is < 0d or > MaxGap)
            throw new ArgumentOutOfRangeException(nameof(options.Gap), options.Gap, "Gap must be between zero and 4096.");

        ValidateSvgIdPrefix(options.SvgIdPrefix);
        var prefix = options.SvgIdPrefix ?? "gnougnou-bear-text";
        var bearOptions = options.BearOptions with { SvgIdPrefix = $"{prefix}-bear" };
        var textOptions = options.TextOptions with { SvgIdPrefix = $"{prefix}-text" };
        var bearSvg = GnouGnouBearSvgGenerator.Generate(bearOptions);
        var textSvg = GnouGnouTextSvgGenerator.Generate(textOptions);
        var bearRoot = ReadRoot(bearSvg);
        var textRoot = ReadRoot(textSvg);
        var bearSize = ReadSize(bearRoot);
        var textSize = ReadSize(textRoot);
        var bearOpticalCenter = bearSize.Height * BearOpticalCenterRatio;
        var textOpticalCenter = ReadTextOpticalCenter(textRoot);
        var relativeTextY = bearOpticalCenter - textOpticalCenter;
        var canvasTop = Math.Min(0d, relativeTextY);
        var bearY = canvasTop < 0d ? -canvasTop : 0d;
        var textY = relativeTextY - canvasTop;
        var canvasWidth = (int)Math.Ceiling(bearSize.Width + options.Gap + textSize.Width);
        var canvasHeight = (int)Math.Ceiling(Math.Max(bearY + bearSize.Height, textY + textSize.Height));
        var textX = bearSize.Width + options.Gap;
        var titleId = $"{prefix}-title";
        var descriptionId = $"{prefix}-desc";
        var title = SvgText.Escape(options.Title ?? $"{options.TextOptions.Text} with GnouGnou");
        var description = SvgText.Escape(options.Description ?? DefaultDescription);
        var animated = options.BearOptions.Animation != GnouGnouBearAnimation.None
            || options.TextOptions.Animation != GnouGnouTextAnimation.None;
        var builder = new StringBuilder(capacity: bearSvg.Length + textSvg.Length + 1200);

        builder.Append("<svg id=\"").Append(prefix).Append("-root\" width=\"").Append(canvasWidth)
            .Append("\" height=\"").Append(canvasHeight).Append("\" viewBox=\"0 0 ")
            .Append(canvasWidth).Append(' ').Append(canvasHeight)
            .Append("\" xmlns=\"http&#58;//www.w3.org/2000/svg\" role=\"img\" aria-labelledby=\"")
            .Append(titleId).Append(' ').Append(descriptionId)
            .Append("\" data-layout=\"horizontal\" data-vertical-alignment=\"optical-center\" data-animated=\"")
            .Append(animated ? "true" : "false").Append("\" data-bear-animation=\"")
            .Append(options.BearOptions.Animation.ToString().ToLowerInvariant())
            .Append("\" data-text-animation=\"")
            .Append(options.TextOptions.Animation.ToString().ToLowerInvariant()).AppendLine("\">");
        builder.Append("  <title id=\"").Append(titleId).Append("\">").Append(title).AppendLine("</title>");
        builder.Append("  <desc id=\"").Append(descriptionId).Append("\">").Append(description).AppendLine("</desc>");
        AppendNestedSvg(builder, "bear", 0d, bearY, bearSvg);
        AppendNestedSvg(builder, "text", textX, textY, textSvg);
        builder.Append("</svg>");

        return builder.ToString();
    }

    private static void AppendNestedSvg(
        StringBuilder builder,
        string part,
        double x,
        double y,
        string svg)
    {
        builder.Append("  <g data-part=\"").Append(part).Append("\" transform=\"translate(")
            .Append(Number(x)).Append(' ').Append(Number(y))
            .AppendLine(")\" aria-hidden=\"true\" focusable=\"false\">");
        foreach (var line in svg.Split('\n'))
            builder.Append("    ").AppendLine(line);
        builder.AppendLine("  </g>");
    }

    private static XElement ReadRoot(string svg)
    {
        return XDocument.Parse(svg).Root
            ?? throw new InvalidOperationException("Generated SVG does not have a root element.");
    }

    private static SvgSize ReadSize(XElement root)
    {
        var width = ParseDimension(root, "width");
        var height = ParseDimension(root, "height");
        return new SvgSize(width, height);
    }

    private static double ReadTextOpticalCenter(XElement root)
    {
        var firstLetter = root.Descendants()
            .FirstOrDefault(static element => element.Name.LocalName == "text"
                && element.Attribute("data-letter-index") is not null)
            ?? throw new InvalidOperationException("Generated text SVG does not contain a visible letter.");
        var baseline = ParseDimension(firstLetter, "y");
        var fontSize = ParseDimension(firstLetter, "font-size");
        return baseline - fontSize * TextOpticalCenterFromBaselineRatio;
    }

    private static double ParseDimension(XElement root, string attributeName)
    {
        var value = root.Attribute(attributeName)?.Value;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dimension)
            || !double.IsFinite(dimension)
            || dimension <= 0d)
        {
            throw new InvalidOperationException($"Generated SVG has an invalid {attributeName}.");
        }

        return dimension;
    }

    private static void ValidateSvgIdPrefix(string? prefix)
    {
        if (prefix is null)
            return;

        if (prefix.Length is 0 or > 64 || !IsPrefixStart(prefix[0]) || prefix.Any(static character => !IsPrefixCharacter(character)))
            throw new ArgumentException(
                "SvgIdPrefix must start with a letter or underscore and contain only letters, digits, underscores, dots, or hyphens.",
                nameof(GnouGnouBearTextOptions.SvgIdPrefix));
    }

    private static bool IsPrefixStart(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsPrefixCharacter(char character) =>
        IsPrefixStart(character) || character is >= '0' and <= '9' or '.' or '-';

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record SvgSize(double Width, double Height);
}
