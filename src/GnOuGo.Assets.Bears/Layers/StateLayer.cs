namespace GnOuGo.Assets.Bears.Layers;

internal static class StateLayer
{
    public static string Render(GnouGnouBearState state, ref StableRandom stableRandom)
    {
        var offset = stableRandom.NextInclusive(-1, 1);

        return state switch
        {
            GnouGnouBearState.Running => $"""
    <g transform="translate({offset} 0)">
      <path d="M35 176 C48 168 58 169 69 177" fill="none" stroke="#349CF0" stroke-width="4" opacity="0.86"/>
      <path d="M32 187 C45 179 58 180 72 190" fill="none" stroke="#78DCFF" stroke-width="3.5" opacity="0.72"/>
    </g>
""",
            GnouGnouBearState.Waiting => $"""
    <g transform="translate({offset} 0)">
      <circle cx="40" cy="197" r="15" fill="#FFFFFF" stroke="#2E6ED1" stroke-width="3.2"/>
      <path d="M40 188 V198 L48 203" fill="none" stroke="#2E6ED1" stroke-width="3"/>
    </g>
""",
            GnouGnouBearState.Failed => $"""
    <g transform="translate({offset} 0)">
      <circle cx="39" cy="198" r="14" fill="#FF855E" stroke="#FFFFFF" stroke-width="3"/>
      <path d="M39 190 V199" stroke="#FFFFFF" stroke-width="3.4"/>
      <circle cx="39" cy="205" r="2.2" fill="#FFFFFF"/>
    </g>
""",
            GnouGnouBearState.Success => $"""
    <g transform="translate({offset} 0)">
      <circle cx="39" cy="198" r="14" fill="#22C55E" stroke="#FFFFFF" stroke-width="3"/>
      <path d="M31 198 L37 204 L49 190" fill="none" stroke="#FFFFFF" stroke-width="4"/>
    </g>
""",
            _ => ""
        };
    }
}
