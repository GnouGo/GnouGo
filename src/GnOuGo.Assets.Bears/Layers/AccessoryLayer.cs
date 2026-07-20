using System.Text;

namespace GnOuGo.Assets.Bears.Layers;

internal static class AccessoryLayer
{
    public static string Render(GnouGnouBearAccessory accessory, ref StableRandom stableRandom)
        => Render([accessory], ref stableRandom, AccessoryPalette.FromVariant(0));

    public static string Render(IReadOnlyList<GnouGnouBearAccessory> accessories, ref StableRandom stableRandom, AccessoryPalette palette)
    {
        var builder = new StringBuilder();
        foreach (var accessory in accessories)
        {
            builder.Append(RenderOne(accessory, ref stableRandom, palette));
        }

        return builder.ToString();
    }

    private static string RenderOne(GnouGnouBearAccessory accessory, ref StableRandom stableRandom, AccessoryPalette palette)
    {
        var offset = stableRandom.NextInclusive(-1, 1);

        return accessory switch
        {
            GnouGnouBearAccessory.Headphones or GnouGnouBearAccessory.BowTie or GnouGnouBearAccessory.None => string.Empty,
            GnouGnouBearAccessory.Necktie => $"""
    <path d="M121 166 H135 L141 215 L128 229 L115 215 Z" fill="{palette.Accent}" stroke="{palette.Dark}" stroke-width="3"/>
    <path d="M121 166 L128 176 L135 166" fill="{palette.Tint}" stroke="{palette.Dark}" stroke-width="2.4"/>
    <path d="M128 180 L128 218" stroke="{palette.Light}" stroke-width="2" opacity="0.55"/>
""",
            GnouGnouBearAccessory.Glasses => $"""
    <circle cx="104" cy="106" r="17" fill="none" stroke="{palette.Deep}" stroke-width="3.4" opacity="0.86"/>
    <circle cx="152" cy="106" r="17" fill="none" stroke="{palette.Deep}" stroke-width="3.4" opacity="0.86"/>
    <path d="M121 106 C124 103 132 103 135 106" fill="none" stroke="{palette.Deep}" stroke-width="3"/>
""",
            GnouGnouBearAccessory.Laptop => $"""
    <g transform="translate({offset} 0)">
      <rect x="83" y="184" width="90" height="38" rx="6" fill="{palette.Surface}" stroke="{palette.Dark}" stroke-width="3"/>
      <path d="M83 215 H173 L183 230 H73 Z" fill="{palette.Tint}" stroke="{palette.Dark}" stroke-width="3"/>
      <circle cx="128" cy="202" r="5" fill="{palette.Accent}" opacity="0.95"/>
      <path d="M110 224 H146" stroke="{palette.Deep}" stroke-width="2.2" opacity="0.55"/>
    </g>
""",
            GnouGnouBearAccessory.Shield => $"""
    <g transform="translate({offset} 0)">
      <path d="M184 170 L207 178 L203 207 C199 220 190 229 184 232 C178 229 169 220 165 207 L161 178 Z" fill="{palette.SurfaceTint}" stroke="{palette.Dark}" stroke-width="3"/>
      <path d="M174 200 L181 207 L196 188" fill="none" stroke="{palette.Success}" stroke-width="4"/>
    </g>
""",
            GnouGnouBearAccessory.Notebook => $"""
    <g transform="translate({offset} 0)">
      <rect x="70" y="174" width="36" height="48" rx="5" fill="#FFF9E8" stroke="{palette.WarmDark}" stroke-width="3"/>
      <path d="M80 186 H96 M80 198 H96 M80 210 H93" stroke="{palette.Accent}" stroke-width="2.4"/>
      <path d="M72 183 H68 M72 197 H68 M72 211 H68" stroke="{palette.WarmDark}" stroke-width="2.2"/>
    </g>
""",
            GnouGnouBearAccessory.Pencil => $"""
    <g transform="translate({offset} 0) rotate(-25 183 188)">
      <rect x="171" y="168" width="13" height="52" rx="4" fill="{palette.Warm}" stroke="{palette.WarmDark}" stroke-width="2.6"/>
      <path d="M171 168 L177.5 157 L184 168 Z" fill="#FFEBD1" stroke="{palette.WarmDark}" stroke-width="2.4"/>
      <path d="M175 159 L177.5 157 L180 159" stroke="#552318" stroke-width="2"/>
      <rect x="171" y="213" width="13" height="8" fill="#F79AA0" stroke="{palette.WarmDark}" stroke-width="2.2"/>
    </g>
""",
            GnouGnouBearAccessory.Magnifier => $"""
    <g transform="translate({offset} 0)">
      <circle cx="185" cy="179" r="17" fill="{palette.SurfaceTint}" opacity="0.7" stroke="{palette.Dark}" stroke-width="4"/>
      <path d="M197 192 L215 211" stroke="{palette.Dark}" stroke-width="6"/>
      <path d="M178 171 C183 168 190 169 193 174" fill="none" stroke="#FFFFFF" stroke-width="2.4" opacity="0.8"/>
    </g>
""",
            GnouGnouBearAccessory.MagicWand => $"""
    <g transform="translate({offset} 0)">
      <path d="M174 198 L211 161" stroke="{palette.Deep}" stroke-width="5"/>
      <path d="M211 151 L215 160 L225 161 L217 167 L220 176 L211 171 L202 176 L205 166 L197 161 L207 160 Z" fill="{palette.Warm}" stroke="{palette.WarmDark}" stroke-width="2.2"/>
      <circle cx="195" cy="151" r="2.5" fill="#FFFFFF"/>
      <circle cx="222" cy="188" r="2.8" fill="{palette.Light}"/>
    </g>
""",
            GnouGnouBearAccessory.Stars => $"""
    <g transform="translate({offset} 0)">
      <path d="M47 53 L51 62 L61 63 L53 69 L56 79 L47 74 L38 79 L41 69 L33 63 L43 62 Z" fill="{palette.Warm}" stroke="{palette.WarmDark}" stroke-width="2"/>
      <path d="M210 43 L213 50 L221 51 L215 56 L217 64 L210 60 L203 64 L205 56 L199 51 L207 50 Z" fill="{palette.Light}" stroke="{palette.Dark}" stroke-width="2"/>
      <circle cx="218" cy="77" r="3.2" fill="#FFFFFF" opacity="0.9"/>
    </g>
""",
            GnouGnouBearAccessory.Umbrella => $"""
    <g transform="translate({offset} 0)">
      <path d="M56 160 C72 125 111 117 140 142 C106 137 79 144 56 160Z" fill="{palette.Light}" stroke="{palette.Dark}" stroke-width="3"/>
      <path d="M56 160 C79 144 106 137 140 142 C119 154 90 160 56 160Z" fill="{palette.Accent}" opacity="0.35"/>
      <path d="M98 138 L82 209" stroke="{palette.WarmDark}" stroke-width="4"/>
      <path d="M82 209 C77 224 61 219 66 207" fill="none" stroke="{palette.WarmDark}" stroke-width="4"/>
      <path d="M74 149 C80 143 87 140 95 139 M104 137 C112 137 121 139 130 142" fill="none" stroke="#FFFFFF" stroke-width="2" opacity="0.62"/>
    </g>
""",
            GnouGnouBearAccessory.SoccerBall => $"""
    <g transform="translate({offset} 0)">
      <circle cx="190" cy="211" r="18" fill="#FFFFFF" stroke="{palette.Deep}" stroke-width="3"/>
      <path d="M190 199 L199 206 L196 217 H184 L181 206 Z" fill="{palette.Deep}"/>
      <path d="M181 206 L172 203 M199 206 L207 202 M196 217 L201 225 M184 217 L178 225 M190 199 L190 191" stroke="{palette.Deep}" stroke-width="2.1"/>
    </g>
""",
            GnouGnouBearAccessory.TennisRacket => $"""
    <g transform="translate({offset} 0) rotate(-18 187 190)">
      <ellipse cx="188" cy="172" rx="17" ry="25" fill="{palette.SurfaceTint}" opacity="0.72" stroke="{palette.Dark}" stroke-width="3.2"/>
      <path d="M177 165 H199 M176 174 H200 M179 183 H197 M188 149 V195 M181 151 L195 192 M195 151 L181 192" stroke="{palette.Dark}" stroke-width="1.4" opacity="0.65"/>
      <path d="M188 196 L188 229" stroke="{palette.WarmDark}" stroke-width="6"/>
      <circle cx="159" cy="208" r="6" fill="{palette.Warm}" stroke="{palette.WarmDark}" stroke-width="2"/>
    </g>
""",
            GnouGnouBearAccessory.Crown => $"""
    <g transform="translate({offset} 0)">
      <path d="M96 54 L111 34 L128 54 L145 34 L160 54 L155 72 H101 Z" fill="{palette.Warm}" stroke="{palette.WarmDark}" stroke-width="3"/>
      <circle cx="111" cy="35" r="4" fill="{palette.Light}"/>
      <circle cx="145" cy="35" r="4" fill="{palette.Light}"/>
      <path d="M104 63 H152" stroke="#FFF3B0" stroke-width="2" opacity="0.7"/>
    </g>
""",
            GnouGnouBearAccessory.CoffeeMug => $"""
    <g transform="translate({offset} 0)">
      <rect x="171" y="187" width="31" height="29" rx="7" fill="{palette.Surface}" stroke="{palette.WarmDark}" stroke-width="3"/>
      <path d="M202 195 C216 194 216 210 202 209" fill="none" stroke="{palette.WarmDark}" stroke-width="3"/>
      <path d="M180 181 C176 174 184 171 180 164 M193 181 C189 174 197 171 193 164" stroke="{palette.Deep}" stroke-width="2" opacity="0.45"/>
      <path d="M177 197 H196" stroke="{palette.Tint}" stroke-width="2"/>
    </g>
""",
            GnouGnouBearAccessory.Compass => $"""
    <g transform="translate({offset} 0)">
      <circle cx="73" cy="184" r="20" fill="{palette.SurfaceTint}" stroke="{palette.Dark}" stroke-width="3"/>
      <path d="M73 168 L80 184 L73 200 L66 184 Z" fill="{palette.Accent}" stroke="{palette.Deep}" stroke-width="2"/>
      <circle cx="73" cy="184" r="4" fill="{palette.Warm}"/>
      <path d="M73 158 V151 M73 217 V210 M50 184 H43 M103 184 H96" stroke="{palette.Dark}" stroke-width="2"/>
    </g>
""",
            GnouGnouBearAccessory.Rocket => $"""
    <g transform="translate({offset} 0) rotate(28 188 182)">
      <path d="M188 145 C205 158 207 184 188 207 C169 184 171 158 188 145Z" fill="{palette.Surface}" stroke="{palette.Dark}" stroke-width="3"/>
      <circle cx="188" cy="171" r="7" fill="{palette.Light}" stroke="{palette.Dark}" stroke-width="2"/>
      <path d="M176 196 L165 211 L181 204 Z M200 196 L211 211 L195 204 Z" fill="{palette.Accent}" stroke="{palette.Dark}" stroke-width="2"/>
      <path d="M182 208 C184 221 192 221 194 208" fill="{palette.Warm}" stroke="{palette.WarmDark}" stroke-width="2"/>
    </g>
""",
            _ => string.Empty
        };
    }
}
