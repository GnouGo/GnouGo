namespace GnOuGo.Assets.Bears;

/// <summary>
/// Options for rounded gradient text inspired by the GnOuGo wordmark.
/// </summary>
public sealed record GnouGnouTextOptions
{
    /// <summary>
    /// Text rendered on one line. Up to 128 Unicode text elements are supported.
    /// </summary>
    public string Text { get; init; } = "GnouGo";

    /// <summary>
    /// Nominal text height in SVG user units. The canvas width and surrounding
    /// animation clearance are calculated automatically.
    /// </summary>
    public int Size { get; init; } = 128;

    /// <summary>
    /// Two to eight hexadecimal colors distributed evenly from left to right.
    /// </summary>
    public IReadOnlyList<string> GradientColors { get; init; } =
    [
        "#3F4A9E",
        "#348CD1",
        "#2EC7D3"
    ];

    /// <summary>
    /// Number of four-point decorative stars after the text. Set to zero to
    /// remove them.
    /// </summary>
    public int StarCount { get; init; } = 2;

    /// <summary>
    /// Optional hexadecimal star color. When omitted, the last gradient color
    /// is used.
    /// </summary>
    public string? StarColor { get; init; }

    /// <summary>
    /// Multiplier applied to the decorative star sizes.
    /// </summary>
    public double StarScale { get; init; } = 1d;

    /// <summary>
    /// Optional self-playing, script-free animation.
    /// </summary>
    public GnouGnouTextAnimation Animation { get; init; } = GnouGnouTextAnimation.None;

    /// <summary>
    /// Optional XML ID prefix for safely embedding several generated wordmarks
    /// in the same document.
    /// </summary>
    public string? SvgIdPrefix { get; init; }

    /// <summary>
    /// Optional accessible SVG title. Defaults to the rendered text.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Optional accessible SVG description.
    /// </summary>
    public string? Description { get; init; }
}
