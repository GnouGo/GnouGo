namespace GnOuGo.Assets.Bears.Layers;

internal static class BackgroundLayer
{
    public static string Render(GnouGnouBearTheme theme)
    {
        return theme switch
        {
            GnouGnouBearTheme.Transparent => """
  <ellipse cx="128" cy="232" rx="73" ry="12" fill="#273247" opacity="0.13"/>

""",
            GnouGnouBearTheme.SoftBlue => """
  <rect width="256" height="256" rx="46" fill="#EDF8FF"/>
  <ellipse cx="128" cy="232" rx="73" ry="12" fill="#273247" opacity="0.13"/>

""",
            GnouGnouBearTheme.Warm => """
  <rect width="256" height="256" rx="46" fill="#FFF7ED"/>
  <ellipse cx="128" cy="232" rx="73" ry="12" fill="#273247" opacity="0.13"/>

""",
            GnouGnouBearTheme.Mint => """
  <rect width="256" height="256" rx="46" fill="#EEFFF9"/>
  <ellipse cx="128" cy="232" rx="73" ry="12" fill="#273247" opacity="0.13"/>

""",
            GnouGnouBearTheme.Lavender => """
  <rect width="256" height="256" rx="46" fill="#F6F0FF"/>
  <ellipse cx="128" cy="232" rx="73" ry="12" fill="#273247" opacity="0.13"/>

""",
            _ => """
  <rect width="256" height="256" rx="46" fill="url(#bg)"/>
  <ellipse cx="128" cy="232" rx="73" ry="12" fill="#273247" opacity="0.13"/>

"""
        };
    }
}
