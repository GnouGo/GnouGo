using Microsoft.AspNetCore.Components;

namespace GnOuGo.Agent.Server.Components;

public partial class App : ComponentBase
{
    [Inject] private AppVersionInfo VersionInfo { get; set; } = null!;
    [Inject] private IWebHostEnvironment WebHostEnvironment { get; set; } = null!;

    private string VersionedAsset(string relativePath)
    {
        var version = VersionInfo.Version;
        var webRoot = WebHostEnvironment.WebRootPath;
        if (!string.IsNullOrWhiteSpace(webRoot))
        {
            var localPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(webRoot, localPath);
            if (File.Exists(fullPath))
                version = File.GetLastWriteTimeUtc(fullPath).Ticks.ToString();
        }

        return $"{relativePath}?v={Uri.EscapeDataString(version)}";
    }
}
