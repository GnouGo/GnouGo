namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Creates storage-backed KeyVault readers while keeping workspace and persistence details
/// inside the central KeyVault library.
/// </summary>
public static class KeyVaultSecretReaderFactory
{
    public static IKeyVaultSecretReader CreateWorkspaceReader(
        string? configuredPath,
        string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var databasePath = KeyVaultDatabasePathResolver.Resolve(configuredPath, baseDirectory);
        return new KeyVaultSecretReader(databasePath);
    }

    public static IKeyVaultSecretCatalogReader CreateWorkspaceCatalogReader(
        string? configuredPath,
        string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var databasePath = KeyVaultDatabasePathResolver.Resolve(configuredPath, baseDirectory);
        return new KeyVaultSecretReader(databasePath);
    }
}
