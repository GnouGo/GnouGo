namespace GnOuGo.Browser.Mcp;

public sealed class BrowserServerSettings
{
    public const string SectionName = "Browser";

    public bool Headless { get; set; } = true;
    public string BrowserName { get; set; } = "chromium";
    public string? Channel { get; set; }
    public string? UserAgent { get; set; }
    public int DefaultTimeoutMs { get; set; } = 30_000;
    public int NavigationTimeoutMs { get; set; } = 45_000;
    public int MaxContentCharacters { get; set; } = 12_000;
    public int ScreenshotQuality { get; set; } = 90;
    public int SlowMoMs { get; set; }
    public int HoldOpenMs { get; set; }
    public bool KeepBrowserOpen { get; set; }
    public List<string> AllowedHosts { get; set; } = [];
}
