namespace GnOuGo.Assets.Bears.Layers;

internal static class AccessoryLayer
{
    public static string Render(GnouGnouBearAccessory accessory, ref StableRandom stableRandom)
    {
        var offset = stableRandom.NextInclusive(-1, 1);

        return accessory switch
        {
            GnouGnouBearAccessory.Glasses => """
    <circle cx="104" cy="106" r="17" fill="none" stroke="#244B8F" stroke-width="3.4" opacity="0.86"/>
    <circle cx="152" cy="106" r="17" fill="none" stroke="#244B8F" stroke-width="3.4" opacity="0.86"/>
    <path d="M121 106 C124 103 132 103 135 106" fill="none" stroke="#244B8F" stroke-width="3"/>
""",
            GnouGnouBearAccessory.Laptop => $"""
    <g transform="translate({offset} 0)">
      <rect x="83" y="184" width="90" height="38" rx="6" fill="#DDF4FF" stroke="#245AA6" stroke-width="3"/>
      <path d="M83 215 H173 L183 230 H73 Z" fill="#83D8F2" stroke="#245AA6" stroke-width="3"/>
      <circle cx="128" cy="202" r="5" fill="#38CAD4" opacity="0.95"/>
      <path d="M110 224 H146" stroke="#245AA6" stroke-width="2.2" opacity="0.55"/>
    </g>
""",
            GnouGnouBearAccessory.Shield => $"""
    <g transform="translate({offset} 0)">
      <path d="M184 170 L207 178 L203 207 C199 220 190 229 184 232 C178 229 169 220 165 207 L161 178 Z" fill="#DBF7FF" stroke="#245AA6" stroke-width="3"/>
      <path d="M174 200 L181 207 L196 188" fill="none" stroke="#16A34A" stroke-width="4"/>
    </g>
""",
            GnouGnouBearAccessory.Notebook => $"""
    <g transform="translate({offset} 0)">
      <rect x="70" y="174" width="36" height="48" rx="5" fill="#FFF9E8" stroke="#8A5B2F" stroke-width="3"/>
      <path d="M80 186 H96 M80 198 H96 M80 210 H93" stroke="#44A9C8" stroke-width="2.4"/>
      <path d="M72 183 H68 M72 197 H68 M72 211 H68" stroke="#8A5B2F" stroke-width="2.2"/>
    </g>
""",
            GnouGnouBearAccessory.Pencil => $"""
    <g transform="translate({offset} 0) rotate(-25 183 188)">
      <rect x="171" y="168" width="13" height="52" rx="4" fill="#FFD66B" stroke="#9A5B22" stroke-width="2.6"/>
      <path d="M171 168 L177.5 157 L184 168 Z" fill="#FFEBD1" stroke="#9A5B22" stroke-width="2.4"/>
      <path d="M175 159 L177.5 157 L180 159" stroke="#552318" stroke-width="2"/>
      <rect x="171" y="213" width="13" height="8" fill="#F79AA0" stroke="#9A5B22" stroke-width="2.2"/>
    </g>
""",
            GnouGnouBearAccessory.Magnifier => $"""
    <g transform="translate({offset} 0)">
      <circle cx="185" cy="179" r="17" fill="#E9FBFF" opacity="0.7" stroke="#245AA6" stroke-width="4"/>
      <path d="M197 192 L215 211" stroke="#245AA6" stroke-width="6"/>
      <path d="M178 171 C183 168 190 169 193 174" fill="none" stroke="#FFFFFF" stroke-width="2.4" opacity="0.8"/>
    </g>
""",
            GnouGnouBearAccessory.MagicWand => $"""
    <g transform="translate({offset} 0)">
      <path d="M174 198 L211 161" stroke="#6B3FA0" stroke-width="5"/>
      <path d="M211 151 L215 160 L225 161 L217 167 L220 176 L211 171 L202 176 L205 166 L197 161 L207 160 Z" fill="#FFE66B" stroke="#9B6B00" stroke-width="2.2"/>
      <circle cx="195" cy="151" r="2.5" fill="#FFFFFF"/>
      <circle cx="222" cy="188" r="2.8" fill="#8CF7F1"/>
    </g>
""",
            GnouGnouBearAccessory.Stars => $"""
    <g transform="translate({offset} 0)">
      <path d="M47 53 L51 62 L61 63 L53 69 L56 79 L47 74 L38 79 L41 69 L33 63 L43 62 Z" fill="#FFE66B" stroke="#B97900" stroke-width="2"/>
      <path d="M210 43 L213 50 L221 51 L215 56 L217 64 L210 60 L203 64 L205 56 L199 51 L207 50 Z" fill="#8CF7F1" stroke="#118795" stroke-width="2"/>
      <circle cx="218" cy="77" r="3.2" fill="#FFFFFF" opacity="0.9"/>
    </g>
""",
            GnouGnouBearAccessory.Umbrella => $"""
    <g transform="translate({offset} 0)">
      <path d="M56 160 C72 125 111 117 140 142 C106 137 79 144 56 160Z" fill="#78DCFF" stroke="#245AA6" stroke-width="3"/>
      <path d="M56 160 C79 144 106 137 140 142 C119 154 90 160 56 160Z" fill="#349CF0" opacity="0.35"/>
      <path d="M98 138 L82 209" stroke="#8A5B2F" stroke-width="4"/>
      <path d="M82 209 C77 224 61 219 66 207" fill="none" stroke="#8A5B2F" stroke-width="4"/>
      <path d="M74 149 C80 143 87 140 95 139 M104 137 C112 137 121 139 130 142" fill="none" stroke="#FFFFFF" stroke-width="2" opacity="0.62"/>
    </g>
""",
            _ => ""
        };
    }
}
