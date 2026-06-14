using GnOuGo.Workspace;

namespace GnOuGo.KeyVault.Core;

/// <summary>
/// Resolves the shared KeyVault SQLite path used by trusted hosts.
/// The default logical path `.GnOuGo/data/gnougo-keyvault.db` is mapped to a
/// stable workspace-local .GnOuGo/data location so multiple local processes can share it.
/// </summary>
public static class KeyVaultDatabasePathResolver
{
    public const string DefaultRelativePath = ".GnOuGo/data/gnougo-keyvault.db";

    public static string Resolve(string? configuredPath, string baseDirectory)
        => GnOuGoWorkspace.ResolveDatabasePath(configuredPath, baseDirectory, DefaultRelativePath);
}
