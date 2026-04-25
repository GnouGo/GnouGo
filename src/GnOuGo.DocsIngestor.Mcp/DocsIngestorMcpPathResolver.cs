namespace GnOuGo.DocsIngestor.Mcp;

public static class DocsIngestorMcpPathResolver
{
    public static string Resolve(string? configuredPath, string baseDirectory, string defaultRelativePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
            return Path.GetFullPath(configuredPath);

        var normalized = string.IsNullOrWhiteSpace(configuredPath)
            ? defaultRelativePath
            : configuredPath.Replace('\\', '/').Trim();

        if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(
                ResolveDesktopDirectory(),
                "GnOuGo",
                normalized.Replace('/', Path.DirectorySeparatorChar));
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, configuredPath ?? defaultRelativePath));
    }

    private static string ResolveDesktopDirectory()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopPath))
            return Path.GetFullPath(desktopPath);

        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfilePath))
            return Path.GetFullPath(Path.Combine(userProfilePath, "Desktop"));

        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homePath))
            return Path.GetFullPath(Path.Combine(homePath, "Desktop"));

        throw new InvalidOperationException("Unable to resolve the current user's Desktop directory.");
    }
}

