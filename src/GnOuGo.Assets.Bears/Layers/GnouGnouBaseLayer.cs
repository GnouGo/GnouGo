namespace GnOuGo.Assets.Bears.Layers;

internal static class GnouGnouBaseLayer
{
    public static string Defs(GnouGnouBearFurPalette furPalette)
    {
        var palette = FurPaletteValues.From(furPalette);

        return $"""
  <defs>
    <radialGradient id="bg" cx="50%" cy="38%" r="65%">
      <stop offset="0%" stop-color="#FFFFFF"/>
      <stop offset="100%" stop-color="#F7FBFF"/>
    </radialGradient>
    <radialGradient id="fur" cx="42%" cy="30%" r="82%">
      <stop offset="0%" stop-color="{palette.FurTop}"/>
      <stop offset="48%" stop-color="{palette.FurMid}"/>
      <stop offset="100%" stop-color="{palette.FurBottom}"/>
    </radialGradient>
    <radialGradient id="fur-light" cx="36%" cy="24%" r="78%">
      <stop offset="0%" stop-color="{palette.FurLightTop}"/>
      <stop offset="62%" stop-color="{palette.FurLightMid}"/>
      <stop offset="100%" stop-color="{palette.FurLightBottom}"/>
    </radialGradient>
    <radialGradient id="muzzle" cx="42%" cy="28%" r="78%">
      <stop offset="0%" stop-color="{palette.MuzzleTop}"/>
      <stop offset="100%" stop-color="{palette.MuzzleBottom}"/>
    </radialGradient>
    <radialGradient id="eye" cx="38%" cy="28%" r="70%">
      <stop offset="0%" stop-color="#5A2619"/>
      <stop offset="58%" stop-color="#2A0C08"/>
      <stop offset="100%" stop-color="#120403"/>
    </radialGradient>
    <linearGradient id="blue" x1="42" y1="46" x2="218" y2="132" gradientUnits="userSpaceOnUse">
      <stop offset="0%" stop-color="#78DCFF"/>
      <stop offset="42%" stop-color="#349CF0"/>
      <stop offset="100%" stop-color="#254EA9"/>
    </linearGradient>
    <linearGradient id="blue-dark" x1="46" y1="70" x2="210" y2="138" gradientUnits="userSpaceOnUse">
      <stop offset="0%" stop-color="#2E6ED1"/>
      <stop offset="100%" stop-color="#1F3F91"/>
    </linearGradient>
    <linearGradient id="bow" x1="88" y1="162" x2="168" y2="188" gradientUnits="userSpaceOnUse">
      <stop offset="0%" stop-color="#8CF7F1"/>
      <stop offset="52%" stop-color="#44D3DD"/>
      <stop offset="100%" stop-color="#18A6BD"/>
    </linearGradient>
    <filter id="drop" x="-25%" y="-25%" width="150%" height="150%">
      <feDropShadow dx="0" dy="3" stdDeviation="2.6" flood-color="#26344D" flood-opacity="0.16"/>
    </filter>
    <filter id="soft" x="-20%" y="-20%" width="140%" height="140%">
      <feGaussianBlur in="SourceAlpha" stdDeviation="0.25" result="blur"/>
      <feMerge>
        <feMergeNode in="blur"/>
        <feMergeNode in="SourceGraphic"/>
      </feMerge>
    </filter>
  </defs>
""";
    }

    public static string OpenMascotGroup()
    {
        return """
  <g filter="url(#drop)" stroke-linecap="round" stroke-linejoin="round">

""";
    }

