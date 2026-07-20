namespace GnOuGo.Assets.Bears.Layers;

internal sealed record AccessoryPalette(
    string Light,
    string Tint,
    string Accent,
    string Dark,
    string Deep,
    string Warm,
    string WarmDark,
    string Surface,
    string SurfaceTint,
    string Success)
{
    public static AccessoryPalette FromVariant(int variant)
    {
        return (Math.Abs(variant) % 6) switch
        {
            1 => new("#D7FBFF", "#9CEAF6", "#33BAD0", "#238BA6", "#1F537A", "#FFD978", "#9A6524", "#F2FBFF", "#DDF5FF", "#18A058"),
            2 => new("#E6EEFF", "#B8CCFF", "#527FE8", "#315CB9", "#263E85", "#FFE08A", "#956A24", "#F7FAFF", "#E4ECFF", "#268B5F"),
            3 => new("#E1FFF1", "#A8EBCF", "#38B987", "#238562", "#1D5F4B", "#FFCF83", "#8F5C29", "#F5FFF9", "#DFF7EA", "#16885B"),
            4 => new("#F1E7FF", "#CFB8F4", "#8B66DC", "#6646AE", "#463075", "#FFD38A", "#94602C", "#FBF7FF", "#EFE7FF", "#1E9B6A"),
            5 => new("#E2F6FF", "#AFE0FA", "#3A9CE7", "#276CB5", "#234A83", "#FFC06F", "#8C4E24", "#F6FCFF", "#DDEFFF", "#1B8F69"),
            _ => new("#C4F6FF", "#78DCFF", "#349CF0", "#245AA6", "#243B86", "#FFD66B", "#9A5B22", "#DDF4FF", "#E9FBFF", "#16A34A")
        };
    }
}
