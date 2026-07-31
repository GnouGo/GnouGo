using System.Globalization;
using System.Text;
using GnOuGo.Assets.Bears.Layers;

namespace GnOuGo.Assets.Bears;

/// <summary>
/// Generates standalone rounded gradient text SVGs inspired by the GnOuGo wordmark.
/// </summary>
public static class GnouGnouTextSvgGenerator
{
    private const int MinSize = 16;
    private const int MaxSize = 1024;
    private const int MaxTextElements = 128;
    private const int MinGradientColors = 2;
    private const int MaxGradientColors = 8;
    private const int MaxStars = 8;
    private const double MaxMargin = 4096d;
    private const string DefaultDescription =
        "Rounded GnOuGo text with a horizontal gradient and decorative four-point stars.";

    /// <summary>
    /// Generates text using the default gradient, stars, and static presentation.
    /// </summary>
    public static string Generate(string text, int size) =>
        Generate(new GnouGnouTextOptions { Text = text, Size = size });

    /// <summary>
    /// Generates a standalone SVG from the supplied options.
    /// </summary>
    public static string Generate(GnouGnouTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var textElements = GetTextElements(options.Text);
        var visibleElements = textElements.Where(static element => !IsWhiteSpace(element)).ToArray();
        var layout = CreateLayout(options, textElements);
        var ids = CreateIds(options.SvgIdPrefix);
        var title = SvgText.Escape(options.Title ?? options.Text);
        var description = SvgText.Escape(options.Description ?? DefaultDescription);
        var starColor = options.StarColor ?? options.GradientColors[^1];
        var builder = new StringBuilder(capacity: 7000 + options.Text.Length * 260);

        builder.Append("<svg id=\"").Append(ids.Root).Append("\" width=\"").Append(layout.CanvasWidth)
            .Append("\" height=\"").Append(layout.CanvasHeight).Append("\" viewBox=\"0 0 ")
            .Append(layout.CanvasWidth).Append(' ').Append(layout.CanvasHeight)
            .Append("\" xmlns=\"http&#58;//www.w3.org/2000/svg\" role=\"img\" aria-labelledby=\"")
            .Append(ids.Title).Append(' ').Append(ids.Description).AppendLine("\">");
        builder.Append("  <title id=\"").Append(ids.Title).Append("\">").Append(title).AppendLine("</title>");
        builder.Append("  <desc id=\"").Append(ids.Description).Append("\">").Append(description).AppendLine("</desc>");
        AppendGradient(builder, options.GradientColors, layout, ids.Gradient);
        AppendAnimationStyle(builder, options.Animation, visibleElements.Length, layout, ids);
        builder.Append("  <g class=\"gnougnou-text\" data-animation=\"")
            .Append(ToAnimationToken(options.Animation)).AppendLine("\">");
        AppendLetters(builder, textElements, layout, ids.Gradient, options.Animation);
        AppendStars(builder, options, layout, starColor);
        builder.AppendLine("  </g>");
        builder.Append("</svg>");

        return builder.ToString();
    }

