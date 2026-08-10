namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Tenant-aware encrypted record storage for trusted components that need durable
/// state without depending on the KeyVault persistence implementation.
/// </summary>
public interface IKeyVaultRecordStore
{
    Task<KeyVaultRecordValue?> GetAsync(
        string collection,
        string tenantId,
        string key,
        string author,
        CancellationToken ct = default);

    Task<KeyVaultRecordValue> UpsertAsync(
        string collection,
        string tenantId,
        string key,
        string value,
        string author,
        CancellationToken ct = default);

    Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(
        string collection,
        string tenantId,
        string author,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(
        string collection,
        string tenantId,
        string key,
        string author,
        CancellationToken ct = default);
}

public sealed record KeyVaultRecordValue(
    string Collection,
    string TenantId,
    string Key,
    string Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

