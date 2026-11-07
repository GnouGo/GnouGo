using System.Text.Json.Serialization;

namespace GnOuGo.KeyVault.Core.Services;

/// <summary>Optional bounded inspection capability. Ordinary record/replay contracts remain unchanged.</summary>
public interface IKeyVaultRecordReader
{
    Task<KeyVaultRecordMetadata?> GetMetadataAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default);
    Task<KeyVaultRecordMetadataPage> QueryMetadataAsync(string collection, string tenantId, string prefix, string author,
        KeyVaultRecordCursor? after = null, int count = 100, CancellationToken ct = default);
    Task<KeyVaultRecordRange?> ReadRangeAsync(string collection, string tenantId, string key, string author,
        string? version, int offset, int count, CancellationToken ct = default);
}

// UTC ticks exceed JavaScript's exact integer range. Cursors must survive browser round trips unchanged.
public sealed record KeyVaultRecordCursor(
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long UpdatedAtTicks, string Key);
public sealed record KeyVaultRecordMetadata(string Key, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Version, int? Characters);
public sealed record KeyVaultRecordMetadataPage(IReadOnlyList<KeyVaultRecordMetadata> Records, KeyVaultRecordCursor? Next, bool HasMore);
public sealed record KeyVaultRecordRange(string Version, int Offset, int NextOffset, int TotalCharacters, string Text)
{
    public bool Complete => NextOffset == TotalCharacters;
}
public sealed class KeyVaultRecordVersionException() : InvalidOperationException("The record changed; reload its document metadata before continuing.");