    private static void ValidateOptions(GnouGnouTextOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Text))
            throw new ArgumentException("Text must contain at least one visible character.", nameof(options));
        if (options.Text.Any(char.IsControl))
            throw new ArgumentException("Text must be a single line and cannot contain control characters.", nameof(options));
        if (options.Size is < MinSize or > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(options), options.Size, "Size must be between 16 and 1024.");
        ValidateMargin(options.HorizontalMargin, nameof(GnouGnouTextOptions.HorizontalMargin));
        ValidateMargin(options.VerticalMargin, nameof(GnouGnouTextOptions.VerticalMargin));
        if (!Enum.IsDefined(options.Animation))
            throw new ArgumentOutOfRangeException(nameof(options), options.Animation, "Unsupported GnOuGo text animation.");
        if (options.GradientColors is null || options.GradientColors.Count is < MinGradientColors or > MaxGradientColors)
            throw new ArgumentException("GradientColors must contain between two and eight colors.", nameof(options));
        if (options.GradientColors.Any(static color => !IsHexColor(color)))
            throw new ArgumentException("GradientColors accepts hexadecimal SVG colors only.", nameof(options));
        if (options.StarColor is not null && !IsHexColor(options.StarColor))
            throw new ArgumentException("StarColor must be a hexadecimal SVG color.", nameof(options));
        if (options.StarCount is < 0 or > MaxStars)
            throw new ArgumentOutOfRangeException(nameof(options), options.StarCount, "StarCount must be between zero and eight.");
        if (!double.IsFinite(options.StarScale) || options.StarScale is < 0.25d or > 3d)
            throw new ArgumentOutOfRangeException(nameof(options), options.StarScale, "StarScale must be between 0.25 and 3.");

        ValidateSvgIdPrefix(options.SvgIdPrefix);

        if (GetTextElements(options.Text).Count > MaxTextElements)
            throw new ArgumentException("Text cannot contain more than 128 Unicode text elements.", nameof(options));
    }

    private static void AppendGradient(
        StringBuilder builder,
        IReadOnlyList<string> colors,
        TextLayout layout,
        string gradientId)
    {
        builder.AppendLine("  <defs>");
        builder.Append("    <linearGradient id=\"").Append(gradientId).Append("\" x1=\"")
            .Append(Number(layout.TextX)).Append("\" y1=\"0\" x2=\"")
            .Append(Number(layout.TextX + layout.TextWidth))
            .AppendLine("\" y2=\"0\" gradientUnits=\"userSpaceOnUse\">");

        for (var index = 0; index < colors.Count; index++)
        {
            var offset = index * 100d / (colors.Count - 1);
            builder.Append("      <stop offset=\"").Append(Number(offset)).Append("%\" stop-color=\"")
                .Append(colors[index]).AppendLine("\"/>");
        }

        builder.AppendLine("    </linearGradient>");
        builder.AppendLine("  </defs>");
    }

    private static void AppendAnimationStyle(
        StringBuilder builder,
        GnouGnouTextAnimation animation,
        int visibleLetterCount,
        TextLayout layout,
        SvgIds ids)
    {
        if (animation == GnouGnouTextAnimation.None)
            return;

        switch (animation)
        {
            case GnouGnouTextAnimation.Idle:
                AppendIdleAnimationStyle(builder, visibleLetterCount, layout, ids);
                return;
            case GnouGnouTextAnimation.Wave:
                AppendWaveAnimationStyle(builder, layout, ids);
                return;
            case GnouGnouTextAnimation.Bounce:
                AppendBounceAnimationStyle(builder, visibleLetterCount, layout, ids);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(animation), animation, "Unsupported GnOuGo text animation.");
        }
    }

    private static void AppendIdleAnimationStyle(
        StringBuilder builder,
        int visibleLetterCount,
        TextLayout layout,
        SvgIds ids)
    {

        var idleLift = Number(layout.Size * 0.018d);
        var waveLift = Number(layout.Size * 0.12d);
        var waveSettle = Number(layout.Size * 0.025d);
        var waveDuration = Number(10d + visibleLetterCount * 0.34d);
        var rootSelector = ids.Root.Replace(".", "\\.", StringComparison.Ordinal);

        builder.AppendLine("  <style>");
        builder.Append("    #").Append(rootSelector).AppendLine(" .gnougnou-text-idle,");
        builder.Append("    #").Append(rootSelector).AppendLine(" .gnougnou-text-wave { transform-box: fill-box; transform-origin: center; }");
        builder.Append("    #").Append(rootSelector).Append(" .gnougnou-text-idle { animation: ")
            .Append(ids.IdleKeyframes).AppendLine(" 4.8s ease-in-out infinite; }");
        builder.Append("    #").Append(rootSelector).Append(" .gnougnou-text-wave { animation: ")
            .Append(ids.WaveKeyframes).Append(' ').Append(waveDuration)
            .AppendLine("s cubic-bezier(.4,0,.2,1) var(--wave-delay) infinite both; }");
        builder.Append("    @keyframes ").Append(ids.IdleKeyframes)
            .Append(" { 0%,100% { transform: translateY(0) rotate(-.35deg); } 50% { transform: translateY(-")
            .Append(idleLift).AppendLine("px) rotate(.35deg); } }");
        builder.Append("    @keyframes ").Append(ids.WaveKeyframes)
            .Append(" { 0%,3.2%,100% { transform: translateY(0) rotate(0) scale(1); } 1% { transform: translateY(-")
            .Append(waveLift).Append("px) rotate(-2.2deg) scale(1.06); } 2.2% { transform: translateY(")
            .Append(waveSettle).AppendLine("px) rotate(1deg) scale(.99); } }");
        builder.AppendLine("    @media (prefers-reduced-motion: reduce) {");
        builder.Append("      #").Append(rootSelector).AppendLine(" .gnougnou-text-idle,");
        builder.Append("      #").Append(rootSelector).AppendLine(" .gnougnou-text-wave { animation: none !important; }");
        builder.AppendLine("    }");
        builder.AppendLine("  </style>");
    }

    private static void AppendWaveAnimationStyle(
        StringBuilder builder,
        TextLayout layout,
        SvgIds ids)
    {
        var lift = Number(layout.Size * 0.085d);
        var dip = Number(layout.Size * 0.035d);
        var rootSelector = CssSelector(ids.Root);

        builder.AppendLine("  <style>");
        builder.Append("    #").Append(rootSelector)
            .AppendLine(" .gnougnou-text-wave-flow { transform-box: fill-box; transform-origin: center; }");
        builder.Append("    #").Append(rootSelector).Append(" .gnougnou-text-wave-flow { animation: ")
            .Append(ids.FlowKeyframes)
            .AppendLine(" 3.2s ease-in-out var(--wave-flow-delay) infinite both; }");
        builder.Append("    @keyframes ").Append(ids.FlowKeyframes)
            .Append(" { 0%,50%,100% { transform: translateY(0) rotate(0); } 25% { transform: translateY(-")
            .Append(lift).Append("px) rotate(-1.4deg); } 75% { transform: translateY(")
            .Append(dip).AppendLine("px) rotate(1deg); } }");
        AppendReducedMotionRule(builder, rootSelector, "gnougnou-text-wave-flow");
        builder.AppendLine("  </style>");
    }

    private static void AppendBounceAnimationStyle(
        StringBuilder builder,
        int visibleLetterCount,
        TextLayout layout,
        SvgIds ids)
    {
        var lift = Number(layout.Size * 0.15d);
        var settle = Number(layout.Size * 0.035d);
        var duration = Number(7d + visibleLetterCount * 0.34d);
        var rootSelector = CssSelector(ids.Root);

        builder.AppendLine("  <style>");
        builder.Append("    #").Append(rootSelector)
            .AppendLine(" .gnougnou-text-bounce { transform-box: fill-box; transform-origin: center bottom; }");
        builder.Append("    #").Append(rootSelector).Append(" .gnougnou-text-bounce { animation: ")
            .Append(ids.BounceKeyframes).Append(' ').Append(duration)
            .AppendLine("s cubic-bezier(.34,1.56,.64,1) var(--bounce-delay) infinite both; }");
        builder.Append("    @keyframes ").Append(ids.BounceKeyframes)
            .Append(" { 0%,5.5%,100% { transform: translateY(0) scale(1); } 1.4% { transform: translateY(-")
            .Append(lift).Append("px) rotate(-1.8deg) scale(1.06,.94); } 3.4% { transform: translateY(")
            .Append(settle).AppendLine("px) rotate(.8deg) scale(.97,1.06); } }");
        AppendReducedMotionRule(builder, rootSelector, "gnougnou-text-bounce");
        builder.AppendLine("  </style>");
    }

    private static void AppendReducedMotionRule(
        StringBuilder builder,
        string rootSelector,
        string className)
    {
        builder.AppendLine("    @media (prefers-reduced-motion: reduce) {");
        builder.Append("      #").Append(rootSelector).Append(" .").Append(className)
            .AppendLine(" { animation: none !important; }");
        builder.AppendLine("    }");
    }

    private static void AppendLetters(
        StringBuilder builder,
        IReadOnlyList<string> textElements,
        TextLayout layout,
        string gradientId,
        GnouGnouTextAnimation animation)
    {
        var x = layout.TextX;
        var visibleIndex = 0;

        foreach (var element in textElements)
        {
            var advance = MeasureTextElement(element, layout.Size);
            if (!IsWhiteSpace(element))
            {
                if (animation == GnouGnouTextAnimation.Idle)
                {
                    var idleDelay = Number(-(visibleIndex % 11) * 0.31d);
                    var waveDelay = Number(4.5d + visibleIndex * 0.34d);
                    builder.Append("    <g class=\"gnougnou-text-idle\" style=\"animation-delay:")
                        .Append(idleDelay).AppendLine("s\">");
                    builder.Append("      <g class=\"gnougnou-text-wave\" style=\"--wave-delay:")
                        .Append(waveDelay).AppendLine("s\">");
                    AppendTextElement(builder, element, visibleIndex, x, advance, layout, gradientId, 8);
                    builder.AppendLine("      </g>");
                    builder.AppendLine("    </g>");
                }
                else if (animation == GnouGnouTextAnimation.Wave)
                {
                    var waveDelay = Number(visibleIndex * 0.13d);
                    builder.Append("    <g class=\"gnougnou-text-wave-flow\" style=\"--wave-flow-delay:")
                        .Append(waveDelay).AppendLine("s\">");
                    AppendTextElement(builder, element, visibleIndex, x, advance, layout, gradientId, 6);
                    builder.AppendLine("    </g>");
                }
                else if (animation == GnouGnouTextAnimation.Bounce)
                {
                    var bounceDelay = Number(1.8d + visibleIndex * 0.34d);
                    builder.Append("    <g class=\"gnougnou-text-bounce\" style=\"--bounce-delay:")
                        .Append(bounceDelay).AppendLine("s\">");
                    AppendTextElement(builder, element, visibleIndex, x, advance, layout, gradientId, 6);
                    builder.AppendLine("    </g>");
                }
                else
                {
                    AppendTextElement(builder, element, visibleIndex, x, advance, layout, gradientId, 4);
                }

                visibleIndex++;
            }

            x += advance + layout.LetterSpacing;
        }
    }

    private static void AppendTextElement(
        StringBuilder builder,
        string element,
        int visibleIndex,
        double x,
        double advance,
        TextLayout layout,
        string gradientId,
        int indentation)
    {
        builder.Append(' ', indentation).Append("<text data-letter-index=\"").Append(visibleIndex)
            .Append("\" x=\"").Append(Number(x)).Append("\" y=\"").Append(Number(layout.Baseline)).Append("\" textLength=\"")
            .Append(Number(advance)).Append("\" lengthAdjust=\"spacingAndGlyphs\" font-family=\"ui-rounded, &quot;Arial Rounded MT Bold&quot;, &quot;Nunito&quot;, &quot;Trebuchet MS&quot;, sans-serif\" font-size=\"")
            .Append(layout.Size).Append("\" font-weight=\"800\" fill=\"url(#")
            .Append(gradientId).Append(")\">").Append(SvgText.Escape(element)).AppendLine("</text>");
    }

    private static void AppendStars(
        StringBuilder builder,
        GnouGnouTextOptions options,
        TextLayout layout,
        string starColor)
    {
        if (options.StarCount == 0)
            return;

        builder.Append("    <g data-part=\"stars\" fill=\"").Append(starColor).AppendLine("\">");
        foreach (var star in layout.Stars)
        {
            builder.Append("      <path data-part=\"star\" d=\"")
                .Append(CreateStarPath(star.X, star.Y, star.Radius)).AppendLine("\"/>");
        }

        builder.AppendLine("    </g>");
    }

    private static string CreateStarPath(double x, double y, double radius)
    {
        var inner = radius * 0.18d;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"M{Number(x)} {Number(y - radius)} C{Number(x)} {Number(y - inner)} {Number(x + inner)} {Number(y)} {Number(x + radius)} {Number(y)} C{Number(x + inner)} {Number(y)} {Number(x)} {Number(y + inner)} {Number(x)} {Number(y + radius)} C{Number(x)} {Number(y + inner)} {Number(x - inner)} {Number(y)} {Number(x - radius)} {Number(y)} C{Number(x - inner)} {Number(y)} {Number(x)} {Number(y - inner)} {Number(x)} {Number(y - radius)}Z");
    }

    private static TextLayout CreateLayout(GnouGnouTextOptions options, IReadOnlyList<string> textElements)
    {
        var size = options.Size;
        var automaticMargin = size * GetAutomaticMarginFactor(options.Animation);
        var horizontalMargin = options.HorizontalMargin ?? automaticMargin;
        var verticalMargin = options.VerticalMargin ?? automaticMargin;
        var letterSpacing = size * 0.015d;
        var textWidth = textElements.Sum(element => MeasureTextElement(element, size))
            + Math.Max(0, textElements.Count - 1) * letterSpacing;
        var baseline = verticalMargin + size * 0.82d;
        var stars = CreateStars(options, horizontalMargin + textWidth, verticalMargin);
        var contentBottom = Math.Max(
            baseline + size * 0.22d,
            stars.Count == 0 ? 0d : stars.Max(static star => star.Y + star.Radius));
        var rightEdge = stars.Count == 0
            ? horizontalMargin + textWidth
            : stars.Max(static star => star.X + star.Radius);
        var canvasWidth = (int)Math.Ceiling(rightEdge + horizontalMargin);
        var canvasHeight = (int)Math.Ceiling(contentBottom + verticalMargin);

        return new TextLayout(
            size,
            canvasWidth,
            canvasHeight,
            horizontalMargin,
            baseline,
            textWidth,
            letterSpacing,
            stars);
    }

    private static IReadOnlyList<StarLayout> CreateStars(
        GnouGnouTextOptions options,
        double textRight,
        double top)
    {
        if (options.StarCount == 0)
            return [];

        var stars = new StarLayout[options.StarCount];
        var cursor = textRight + options.Size * 0.14d;
        for (var index = 0; index < stars.Length; index++)
        {
            var radiusFactor = index % 2 == 0 ? 0.15d : 0.085d;
            var radius = options.Size * radiusFactor * options.StarScale;
            var gap = index == 0 ? 0d : options.Size * 0.055d;
            var x = cursor + gap + radius;
            var y = top + radius + options.Size * (index % 2 == 0 ? 0d : 0.16d);
            stars[index] = new StarLayout(x, y, radius);
            cursor = x + radius;
        }

        return stars;
    }

    private static double MeasureTextElement(string textElement, int size)
    {
        if (IsWhiteSpace(textElement))
            return size * 0.34d;

        var rune = textElement.EnumerateRunes().First();
        var value = rune.Value;
        var factor = value switch
        {
            'W' or 'M' => 0.9d,
            'I' => 0.34d,
            'J' => 0.48d,
            >= 'A' and <= 'Z' => 0.72d,
            'm' or 'w' => 0.82d,
            'i' or 'l' => 0.3d,
            'j' or 't' or 'f' or 'r' => 0.43d,
            'c' or 'e' or 's' or 'z' => 0.55d,
            >= 'a' and <= 'z' => 0.63d,
            >= '0' and <= '9' => 0.62d,
            '.' or ',' or ':' or ';' or '!' or '|' or '\'' => 0.28d,
            '-' or '_' or '+' or '=' or '/' or '\\' => 0.48d,
            '(' or ')' or '[' or ']' or '{' or '}' => 0.38d,
            _ when Rune.IsLetterOrDigit(rune) => 0.68d,
            _ when Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherSymbol => 1d,
            _ => 0.56d
        };

        return size * factor;
    }

    private static IReadOnlyList<string> GetTextElements(string text)
    {
        var elements = new List<string>(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            elements.Add(enumerator.GetTextElement());
        return elements;
    }

    private static bool IsWhiteSpace(string textElement) =>
        textElement.EnumerateRunes().All(Rune.IsWhiteSpace);

    private static bool IsHexColor(string? color)
    {
        if (color is null || color.Length is not (4 or 5 or 7 or 9) || color[0] != '#')
            return false;

        return color.AsSpan(1).ToString().All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
    }

    private static void ValidateMargin(double? margin, string propertyName)
    {
        if (margin is null)
            return;

        if (!double.IsFinite(margin.Value) || margin.Value is < 0d or > MaxMargin)
            throw new ArgumentOutOfRangeException(
                propertyName,
                margin,
                $"{propertyName} must be between zero and 4096 when specified.");
    }

    private static void ValidateSvgIdPrefix(string? prefix)
    {
        if (prefix is null)
            return;

        if (prefix.Length is 0 or > 64 || !IsPrefixStart(prefix[0]) || prefix.Any(static character => !IsPrefixCharacter(character)))
            throw new ArgumentException(
                "SvgIdPrefix must start with a letter or underscore and contain only letters, digits, underscores, dots, or hyphens.",
                nameof(GnouGnouTextOptions.SvgIdPrefix));
    }

    private static bool IsPrefixStart(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsPrefixCharacter(char character) =>
        IsPrefixStart(character) || character is >= '0' and <= '9' or '.' or '-';

    private static SvgIds CreateIds(string? prefix)
    {
        var idPrefix = prefix ?? "gnougnou-text";
        var cssPrefix = idPrefix.Replace('.', '-');
        return new SvgIds(
            $"{idPrefix}-root",
            $"{idPrefix}-title",
            $"{idPrefix}-desc",
            $"{idPrefix}-gradient",
            $"{cssPrefix}-idle",
            $"{cssPrefix}-wave",
            $"{cssPrefix}-wave-flow",
            $"{cssPrefix}-bounce");
    }

    private static double GetAutomaticMarginFactor(GnouGnouTextAnimation animation) => animation switch
    {
        GnouGnouTextAnimation.None => 0.14d,
        GnouGnouTextAnimation.Idle => 0.22d,
        GnouGnouTextAnimation.Wave => 0.2d,
        GnouGnouTextAnimation.Bounce => 0.24d,
        _ => throw new ArgumentOutOfRangeException(nameof(animation), animation, "Unsupported GnOuGo text animation.")
    };

    private static string CssSelector(string id) =>
        id.Replace(".", "\\.", StringComparison.Ordinal);

    private static string ToAnimationToken(GnouGnouTextAnimation animation) => animation switch
    {
        GnouGnouTextAnimation.None => "none",
        GnouGnouTextAnimation.Idle => "idle",
        GnouGnouTextAnimation.Wave => "wave",
        GnouGnouTextAnimation.Bounce => "bounce",
        _ => throw new ArgumentOutOfRangeException(nameof(animation), animation, "Unsupported GnOuGo text animation.")
    };

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record TextLayout(
        int Size,
        int CanvasWidth,
        int CanvasHeight,
        double TextX,
        double Baseline,
        double TextWidth,
        double LetterSpacing,
        IReadOnlyList<StarLayout> Stars);

    private sealed record StarLayout(double X, double Y, double Radius);

    private sealed record SvgIds(
        string Root,
        string Title,
        string Description,
        string Gradient,
        string IdleKeyframes,
        string WaveKeyframes,
        string FlowKeyframes,
        string BounceKeyframes);
}