    public static string BodyBeforeFace(bool hasHeadphones, bool hasBowTie)
    {
        var headphonesBand = hasHeadphones
            ? """
    <path d="M58 108 C56 38 200 38 198 108" fill="none" stroke="#253B86" stroke-width="18"/>
    <path d="M63 105 C64 48 192 48 193 105" fill="none" stroke="url(#blue)" stroke-width="11"/>
"""
            : "";

        var collar = hasBowTie
            ? """
    <path d="M83 164 C103 181 153 181 173 164" fill="none" stroke="url(#blue-dark)" stroke-width="9" opacity="0.95"/>
"""
            : "";

        return $"""
    <path d="M48 67 C48 49 61 38 76 39 C91 40 102 52 101 68 C100 84 87 96 71 95 C56 94 48 83 48 67Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.6"/>
    <path d="M155 68 C154 52 165 40 180 39 C195 38 208 49 208 67 C208 83 200 94 185 95 C169 96 156 84 155 68Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.6"/>
    <circle cx="73" cy="72" r="18" fill="#EBA96F" stroke="#A85D38" stroke-width="2.8" opacity="0.78"/>
    <circle cx="183" cy="72" r="18" fill="#EBA96F" stroke="#A85D38" stroke-width="2.8" opacity="0.78"/>
{headphonesBand}{collar}
    <ellipse cx="128" cy="194" rx="56" ry="49" fill="url(#fur)" stroke="#71381F" stroke-width="3.7"/>
    <ellipse cx="128" cy="209" rx="31" ry="24" fill="#FFEBD1" opacity="0.72"/>
    <ellipse cx="82" cy="218" rx="25" ry="25" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.7"/>
    <ellipse cx="174" cy="218" rx="25" ry="25" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.7"/>
    <ellipse cx="82" cy="220" rx="14" ry="16" fill="#FFE2C1" stroke="#B77349" stroke-width="2.2"/>
    <ellipse cx="174" cy="220" rx="14" ry="16" fill="#FFE2C1" stroke="#B77349" stroke-width="2.2"/>
    <ellipse cx="101" cy="185" rx="21" ry="38" transform="rotate(-20 101 185)" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.6"/>
    <ellipse cx="155" cy="185" rx="21" ry="38" transform="rotate(20 155 185)" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.6"/>
    <path d="M128 45 C166 45 194 72 194 111 C194 151 166 178 128 178 C90 178 62 151 62 111 C62 72 90 45 128 45Z" fill="url(#fur)" stroke="#71381F" stroke-width="3.8"/>
    <path d="M68 91 C63 98 62 108 64 116" fill="none" stroke="#A45A34" stroke-width="2" opacity="0.26"/>
    <path d="M188 91 C193 98 194 108 192 116" fill="none" stroke="#A45A34" stroke-width="2" opacity="0.26"/>
    <path d="M89 55 C96 51 103 51 109 55" fill="none" stroke="#FFF2D7" stroke-width="2.3" opacity="0.5"/>
    <path d="M117 48 C122 42 130 48 126 55" fill="none" stroke="#FFF2D7" stroke-width="2.2" opacity="0.5"/>
    <path d="M132 50 C139 43 149 48 142 57" fill="none" stroke="#FFF2D7" stroke-width="2.2" opacity="0.48"/>
    <path d="M111 51 C119 43 126 49 123 56 C132 45 141 47 135 58 C145 50 153 56 145 63" fill="none" stroke="#8A4729" stroke-width="2.2" opacity="0.38"/>
    <ellipse cx="128" cy="136" rx="43" ry="31" fill="url(#muzzle)"/>
    <ellipse cx="94" cy="133" rx="13" ry="11" fill="#F79AA0" opacity="0.75"/>
    <ellipse cx="162" cy="133" rx="13" ry="11" fill="#F79AA0" opacity="0.75"/>
""";
    }

