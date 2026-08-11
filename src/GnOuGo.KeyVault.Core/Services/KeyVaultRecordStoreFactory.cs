namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Creates workspace-backed encrypted record stores while keeping path and
/// persistence details inside the central KeyVault library.
/// </summary>
public static class KeyVaultRecordStoreFactory
{
    public static IKeyVaultRecordStore CreateWorkspaceStore(
        string? configuredPath,
        string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var databasePath = KeyVaultDatabasePathResolver.Resolve(configuredPath, baseDirectory);
        return new KeyVaultRecordStore(databasePath);
    }
}

