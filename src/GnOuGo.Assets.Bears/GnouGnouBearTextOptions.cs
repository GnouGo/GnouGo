namespace GnOuGo.Assets.Bears;

/// <summary>
/// Options for a horizontal GnOuGo lockup composed from an independently
/// generated bear and text SVG.
/// </summary>
public sealed record GnouGnouBearTextOptions
{
    /// <summary>
    /// Complete mascot options. The composite generator namespaces its SVG IDs
    /// while preserving every other option, including animation.
    /// </summary>
    public GnouGnouBearOptions BearOptions { get; init; } = new()
    {
        Theme = GnouGnouBearTheme.Transparent
    };

    /// <summary>
    /// Complete rounded text options. The composite generator namespaces its
    /// SVG IDs while preserving every other option, including animation.
    /// </summary>
    public GnouGnouTextOptions TextOptions { get; init; } = new();

    /// <summary>
    /// Horizontal distance between the bear canvas and text canvas, in SVG user
    /// units.
    /// </summary>
    public double Gap { get; init; } = 24d;

    /// <summary>
    /// Optional XML ID prefix used by the composite and both nested SVGs.
    /// </summary>
    public string? SvgIdPrefix { get; init; }

    /// <summary>
    /// Optional accessible SVG title. Defaults to the text followed by
    /// "with GnouGnou".
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Optional accessible SVG description.
    /// </summary>
    public string? Description { get; init; }
}
