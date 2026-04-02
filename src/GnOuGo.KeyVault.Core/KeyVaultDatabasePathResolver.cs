namespace GnOuGo.KeyVault.Core;

/// <summary>
/// Resolves the shared KeyVault SQLite path used by trusted hosts.
/// The default logical path `data/gnougo-keyvault.db` is mapped to a
/// stable LocalApplicationData location so multiple processes can share it.
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
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GnOuGo.Agent",
                "data",
                "gnougo-keyvault.db");
        }

        return Path.Combine(baseDirectory, configuredPath!);
    }
}

