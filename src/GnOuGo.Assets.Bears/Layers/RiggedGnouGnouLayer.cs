namespace GnOuGo.Assets.Bears.Layers;

/// <summary>
/// An animation-oriented rendering of the mascot. Geometry remains in the canonical
/// 256x256 coordinate system, while every movable part declares its own stable pivot.
/// Hosts animate the groups; the SVG itself stays script-free.
/// </summary>
internal static class RiggedGnouGnouLayer
{
    public static string Render(
        GnouGnouBearOptions options,
        bool hasHeadphones,
        bool hasBowTie,
        AccessoryPalette palette)
    {
        var headphones = hasHeadphones ? RenderHeadphones(palette) : string.Empty;
        var bowTie = hasBowTie ? RenderBowTie(palette) : string.Empty;
        var beard = options.HasBeard ? RenderBeard() : string.Empty;

        var animation = GnouGnouBearAnimationNames.ToToken(options.Animation);
        return $$"""
  <g class="gnougo-rig" data-animation-rig="true" data-animation="{{animation}}" data-animation-enabled="{{(options.Animation != GnouGnouBearAnimation.None ? "true" : "false")}}" filter="url(#drop)" stroke-linecap="round" stroke-linejoin="round">
    <g class="gnougo-part gnougo-leg-left" data-part="leg-left" data-pivot-x="104" data-pivot-y="179">
      <path d="M105 174 C97 180 90 194 87 212 C84 226 92 237 105 237 C119 237 123 226 120 213 C117 196 115 182 105 174Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.8"/>
      <ellipse cx="102" cy="226" rx="15" ry="11" fill="#FFE2C1" stroke="#B77349" stroke-width="2.2"/>
      <path d="M94 225q8-8 16 0" fill="none" stroke="#D39367" stroke-width="2" opacity=".8"/>
    </g>
    <g class="gnougo-part gnougo-leg-right" data-part="leg-right" data-pivot-x="152" data-pivot-y="179">
      <path d="M151 174 C159 180 166 194 169 212 C172 226 164 237 151 237 C137 237 133 226 136 213 C139 196 141 182 151 174Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.8"/>
      <ellipse cx="154" cy="226" rx="15" ry="11" fill="#FFE2C1" stroke="#B77349" stroke-width="2.2"/>
      <path d="M146 225q8-8 16 0" fill="none" stroke="#D39367" stroke-width="2" opacity=".8"/>
    </g>

    <g class="gnougo-part gnougo-body" data-part="body" data-pivot-x="128" data-pivot-y="181">
      <ellipse cx="128" cy="185" rx="53" ry="48" fill="url(#fur)" stroke="#71381F" stroke-width="3.8"/>
      <ellipse cx="128" cy="199" rx="29" ry="27" fill="#FFEBD1" opacity=".75"/>
      <path d="M98 177q30 19 60 0" fill="none" stroke="#A45A34" stroke-width="2" opacity=".24"/>
    </g>

    <g class="gnougo-part gnougo-arm-left" data-part="arm-left" data-pivot-x="94" data-pivot-y="157">
      <path d="M98 154 C83 153 68 169 66 190 C64 208 74 217 87 210 C98 204 103 187 106 171 C108 162 105 157 98 154Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.8"/>
      <g data-part="hand-left" data-pivot-x="80" data-pivot-y="202">
        <circle cx="80" cy="202" r="13" fill="#FFE2C1" stroke="#B77349" stroke-width="2.3"/>
        <path d="M74 200q6-6 12 0M76 205q4-4 8 0" fill="none" stroke="#D39367" stroke-width="1.8"/>
      </g>
    </g>
    <g class="gnougo-part gnougo-arm-right" data-part="arm-right" data-pivot-x="162" data-pivot-y="157">
      <path d="M158 154 C173 153 188 169 190 190 C192 208 182 217 169 210 C158 204 153 187 150 171 C148 162 151 157 158 154Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.8"/>
      <g data-part="hand-right" data-pivot-x="176" data-pivot-y="202">
        <circle cx="176" cy="202" r="13" fill="#FFE2C1" stroke="#B77349" stroke-width="2.3"/>
        <path d="M170 200q6-6 12 0M172 205q4-4 8 0" fill="none" stroke="#D39367" stroke-width="1.8"/>
      </g>
    </g>

    <g class="gnougo-part gnougo-head" data-part="head" data-pivot-x="128" data-pivot-y="151">
      <g class="gnougo-part gnougo-ear-left" data-part="ear-left" data-pivot-x="91" data-pivot-y="82">
        <path d="M48 67 C48 49 61 38 76 39 C91 40 102 52 101 68 C100 84 87 96 71 95 C56 94 48 83 48 67Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.6"/>
        <circle cx="73" cy="72" r="18" fill="#EBA96F" stroke="#A85D38" stroke-width="2.8" opacity=".78"/>
      </g>
      <g class="gnougo-part gnougo-ear-right" data-part="ear-right" data-pivot-x="165" data-pivot-y="82">
        <path d="M155 68 C154 52 165 40 180 39 C195 38 208 49 208 67 C208 83 200 94 185 95 C169 96 156 84 155 68Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.6"/>
        <circle cx="183" cy="72" r="18" fill="#EBA96F" stroke="#A85D38" stroke-width="2.8" opacity=".78"/>
      </g>
{{headphones}}
      <path d="M128 45 C166 45 194 72 194 111 C194 151 166 178 128 178 C90 178 62 151 62 111 C62 72 90 45 128 45Z" fill="url(#fur)" stroke="#71381F" stroke-width="3.8"/>
      <path d="M91 58q10-11 19-2M117 50q8-9 14 2M139 52q9-7 16 2" fill="none" stroke="#FFF2D7" stroke-width="2.5" opacity=".56"/>
      <ellipse cx="128" cy="136" rx="43" ry="31" fill="url(#muzzle)"/>
      <g data-part="cheek-left" data-pivot-x="91" data-pivot-y="135">
        <ellipse cx="91" cy="135" rx="13" ry="9" fill="#F79AA0" opacity=".68"/>
      </g>
      <g data-part="cheek-right" data-pivot-x="165" data-pivot-y="135">
        <ellipse cx="165" cy="135" rx="13" ry="9" fill="#F79AA0" opacity=".68"/>
      </g>

      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105">
        <ellipse cx="104" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106">
          <ellipse cx="104" cy="107" rx="8" ry="10" fill="url(#eye)"/>
          <circle cx="107" cy="103" r="3" fill="#fff"/>
        </g>
      </g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105">
        <ellipse cx="152" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106">
          <ellipse cx="152" cy="107" rx="8" ry="10" fill="url(#eye)"/>
          <circle cx="155" cy="103" r="3" fill="#fff"/>
        </g>
      </g>
      <path data-part="brow-left" data-pivot-x="104" data-pivot-y="83" d="M91 82q13-8 25 1" fill="none" stroke="#71381F" stroke-width="4"/>
      <path data-part="brow-right" data-pivot-x="152" data-pivot-y="83" d="M140 83q13-9 25-1" fill="none" stroke="#71381F" stroke-width="4"/>
      <path d="M118 126 Q128 118 138 126 Q136 137 128 137 Q120 137 118 126Z" fill="#3A1511"/>
      <g data-part="mouth" data-pivot-x="128" data-pivot-y="145">
        <g data-expression="default">
          <path d="M128 137v7M128 144q-11 12-22 1M128 144q11 12 22 1" fill="none" stroke="#6B261D" stroke-width="3.2"/>
          <path d="M117 153q11 10 22 0" fill="#F47E86" stroke="#6B261D" stroke-width="2"/>
        </g>
        <g data-expression="failure" opacity="0">
          <path d="M128 137v6" fill="none" stroke="#6B261D" stroke-width="3.2"/>
          <path d="M109 158 Q128 137 147 158" fill="none" stroke="#6B261D" stroke-width="3.6"/>
          <path d="M109 158l-4 3M147 158l4 3" fill="none" stroke="#6B261D" stroke-width="2.4"/>
        </g>
      </g>
{{beard}}
    </g>
{{bowTie}}
    <g class="gnougo-action-fx" data-part="action-fx" opacity="0" pointer-events="none">
      <path d="M74 36l5 10 11 2-8 8 2 11-10-5-10 5 2-11-8-8 11-2z" fill="#FFE36E" stroke="#B86A19" stroke-width="2"/>
      <circle cx="190" cy="51" r="8" fill="#38F8DF"/>
      <circle cx="205" cy="34" r="4" fill="#7A6BE8"/>
      <path d="M49 116q-22-13-28 8M207 116q22-13 28 8" fill="none" stroke="#38F8DF" stroke-width="5"/>
    </g>
  </g>
""";
    }

