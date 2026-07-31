namespace GnOuGo.Assets.Bears.Layers;

internal static class MouthLayer
{
    public static string Render(GnouGnouBearEmotion emotion, GnouGnouBearNoseStyle noseStyle)
        => RenderNose(noseStyle) + RenderMouth(emotion);

    public static string RenderNose(GnouGnouBearNoseStyle noseStyle)
    {
        return noseStyle switch
        {
            GnouGnouBearNoseStyle.Button => """
    <g data-part="nose" data-nose-style="button">
      <ellipse cx="128" cy="126" rx="8.2" ry="7.4" fill="#552318"/>
      <circle cx="125.5" cy="123.5" r="2.2" fill="#FFFFFF" opacity="0.52"/>
    </g>
""",
            GnouGnouBearNoseStyle.Heart => """
    <g data-part="nose" data-nose-style="heart">
      <path d="M128 134 C124 129 116 126 117 120 C118 115 125 115 128 120 C131 115 138 115 139 120 C140 126 132 129 128 134Z" fill="#552318"/>
      <path d="M121 120 C123 118 126 119 127 121" fill="none" stroke="#FFFFFF" stroke-width="1.8" opacity="0.48"/>
    </g>
""",
            GnouGnouBearNoseStyle.Triangle => """
    <g data-part="nose" data-nose-style="triangle">
      <path d="M116 122 Q128 116 140 122 L133 132 Q128 137 123 132Z" fill="#552318"/>
      <path d="M121 122 Q128 119 134 122" fill="none" stroke="#FFFFFF" stroke-width="2" opacity="0.48"/>
    </g>
""",
            GnouGnouBearNoseStyle.Wide => """
    <g data-part="nose" data-nose-style="wide">
      <ellipse cx="128" cy="126" rx="13.5" ry="7.1" fill="#552318"/>
      <ellipse cx="124" cy="123" rx="4.4" ry="1.9" fill="#FFFFFF" opacity="0.48"/>
    </g>
""",
            _ => """
    <g data-part="nose" data-nose-style="default">
      <ellipse cx="128" cy="126" rx="10.5" ry="7.2" fill="#552318"/>
      <ellipse cx="128" cy="123" rx="5.4" ry="2.1" fill="#FFFFFF" opacity="0.55"/>
    </g>
"""
        };
    }

    public static string RenderMouth(GnouGnouBearEmotion emotion)
    {
        return emotion switch
        {
            GnouGnouBearEmotion.Surprised => """
    <ellipse cx="128" cy="143" rx="7" ry="8" fill="none" stroke="#552318" stroke-width="3.2"/>
""",
            GnouGnouBearEmotion.Sleeping => """
    <path d="M128 132 L128 139" stroke="#552318" stroke-width="3"/>
    <path d="M119 145 Q128 151 137 145" fill="none" stroke="#552318" stroke-width="3.4"/>
""",
            GnouGnouBearEmotion.Worried => """
    <path d="M128 132 L128 139" stroke="#552318" stroke-width="3"/>
    <path d="M116 149 Q128 141 140 149" fill="none" stroke="#552318" stroke-width="3.4"/>
""",
            GnouGnouBearEmotion.Proud => """
    <path d="M128 132 L128 141" stroke="#552318" stroke-width="3"/>
    <path d="M112 139 C118 153 138 153 144 139" fill="none" stroke="#552318" stroke-width="3.5"/>
""",
            _ => """
    <path d="M128 132 L128 141" stroke="#552318" stroke-width="3"/>
    <path d="M128 141 C122 150 112 150 106 140" fill="none" stroke="#552318" stroke-width="3.5"/>
    <path d="M128 141 C134 150 144 150 150 140" fill="none" stroke="#552318" stroke-width="3.5"/>
"""
        };
    }
}
