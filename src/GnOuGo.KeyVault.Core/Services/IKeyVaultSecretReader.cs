namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Storage-agnostic read access to encrypted KeyVault secrets used by trusted consumers.
/// </summary>
public interface IKeyVaultSecretReader
{
    Task<string?> GetDefaultTenantSecretValueAsync(
        string key,
        string? author = null,
        CancellationToken ct = default);

    Task<KeyVaultSecretLookupResult?> GetFirstDefaultTenantSecretValueAsync(
        IEnumerable<string> candidateKeys,
        string? author = null,
        CancellationToken ct = default);
}

/// <summary>
/// Storage-agnostic catalog access for consumers that own a secret-key prefix.
/// Key interpretation and configuration mapping remain the consumer's responsibility.
/// </summary>
public interface IKeyVaultSecretCatalogReader : IKeyVaultSecretReader
{
    Task<IReadOnlyList<KeyVaultSecretLookupResult>> GetDefaultTenantSecretValuesByPrefixAsync(
        string keyPrefix,
        string? author = null,
        CancellationToken ct = default);
}
