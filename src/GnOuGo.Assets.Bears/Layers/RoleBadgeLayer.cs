namespace GnOuGo.Assets.Bears.Layers;

internal static class RoleBadgeLayer
{
    public static string Render(GnouGnouBearRole role, ref StableRandom stableRandom)
    {
        var dx = stableRandom.NextInclusive(-1, 1);
        var dy = stableRandom.NextInclusive(-1, 1);
        var letter = role switch
        {
            GnouGnouBearRole.Planner => "P",
            GnouGnouBearRole.Reviewer => "R",
            GnouGnouBearRole.Coder => "C",
            GnouGnouBearRole.Observer => "O",
            _ => "G"
        };

        return $"""
    <g aria-label="role-{role}" transform="translate({dx} {dy})">
      <circle cx="216" cy="222" r="18" fill="#2E6ED1" stroke="#FFFFFF" stroke-width="4"/>
      <text x="216" y="229" text-anchor="middle" font-family="Arial, sans-serif" font-size="20" font-weight="700" fill="#FFFFFF">{letter}</text>
    </g>
""";
    }
}
