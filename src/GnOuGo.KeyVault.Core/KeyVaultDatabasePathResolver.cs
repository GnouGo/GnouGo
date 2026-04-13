namespace GnOuGo.KeyVault.Core;

/// <summary>
/// Resolves the shared KeyVault SQLite path used by trusted hosts.
/// The default logical path `data/gnougo-keyvault.db` is mapped to a
/// stable Desktop\GnOuGo location so multiple local processes can share it.
/// </summary>
public static class KeyVaultDatabasePathResolver
{
    public const string DefaultRelativePath = "data/gnougo-keyvault.db";

    public static string Resolve(string? configuredPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
            return configuredPath;

        var normalized = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultRelativePath
            : configuredPath.Replace('\\', '/').Trim();

        if (string.Equals(normalized, DefaultRelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(
                ResolveDesktopDirectory(),
                "GnOuGo",
                "data",
                "gnougo-keyvault.db");
        }

        return Path.Combine(baseDirectory, configuredPath!);
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