    private static string RenderHeadphones(AccessoryPalette palette) => $$"""
      <path d="M58 108 C56 38 200 38 198 108" fill="none" stroke="{{palette.Deep}}" stroke-width="18"/>
      <path d="M63 105 C64 48 192 48 193 105" fill="none" stroke="url(#blue)" stroke-width="11"/>
      <ellipse cx="52" cy="105" rx="22" ry="32" fill="{{palette.Deep}}"/>
      <ellipse cx="204" cy="105" rx="22" ry="32" fill="{{palette.Deep}}"/>
      <ellipse cx="58" cy="105" rx="22" ry="30" fill="url(#blue)" stroke="{{palette.Deep}}" stroke-width="3.9"/>
      <ellipse cx="198" cy="105" rx="22" ry="30" fill="url(#blue)" stroke="{{palette.Deep}}" stroke-width="3.9"/>
      <ellipse cx="57" cy="101" rx="11" ry="19" fill="{{palette.Light}}" opacity=".78"/>
      <ellipse cx="199" cy="101" rx="11" ry="19" fill="{{palette.Light}}" opacity=".78"/>
""";

    private static string RenderBowTie(AccessoryPalette palette) => $$"""
    <g data-part="bow-tie" data-pivot-x="128" data-pivot-y="174">
      <path d="M117 169 C106 156 90 151 84 160 C77 171 87 187 99 187 C107 187 113 182 117 177 Z" fill="url(#bow)" stroke="{{palette.Dark}}" stroke-width="3"/>
      <path d="M139 169 C150 156 166 151 172 160 C179 171 169 187 157 187 C149 187 143 182 139 177 Z" fill="url(#bow)" stroke="{{palette.Dark}}" stroke-width="3"/>
      <rect x="115" y="161" width="26" height="29" rx="11" fill="{{palette.Accent}}" stroke="{{palette.Dark}}" stroke-width="3"/>
    </g>
""";

    private static string RenderBeard() => """
      <g data-part="beard">
        <path d="M98 142 C103 158 107 176 121 183 L128 174 L135 183 C149 176 153 158 158 142 C147 149 138 150 128 145 C118 150 109 149 98 142Z" fill="#5A352B" stroke="#2E1B17" stroke-width="2.8" opacity=".98"/>
        <path d="M107 150q10 13 15 23M149 150q-10 13-15 23M119 151q4 12 9 23M137 151q-4 12-9 23" fill="none" stroke="#8B5A45" stroke-width="2.2" opacity=".74"/>
      </g>
""";
}
