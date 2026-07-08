namespace GnOuGo.Assets.Bears.Layers;

internal static class MouthLayer
{
    public static string Render(GnouGnouBearEmotion emotion)
    {
        return emotion switch
        {
            GnouGnouBearEmotion.Surprised => """
    <ellipse cx="128" cy="126" rx="10.5" ry="7.2" fill="#552318"/>
    <ellipse cx="128" cy="123" rx="5.4" ry="2.1" fill="#FFFFFF" opacity="0.55"/>
    <ellipse cx="128" cy="143" rx="7" ry="8" fill="none" stroke="#552318" stroke-width="3.2"/>
""",
            GnouGnouBearEmotion.Sleeping => """
    <ellipse cx="128" cy="126" rx="10.5" ry="7.2" fill="#552318"/>
    <ellipse cx="128" cy="123" rx="5.4" ry="2.1" fill="#FFFFFF" opacity="0.55"/>
    <path d="M128 132 L128 139" stroke="#552318" stroke-width="3"/>
    <path d="M119 145 Q128 151 137 145" fill="none" stroke="#552318" stroke-width="3.4"/>
""",
            GnouGnouBearEmotion.Worried => """
    <ellipse cx="128" cy="126" rx="10.5" ry="7.2" fill="#552318"/>
    <ellipse cx="128" cy="123" rx="5.4" ry="2.1" fill="#FFFFFF" opacity="0.55"/>
    <path d="M128 132 L128 139" stroke="#552318" stroke-width="3"/>
    <path d="M116 149 Q128 141 140 149" fill="none" stroke="#552318" stroke-width="3.4"/>
""",
            GnouGnouBearEmotion.Proud => """
    <ellipse cx="128" cy="126" rx="10.5" ry="7.2" fill="#552318"/>
    <ellipse cx="128" cy="123" rx="5.4" ry="2.1" fill="#FFFFFF" opacity="0.55"/>
    <path d="M128 132 L128 141" stroke="#552318" stroke-width="3"/>
    <path d="M112 139 C118 153 138 153 144 139" fill="none" stroke="#552318" stroke-width="3.5"/>
""",
            _ => """
    <ellipse cx="128" cy="126" rx="10.5" ry="7.2" fill="#552318"/>
    <ellipse cx="128" cy="123" rx="5.4" ry="2.1" fill="#FFFFFF" opacity="0.55"/>
    <path d="M128 132 L128 141" stroke="#552318" stroke-width="3"/>
    <path d="M128 141 C122 150 112 150 106 140" fill="none" stroke="#552318" stroke-width="3.5"/>
    <path d="M128 141 C134 150 144 150 150 140" fill="none" stroke="#552318" stroke-width="3.5"/>
"""
        };
    }
}