    public static string AfterFace(bool hasHeadphones, bool hasBowTie)
    {
        var headphones = hasHeadphones
            ? """
    <ellipse cx="52" cy="105" rx="22" ry="32" fill="#243B86"/>
    <ellipse cx="204" cy="105" rx="22" ry="32" fill="#243B86"/>
    <ellipse cx="58" cy="105" rx="22" ry="30" fill="url(#blue)" stroke="#243B86" stroke-width="3.9"/>
    <ellipse cx="198" cy="105" rx="22" ry="30" fill="url(#blue)" stroke="#243B86" stroke-width="3.9"/>
    <ellipse cx="57" cy="101" rx="11" ry="19" fill="#C4F6FF" opacity="0.78"/>
    <ellipse cx="199" cy="101" rx="11" ry="19" fill="#C4F6FF" opacity="0.78"/>
    <path d="M47 90 C41 101 42 116 49 127" fill="none" stroke="#88E6FF" stroke-width="3" opacity="0.46"/>
    <path d="M209 90 C215 101 214 116 207 127" fill="none" stroke="#88E6FF" stroke-width="3" opacity="0.46"/>
    <circle cx="67" cy="88" r="4" fill="#FFFFFF" opacity="0.86"/>
    <circle cx="189" cy="88" r="4" fill="#FFFFFF" opacity="0.86"/>
"""
            : "";

        var bowTie = hasBowTie
            ? """
    <path d="M117 169 C106 156 90 151 84 160 C77 171 87 187 99 187 C107 187 113 182 117 177 Z" fill="url(#bow)" stroke="#118795" stroke-width="3"/>
    <path d="M139 169 C150 156 166 151 172 160 C179 171 169 187 157 187 C149 187 143 182 139 177 Z" fill="url(#bow)" stroke="#118795" stroke-width="3"/>
    <rect x="115" y="161" width="26" height="29" rx="11" fill="#38CAD4" stroke="#118795" stroke-width="3"/>
    <path d="M98 164 C104 169 110 173 116 175" stroke="#C8FFFF" stroke-width="2" opacity="0.68"/>
    <path d="M158 164 C152 169 146 173 140 175" stroke="#C8FFFF" stroke-width="2" opacity="0.68"/>
    <path d="M121 166 C125 164 132 164 136 166" stroke="#D5FFFF" stroke-width="1.6" opacity="0.58"/>
"""
            : "";

        return $"""
{headphones}{bowTie}
    <path d="M88 202 C93 208 99 212 104 213" fill="none" stroke="#8A4729" stroke-width="1.5" opacity="0.26"/>
    <path d="M168 202 C163 208 157 212 152 213" fill="none" stroke="#8A4729" stroke-width="1.5" opacity="0.26"/>
""";
    }

    public static string CloseMascotGroup()
    {
        return """
  </g>
""";
    }
}

internal sealed record FurPaletteValues(
    string FurTop,
    string FurMid,
    string FurBottom,
    string FurLightTop,
    string FurLightMid,
    string FurLightBottom,
    string MuzzleTop,
    string MuzzleBottom)
{
    public static FurPaletteValues From(GnouGnouBearFurPalette palette)
    {
        return palette switch
        {
            GnouGnouBearFurPalette.Honey => new("#FFF0B8", "#F4C65F", "#C4872F", "#FFF7D1", "#F7D987", "#D89B41", "#FFFFFF", "#FFF4CE"),
            GnouGnouBearFurPalette.Cocoa => new("#D8A06A", "#9B5F38", "#5F321F", "#E7B985", "#AC7148", "#6C3D27", "#FFF3DD", "#F2D6B8"),
            GnouGnouBearFurPalette.Polar => new("#FFFFFF", "#EAF6FF", "#BFD5E8", "#FFFFFF", "#F4FBFF", "#CFE3F1", "#FFFFFF", "#F5FBFF"),
            GnouGnouBearFurPalette.Panda => new("#F8F8F8", "#DCE2E8", "#AEB7C2", "#FFFFFF", "#E8EEF3", "#C4CCD6", "#FFFFFF", "#F2F2EA"),
            GnouGnouBearFurPalette.Rose => new("#FFE0E8", "#F2A8B9", "#C66C86", "#FFEAF0", "#F6BED0", "#D7839B", "#FFFFFF", "#FFE8EC"),
            GnouGnouBearFurPalette.Mint => new("#DDF8E9", "#8AD8B3", "#4A9A76", "#EAFFF4", "#ABE7C8", "#62AD88", "#FFFFFF", "#EDFFF7"),
            GnouGnouBearFurPalette.Blueberry => new("#D8E9FF", "#84AEE8", "#5270B8", "#E7F2FF", "#A6C7F2", "#6888C8", "#FFFFFF", "#EEF6FF"),
            GnouGnouBearFurPalette.Lavender => new("#EEE2FF", "#B99BE6", "#7D5DB9", "#F6EDFF", "#CEB7F0", "#9273C8", "#FFFFFF", "#F4ECFF"),
            _ => new("#FFE2B8", "#F4B778", "#D1844D", "#FFE9C8", "#F5BD82", "#D79057", "#FFFFFF", "#FFF0D5")
        };
    }
}
