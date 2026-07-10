namespace GnOuGo.Assets.Bears.Layers;

internal static class EyesLayer
{
    public static string Render(GnouGnouBearEmotion emotion, GnouGnouBearEyeStyle eyeStyle, ref StableRandom stableRandom)
    {
        var shine = stableRandom.NextInclusive(-1, 1);

        if (eyeStyle != GnouGnouBearEyeStyle.Default)
            return RenderEyeStyle(eyeStyle, shine);

        return emotion switch
        {
            GnouGnouBearEmotion.Sleeping => """
    <path d="M94 106 C100 100 108 100 114 106" fill="none" stroke="#2A0C08" stroke-width="4.2"/>
    <path d="M142 106 C148 100 156 100 162 106" fill="none" stroke="#2A0C08" stroke-width="4.2"/>
    <path d="M96 90 Q104 87 112 91" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M144 91 Q152 87 160 90" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            GnouGnouBearEmotion.Surprised => $"""
    <circle cx="104" cy="105" r="12.5" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="12.5" fill="url(#eye)"/>
    <circle cx="{108 + shine}" cy="100" r="4.5" fill="#FFFFFF"/>
    <circle cx="{156 - shine}" cy="100" r="4.5" fill="#FFFFFF"/>
    <circle cx="101" cy="111" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <circle cx="149" cy="111" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <path d="M95 88 Q104 80 113 88" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M143 88 Q152 80 161 88" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            GnouGnouBearEmotion.Thinking => $"""
    <circle cx="104" cy="105" r="11" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="10.5" fill="url(#eye)"/>
    <circle cx="{108 + shine}" cy="100" r="4.2" fill="#FFFFFF"/>
    <circle cx="{156 - shine}" cy="101" r="4" fill="#FFFFFF"/>
    <circle cx="101" cy="111" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <circle cx="149" cy="111" r="2" fill="#FFFFFF" opacity="0.78"/>
    <path d="M96 90 Q104 84 112 89" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M143 88 Q153 84 161 92" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            GnouGnouBearEmotion.Focused => $"""
    <circle cx="104" cy="105" r="10.5" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="10.5" fill="url(#eye)"/>
    <circle cx="{108 + shine}" cy="100" r="3.9" fill="#FFFFFF"/>
    <circle cx="{156 - shine}" cy="100" r="3.9" fill="#FFFFFF"/>
    <circle cx="101" cy="111" r="1.9" fill="#FFFFFF" opacity="0.78"/>
    <circle cx="149" cy="111" r="1.9" fill="#FFFFFF" opacity="0.78"/>
    <path d="M95 91 Q104 86 113 88" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M143 88 Q152 86 161 91" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            GnouGnouBearEmotion.Worried => $"""
    <circle cx="104" cy="105" r="10.8" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="10.8" fill="url(#eye)"/>
    <circle cx="{108 + shine}" cy="100" r="4.1" fill="#FFFFFF"/>
    <circle cx="{156 - shine}" cy="100" r="4.1" fill="#FFFFFF"/>
    <circle cx="101" cy="111" r="2" fill="#FFFFFF" opacity="0.78"/>
    <circle cx="149" cy="111" r="2" fill="#FFFFFF" opacity="0.78"/>
    <path d="M96 88 Q104 92 112 90" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M144 90 Q152 92 160 88" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            _ => $"""
    <circle cx="104" cy="105" r="11" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="11" fill="url(#eye)"/>
    <circle cx="{108 + shine}" cy="100" r="4.2" fill="#FFFFFF"/>
    <circle cx="{156 - shine}" cy="100" r="4.2" fill="#FFFFFF"/>
    <circle cx="101" cy="111" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <circle cx="149" cy="111" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <path d="M96 89 Q104 83 112 89" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M144 89 Q152 83 160 89" fill="none" stroke="#562315" stroke-width="3.8"/>
"""
        };
    }

    private static string RenderEyeStyle(GnouGnouBearEyeStyle eyeStyle, int shine)
    {
        return eyeStyle switch
        {
            GnouGnouBearEyeStyle.BigGlossy => $"""
    <circle cx="104" cy="105" r="13" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="13" fill="url(#eye)"/>
    <circle cx="{109 + shine}" cy="99" r="5" fill="#FFFFFF"/>
    <circle cx="{157 - shine}" cy="99" r="5" fill="#FFFFFF"/>
    <circle cx="100" cy="112" r="2.6" fill="#FFFFFF" opacity="0.82"/>
    <circle cx="148" cy="112" r="2.6" fill="#FFFFFF" opacity="0.82"/>
    <path d="M96 89 Q104 83 112 89" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M144 89 Q152 83 160 89" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            GnouGnouBearEyeStyle.Tiny => """
    <circle cx="104" cy="105" r="8" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="8" fill="url(#eye)"/>
    <circle cx="107" cy="101" r="3" fill="#FFFFFF"/>
    <circle cx="155" cy="101" r="3" fill="#FFFFFF"/>
    <path d="M96 89 Q104 84 112 89" fill="none" stroke="#562315" stroke-width="3.5"/>
    <path d="M144 89 Q152 84 160 89" fill="none" stroke="#562315" stroke-width="3.5"/>
""",
            GnouGnouBearEyeStyle.Wink => """
    <path d="M94 106 C100 100 109 100 115 106" fill="none" stroke="#2A0C08" stroke-width="4.2"/>
    <circle cx="152" cy="105" r="11.5" fill="url(#eye)"/>
    <circle cx="156" cy="100" r="4.2" fill="#FFFFFF"/>
    <circle cx="149" cy="111" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <path d="M96 89 Q104 83 112 89" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M144 89 Q152 83 160 89" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            GnouGnouBearEyeStyle.Starry => """
    <path d="M104 92 L108 101 L118 102 L110 108 L113 118 L104 113 L95 118 L98 108 L90 102 L100 101 Z" fill="#2A0C08"/>
    <path d="M152 92 L156 101 L166 102 L158 108 L161 118 L152 113 L143 118 L146 108 L138 102 L148 101 Z" fill="#2A0C08"/>
    <circle cx="107" cy="100" r="2.8" fill="#FFFFFF" opacity="0.86"/>
    <circle cx="155" cy="100" r="2.8" fill="#FFFFFF" opacity="0.86"/>
    <path d="M96 88 Q104 82 112 88" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M144 88 Q152 82 160 88" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            GnouGnouBearEyeStyle.Sparkly => $"""
    <circle cx="104" cy="105" r="11" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="11" fill="url(#eye)"/>
    <path d="M109 94 L111 99 L116 100 L112 103 L114 108 L109 105 L104 108 L106 103 L102 100 L107 99 Z" fill="#FFFFFF"/>
    <path d="M157 94 L159 99 L164 100 L160 103 L162 108 L157 105 L152 108 L154 103 L150 100 L155 99 Z" fill="#FFFFFF"/>
    <circle cx="{100 + shine}" cy="112" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <circle cx="{148 - shine}" cy="112" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <path d="M96 89 Q104 83 112 89" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M144 89 Q152 83 160 89" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            GnouGnouBearEyeStyle.SideEye => """
    <circle cx="104" cy="105" r="11" fill="url(#eye)"/>
    <circle cx="152" cy="105" r="11" fill="url(#eye)"/>
    <circle cx="101" cy="100" r="4.2" fill="#FFFFFF"/>
    <circle cx="149" cy="100" r="4.2" fill="#FFFFFF"/>
    <circle cx="98" cy="111" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <circle cx="146" cy="111" r="2.1" fill="#FFFFFF" opacity="0.78"/>
    <path d="M96 89 Q104 83 112 89" fill="none" stroke="#562315" stroke-width="3.8"/>
    <path d="M144 89 Q152 83 160 89" fill="none" stroke="#562315" stroke-width="3.8"/>
""",
            _ => ""
        };
    }
}
