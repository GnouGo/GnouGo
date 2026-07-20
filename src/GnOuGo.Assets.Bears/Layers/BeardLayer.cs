namespace GnOuGo.Assets.Bears.Layers;

internal static class BeardLayer
{
    public static string Render(bool hasBeard, ref StableRandom stableRandom)
    {
        if (!hasBeard)
        {
            return string.Empty;
        }

        var style = stableRandom.NextInclusive(0, 4);
        var size = stableRandom.NextInclusive(0, 2);
        var palette = BeardPalette.FromVariant(stableRandom.NextInclusive(0, 5));

        return style switch
        {
            1 => RenderLongPoint(size, palette),
            2 => RenderCloud(size, palette),
            3 => RenderSquare(size, palette),
            4 => RenderSplit(size, palette),
            _ => RenderClassic(size, palette)
        };
    }

    private static string RenderClassic(int size, BeardPalette palette)
    {
        var bottom = size switch { 0 => 184, 1 => 194, _ => 204 };
        var left = size switch { 0 => 91, 1 => 87, _ => 83 };
        var right = 256 - left;

        return $"""
    <path d="M{left} 132 C80 145 82 166 96 180 C108 194 119 {bottom - 2} 128 {bottom} C137 {bottom - 2} 148 194 160 180 C174 166 176 145 {right} 132 C158 148 145 156 128 157 C111 156 98 148 {left} 132Z" fill="{palette.Base}" stroke="{palette.Outline}" stroke-width="4.2" opacity="0.99"/>
    <path d="M99 143 C91 154 93 169 104 178 C112 185 121 188 128 189 C135 188 144 185 152 178 C163 169 165 154 157 143 C150 161 139 171 128 178 C117 171 106 161 99 143Z" fill="{palette.Inner}" stroke="{palette.Outline}" stroke-width="2.5" opacity="0.96"/>
    <path d="M103 158 C111 170 119 178 128 186 C137 178 145 170 153 158" fill="none" stroke="{palette.Shadow}" stroke-width="4.4" opacity="0.72"/>
    <path d="M115 162 C119 174 124 181 128 186 C132 181 137 174 141 162" fill="none" stroke="{palette.Highlight}" stroke-width="3.2" opacity="0.86"/>
""";
    }

    private static string RenderLongPoint(int size, BeardPalette palette)
    {
        var tip = size switch { 0 => 194, 1 => 206, _ => 218 };
        var left = size switch { 0 => 93, 1 => 88, _ => 84 };
        var right = 256 - left;

        return $"""
    <path d="M{left} 133 C84 151 91 176 111 {tip - 19} C119 {tip - 10} 124 {tip - 4} 128 {tip} C132 {tip - 4} 137 {tip - 10} 145 {tip - 19} C165 176 172 151 {right} 133 C158 151 144 161 128 164 C112 161 98 151 {left} 133Z" fill="{palette.Base}" stroke="{palette.Outline}" stroke-width="4.2" opacity="0.99"/>
    <path d="M105 145 C111 166 121 {tip - 16} 128 {tip - 4} C135 {tip - 16} 145 166 151 145 C145 169 136 {tip - 13} 128 {tip - 3} C120 {tip - 13} 111 169 105 145Z" fill="{palette.Inner}" stroke="{palette.Outline}" stroke-width="2.4" opacity="0.94"/>
    <path d="M112 158 C117 178 123 {tip - 11} 128 {tip - 3} C133 {tip - 11} 139 178 144 158" fill="none" stroke="{palette.Shadow}" stroke-width="4.4" opacity="0.72"/>
    <path d="M128 164 C126 182 126 {tip - 12} 128 {tip - 2}" fill="none" stroke="{palette.Highlight}" stroke-width="3.2" opacity="0.86"/>
""";
    }

    private static string RenderCloud(int size, BeardPalette palette)
    {
        var bottom = size switch { 0 => 183, 1 => 193, _ => 202 };

        return $"""
    <path d="M90 136 C78 145 80 162 92 168 C84 180 95 194 110 190 C114 {bottom + 4} 124 {bottom + 9} 128 {bottom + 10} C132 {bottom + 9} 142 {bottom + 4} 146 190 C161 194 172 180 164 168 C176 162 178 145 166 136 C154 150 143 154 128 155 C113 154 102 150 90 136Z" fill="{palette.Base}" stroke="{palette.Outline}" stroke-width="4.2" opacity="0.99"/>
    <path d="M102 151 C96 165 106 177 118 175 C120 188 126 {bottom} 128 {bottom + 4} C130 {bottom} 136 188 138 175 C150 177 160 165 154 151 C146 163 137 169 128 173 C119 169 110 163 102 151Z" fill="{palette.Inner}" stroke="{palette.Outline}" stroke-width="2.4" opacity="0.95"/>
    <path d="M101 168 C111 178 119 184 128 190 C137 184 145 178 155 168" fill="none" stroke="{palette.Shadow}" stroke-width="4.2" opacity="0.72"/>
    <path d="M112 158 C116 171 123 182 128 190 C133 182 140 171 144 158" fill="none" stroke="{palette.Highlight}" stroke-width="3.2" opacity="0.84"/>
""";
    }

