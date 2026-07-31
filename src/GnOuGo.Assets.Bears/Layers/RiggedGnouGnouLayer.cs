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
        AccessoryPalette palette,
        ref StableRandom stableRandom)
    {
        var headphones = hasHeadphones ? RenderHeadphones(palette) : string.Empty;
        var bowTie = hasBowTie ? RenderBowTie(palette) : string.Empty;
        var eyes = RenderEyes(options.Emotion, options.EyeStyle);
        var nose = MouthLayer.RenderNose(options.NoseStyle);
        var mouth = MouthLayer.RenderMouth(options.Emotion);
        var beard = BeardLayer.Render(
            options.HasBeard,
            options.BeardStyle,
            ref stableRandom,
            preserveOffsetOnPartReset: true);

        var animation = GnouGnouBearAnimationNames.ToToken(options.Animation);
        return $$"""
  <g class="gnougo-rig" data-animation-rig="true" data-animation="{{animation}}" data-animation-enabled="{{(options.Animation != GnouGnouBearAnimation.None ? "true" : "false")}}" data-eye-style="{{options.EyeStyle.ToString().ToLowerInvariant()}}" data-emotion="{{options.Emotion.ToString().ToLowerInvariant()}}" data-nose-style="{{options.NoseStyle.ToString().ToLowerInvariant()}}" filter="url(#drop)" stroke-linecap="round" stroke-linejoin="round">
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
      <g data-part="thinking-flush" data-pivot-x="128" data-pivot-y="103" opacity="0" pointer-events="none">
        <path d="M128 48 C163 48 190 74 190 111 C190 145 163 171 128 171 C93 171 66 145 66 111 C66 74 93 48 128 48Z" fill="#EF625D" opacity=".62"/>
      </g>
      <ellipse cx="128" cy="136" rx="43" ry="31" fill="url(#muzzle)"/>
      <g data-part="cheek-left" data-pivot-x="91" data-pivot-y="135">
        <ellipse cx="91" cy="135" rx="13" ry="9" fill="#F79AA0" opacity=".68"/>
      </g>
      <g data-part="cheek-right" data-pivot-x="165" data-pivot-y="135">
        <ellipse cx="165" cy="135" rx="13" ry="9" fill="#F79AA0" opacity=".68"/>
      </g>

{{eyes}}
{{nose}}
      <g data-part="mouth" data-pivot-x="128" data-pivot-y="145">
        <g data-expression="default">
{{mouth}}
        </g>
        <g data-expression="failure" opacity="0">
          <path d="M128 137v6" fill="none" stroke="#6B261D" stroke-width="3.2"/>
          <path d="M109 158 Q128 137 147 158" fill="none" stroke="#6B261D" stroke-width="3.6"/>
          <path d="M109 158l-4 3M147 158l4 3" fill="none" stroke="#6B261D" stroke-width="2.4"/>
        </g>
      </g>
      <g data-part="thinking-sweat" data-pivot-x="169" data-pivot-y="61" opacity="0" pointer-events="none">
        <path d="M169 48 C165 55 160 61 160 67 C160 73 164 77 169 77 C175 77 179 73 179 67 C179 61 174 55 169 48Z" fill="#BDEEFF" stroke="#2B79B9" stroke-width="2.3"/>
        <path d="M166 60 C164 64 164 68 167 70" fill="none" stroke="#FFFFFF" stroke-width="2.2" opacity=".9"/>
        <path d="M187 65 C184 70 181 74 181 78 C181 82 184 85 188 85 C192 85 195 82 195 78 C195 74 191 70 187 65Z" fill="#D9F7FF" stroke="#2B79B9" stroke-width="1.8" opacity=".88"/>
      </g>
{{bowTie}}
{{beard}}
    </g>
    <g class="gnougo-part gnougo-thinking-arm-rub" data-part="thinking-arm-rub" data-pivot-x="162" data-pivot-y="157" opacity="0" pointer-events="none">
      <path d="M158 154 C173 153 188 169 190 190 C192 208 182 217 169 210 C158 204 153 187 150 171 C148 162 151 157 158 154Z" fill="url(#fur-light)" stroke="#71381F" stroke-width="3.8"/>
      <g data-part="thinking-hand-rub" data-pivot-x="176" data-pivot-y="202">
        <circle cx="176" cy="202" r="13" fill="#FFE2C1" stroke="#B77349" stroke-width="2.3"/>
        <path d="M170 200q6-6 12 0M172 205q4-4 8 0" fill="none" stroke="#D39367" stroke-width="1.8"/>
      </g>
    </g>
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

    private static string RenderEyes(GnouGnouBearEmotion emotion, GnouGnouBearEyeStyle eyeStyle)
    {
        var eyes = eyeStyle switch
        {
            GnouGnouBearEyeStyle.BigGlossy => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105">
        <ellipse cx="104" cy="105" rx="15.5" ry="18.5" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106">
          <ellipse cx="104" cy="107" rx="9.5" ry="12" fill="url(#eye)"/>
          <circle cx="108" cy="101" r="4.2" fill="#fff"/><circle cx="100" cy="113" r="2.2" fill="#fff" opacity=".8"/>
        </g>
      </g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105">
        <ellipse cx="152" cy="105" rx="15.5" ry="18.5" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106">
          <ellipse cx="152" cy="107" rx="9.5" ry="12" fill="url(#eye)"/>
          <circle cx="156" cy="101" r="4.2" fill="#fff"/><circle cx="148" cy="113" r="2.2" fill="#fff" opacity=".8"/>
        </g>
      </g>
""",
            GnouGnouBearEyeStyle.Tiny => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105">
        <ellipse cx="104" cy="105" rx="10.5" ry="12.5" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106"><ellipse cx="104" cy="106" rx="5.8" ry="7" fill="url(#eye)"/><circle cx="106" cy="103" r="2.2" fill="#fff"/></g>
      </g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105">
        <ellipse cx="152" cy="105" rx="10.5" ry="12.5" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106"><ellipse cx="152" cy="106" rx="5.8" ry="7" fill="url(#eye)"/><circle cx="154" cy="103" r="2.2" fill="#fff"/></g>
      </g>
""",
            GnouGnouBearEyeStyle.Wink => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105">
        <path d="M91 106 C98 99 110 99 117 106" fill="none" stroke="#71381F" stroke-width="4.2"/>
        <g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106"/>
      </g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105">
        <ellipse cx="152" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106"><ellipse cx="152" cy="107" rx="8" ry="10" fill="url(#eye)"/><circle cx="155" cy="103" r="3" fill="#fff"/></g>
      </g>
""",
            GnouGnouBearEyeStyle.Starry => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105">
        <circle cx="104" cy="105" r="15" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106"><path d="M104 92l4 9 10 1-8 6 3 10-9-5-9 5 3-10-8-6 10-1z" fill="url(#eye)"/><circle cx="108" cy="100" r="2.8" fill="#fff"/></g>
      </g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105">
        <circle cx="152" cy="105" r="15" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106"><path d="M152 92l4 9 10 1-8 6 3 10-9-5-9 5 3-10-8-6 10-1z" fill="url(#eye)"/><circle cx="156" cy="100" r="2.8" fill="#fff"/></g>
      </g>
""",
            GnouGnouBearEyeStyle.Sparkly => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105">
        <ellipse cx="104" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106"><ellipse cx="104" cy="107" rx="8" ry="10" fill="url(#eye)"/><path d="M108 97l2 4 4 1-3 3 1 4-4-2-4 2 1-4-3-3 4-1z" fill="#fff"/></g>
      </g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105">
        <ellipse cx="152" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106"><ellipse cx="152" cy="107" rx="8" ry="10" fill="url(#eye)"/><path d="M156 97l2 4 4 1-3 3 1 4-4-2-4 2 1-4-3-3 4-1z" fill="#fff"/></g>
      </g>
""",
            GnouGnouBearEyeStyle.SideEye => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105">
        <ellipse cx="104" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106"><ellipse cx="100" cy="107" rx="8" ry="10" fill="url(#eye)"/><circle cx="102" cy="103" r="3" fill="#fff"/></g>
      </g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105">
        <ellipse cx="152" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106"><ellipse cx="148" cy="107" rx="8" ry="10" fill="url(#eye)"/><circle cx="150" cy="103" r="3" fill="#fff"/></g>
      </g>
""",
            _ when emotion == GnouGnouBearEmotion.Sleeping => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105"><path d="M91 106 C98 99 110 99 117 106" fill="none" stroke="#71381F" stroke-width="4.2"/><g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106"/></g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105"><path d="M139 106 C146 99 158 99 165 106" fill="none" stroke="#71381F" stroke-width="4.2"/><g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106"/></g>
""",
            _ when emotion == GnouGnouBearEmotion.Surprised => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105"><ellipse cx="104" cy="105" rx="15" ry="19" fill="#fff" stroke="#71381F" stroke-width="2.4"/><g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106"><ellipse cx="104" cy="107" rx="9" ry="11" fill="url(#eye)"/><circle cx="108" cy="101" r="3.8" fill="#fff"/></g></g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105"><ellipse cx="152" cy="105" rx="15" ry="19" fill="#fff" stroke="#71381F" stroke-width="2.4"/><g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106"><ellipse cx="152" cy="107" rx="9" ry="11" fill="url(#eye)"/><circle cx="156" cy="101" r="3.8" fill="#fff"/></g></g>
""",
            _ => """
      <g class="gnougo-eye" data-part="eye-left" data-pivot-x="104" data-pivot-y="105">
        <ellipse cx="104" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-left" data-pivot-x="104" data-pivot-y="106"><ellipse cx="104" cy="107" rx="8" ry="10" fill="url(#eye)"/><circle cx="107" cy="103" r="3" fill="#fff"/></g>
      </g>
      <g class="gnougo-eye" data-part="eye-right" data-pivot-x="152" data-pivot-y="105">
        <ellipse cx="152" cy="105" rx="14" ry="17" fill="#fff" stroke="#71381F" stroke-width="2.4"/>
        <g data-part="pupil-right" data-pivot-x="152" data-pivot-y="106"><ellipse cx="152" cy="107" rx="8" ry="10" fill="url(#eye)"/><circle cx="155" cy="103" r="3" fill="#fff"/></g>
      </g>
"""
        };

        return eyes + RenderBrows(emotion);
    }

    private static string RenderBrows(GnouGnouBearEmotion emotion)
    {
        return emotion switch
        {
            GnouGnouBearEmotion.Surprised => """
      <path data-part="brow-left" data-pivot-x="104" data-pivot-y="83" d="M91 86 Q104 76 117 86" fill="none" stroke="#71381F" stroke-width="4"/>
      <path data-part="brow-right" data-pivot-x="152" data-pivot-y="83" d="M139 86 Q152 76 165 86" fill="none" stroke="#71381F" stroke-width="4"/>
""",
            GnouGnouBearEmotion.Thinking => """
      <path data-part="brow-left" data-pivot-x="104" data-pivot-y="83" d="M91 83 Q104 77 117 84" fill="none" stroke="#71381F" stroke-width="4"/>
      <path data-part="brow-right" data-pivot-x="152" data-pivot-y="83" d="M139 80 Q153 78 165 88" fill="none" stroke="#71381F" stroke-width="4"/>
""",
            GnouGnouBearEmotion.Focused => """
      <path data-part="brow-left" data-pivot-x="104" data-pivot-y="83" d="M91 79 Q104 84 117 87" fill="none" stroke="#71381F" stroke-width="4.4"/>
      <path data-part="brow-right" data-pivot-x="152" data-pivot-y="83" d="M139 87 Q152 84 165 79" fill="none" stroke="#71381F" stroke-width="4.4"/>
""",
            GnouGnouBearEmotion.Worried => """
      <path data-part="brow-left" data-pivot-x="104" data-pivot-y="83" d="M91 87 Q104 78 117 82" fill="none" stroke="#71381F" stroke-width="4"/>
      <path data-part="brow-right" data-pivot-x="152" data-pivot-y="83" d="M139 82 Q152 78 165 87" fill="none" stroke="#71381F" stroke-width="4"/>
""",
            GnouGnouBearEmotion.Proud => """
      <path data-part="brow-left" data-pivot-x="104" data-pivot-y="83" d="M91 82 Q104 75 117 81" fill="none" stroke="#71381F" stroke-width="4.8"/>
      <path data-part="brow-right" data-pivot-x="152" data-pivot-y="83" d="M139 81 Q152 75 165 82" fill="none" stroke="#71381F" stroke-width="4.8"/>
""",
            GnouGnouBearEmotion.Sleeping => """
      <path data-part="brow-left" data-pivot-x="104" data-pivot-y="83" d="M93 88 Q104 84 115 88" fill="none" stroke="#71381F" stroke-width="3.5"/>
      <path data-part="brow-right" data-pivot-x="152" data-pivot-y="83" d="M141 88 Q152 84 163 88" fill="none" stroke="#71381F" stroke-width="3.5"/>
""",
            _ => """
      <path data-part="brow-left" data-pivot-x="104" data-pivot-y="83" d="M91 82q13-8 25 1" fill="none" stroke="#71381F" stroke-width="4"/>
      <path data-part="brow-right" data-pivot-x="152" data-pivot-y="83" d="M140 83q13-9 25-1" fill="none" stroke="#71381F" stroke-width="4"/>
"""
        };
    }
}
