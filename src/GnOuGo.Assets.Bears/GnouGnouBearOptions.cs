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
    public int Size { get; init; } = 256;
    public string? Title { get; init; }
    public string? Description { get; init; }
}