    private static string RenderSquare(int size, BeardPalette palette)
    {
        var bottom = size switch { 0 => 180, 1 => 190, _ => 200 };
        var left = size switch { 0 => 93, 1 => 89, _ => 85 };
        var right = 256 - left;

        return $"""
    <path d="M{left} 134 C88 151 91 {bottom - 8} 106 {bottom - 2} C118 {bottom + 4} 138 {bottom + 4} 150 {bottom - 2} C165 {bottom - 8} 168 151 {right} 134 C157 149 144 155 128 156 C112 155 99 149 {left} 134Z" fill="{palette.Base}" stroke="{palette.Outline}" stroke-width="4.2" opacity="0.99"/>
    <path d="M103 148 C108 168 116 {bottom - 5} 128 {bottom - 2} C140 {bottom - 5} 148 168 153 148 C147 171 138 {bottom + 2} 128 {bottom + 2} C118 {bottom + 2} 109 171 103 148Z" fill="{palette.Inner}" stroke="{palette.Outline}" stroke-width="2.5" opacity="0.95"/>
    <path d="M108 161 C116 {bottom - 3} 140 {bottom - 3} 148 161" fill="none" stroke="{palette.Shadow}" stroke-width="4.4" opacity="0.74"/>
    <path d="M116 157 V{bottom - 1} M128 159 V{bottom + 2} M140 157 V{bottom - 1}" stroke="{palette.Highlight}" stroke-width="3" opacity="0.82"/>
""";
    }

    private static string RenderSplit(int size, BeardPalette palette)
    {
        var bottom = size switch { 0 => 190, 1 => 203, _ => 216 };
        var left = size switch { 0 => 91, 1 => 87, _ => 83 };
        var right = 256 - left;

        return $"""
    <path d="M{left} 134 C84 153 95 {bottom - 24} 117 {bottom - 10} C112 {bottom - 2} 106 {bottom + 1} 101 {bottom - 2} C112 {bottom + 13} 124 {bottom + 8} 128 {bottom - 5} C132 {bottom + 8} 144 {bottom + 13} 155 {bottom - 2} C150 {bottom + 1} 144 {bottom - 2} 139 {bottom - 10} C161 {bottom - 24} 172 153 {right} 134 C158 151 144 160 128 163 C112 160 98 151 {left} 134Z" fill="{palette.Base}" stroke="{palette.Outline}" stroke-width="4.2" opacity="0.99"/>
    <path d="M105 148 C113 {bottom - 31} 121 {bottom - 17} 126 {bottom - 8} C120 {bottom - 5} 114 {bottom - 8} 108 {bottom - 15}" fill="none" stroke="{palette.Shadow}" stroke-width="4.3" opacity="0.74"/>
    <path d="M151 148 C143 {bottom - 31} 135 {bottom - 17} 130 {bottom - 8} C136 {bottom - 5} 142 {bottom - 8} 148 {bottom - 15}" fill="none" stroke="{palette.Shadow}" stroke-width="4.3" opacity="0.74"/>
    <path d="M128 164 C126 181 126 {bottom - 16} 128 {bottom - 7}" fill="none" stroke="{palette.Highlight}" stroke-width="3.2" opacity="0.86"/>
""";
    }

    private sealed record BeardPalette(string Base, string Inner, string Outline, string Shadow, string Highlight)
    {
        public static BeardPalette FromVariant(int variant)
            => variant switch
            {
                1 => new("#FFFFFF", "#F8FAFC", "#5F6775", "#8E99A8", "#FFFFFF"),
                2 => new("#F6F8FB", "#E9EEF5", "#596579", "#8794A6", "#FFFFFF"),
                3 => new("#FFFDF5", "#F4F0E4", "#6E675C", "#9A9388", "#FFFFFF"),
                4 => new("#EEF3FA", "#DDE6F0", "#526070", "#7F8FA1", "#FFFFFF"),
                5 => new("#FFFFFF", "#F0F0EA", "#6B6860", "#928D84", "#FFFFFF"),
                _ => new("#FFFFFF", "#F4F6F8", "#555E6A", "#87909A", "#FFFFFF")
            };
    }
}
