namespace GnOuGo.Assets.Bears;

public sealed record GnouGnouBearOptions
{
    public int Seed { get; init; } = 1;
    public GnouGnouBearRole Role { get; init; } = GnouGnouBearRole.Default;
    public GnouGnouBearEmotion Emotion { get; init; } = GnouGnouBearEmotion.Happy;
    public GnouGnouBearAccessory Accessory { get; init; } = GnouGnouBearAccessory.None;
    public IReadOnlyList<GnouGnouBearAccessory> Accessories { get; init; } = [];
    public int AccessoryColorVariant { get; init; }
    public GnouGnouBearState State { get; init; } = GnouGnouBearState.Idle;
    public GnouGnouBearTheme Theme { get; init; } = GnouGnouBearTheme.Default;
    public GnouGnouBearFurPalette FurPalette { get; init; } = GnouGnouBearFurPalette.Classic;
    public GnouGnouBearEyeStyle EyeStyle { get; init; } = GnouGnouBearEyeStyle.Default;
    public bool HasHeadphones { get; init; } = true;
    public bool HasBowTie { get; init; } = true;
    public bool HasBeard { get; init; }
    /// <summary>
    /// Renders the mascot as semantic, independently transformable body-part groups.
    /// This is intended for host-driven SVG animation and is disabled by default so
    /// existing static SVG output remains unchanged.
    /// </summary>
    public bool EnableAnimationRig { get; init; }
    public int Size { get; init; } = 256;
    /// <summary>
    /// Optional XML ID prefix used when several mascots are embedded in the same SVG document.
    /// The prefix must start with a letter or underscore and contain only letters, digits,
    /// underscores, dots, or hyphens.
    /// </summary>
    public string? SvgIdPrefix { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
}
