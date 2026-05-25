using Microsoft.Data.Sqlite;
using GnOuGo.KeyVault.Core.Models;

namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// AOT-friendly read-only helper for trusted local tools that need to resolve
/// default-tenant secrets from the shared KeyVault SQLite database without
/// constructing the EF Core model in a Native AOT process.
/// </summary>
public sealed class KeyVaultSecretReader
{
    private const string DefaultAuthor = "GnOuGo.KeyVault.Core";
    private readonly string _databasePath;

    public KeyVaultSecretReader(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public async Task<string?> GetDefaultTenantSecretValueAsync(
        string key,
        string? author = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!File.Exists(_databasePath))
            return null;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        var row = await ReadLatestDefaultTenantSecretAsync(connection, key, ct);
        if (row is null)
            return null;

        var value = CryptoService.Decrypt(row.Value.EncryptedValue, row.Value.PrivateKeyPem);
        await TryWriteAuditEntryAsync(connection, key, author, row.Value.Version, ct);
        return value;
    }

    public async Task<KeyVaultSecretLookupResult?> GetFirstDefaultTenantSecretValueAsync(
        IEnumerable<string> candidateKeys,
        string? author = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidateKeys);

        foreach (var key in candidateKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var value = await GetDefaultTenantSecretValueAsync(key, author, ct);
            if (value is not null)
                return new KeyVaultSecretLookupResult(key, value);
        }

        return null;
    }

    private static async Task<EncryptedSecretRow?> ReadLatestDefaultTenantSecretAsync(
        SqliteConnection connection,
        string key,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sv.EncryptedValue, dt.PrivateKeyPem, sv.Version
            FROM Secrets AS s
            INNER JOIN SecretVersions AS sv ON sv.SecretId = s.Id
            INNER JOIN Tenants AS dt ON dt.Name = '__default__' AND dt.IsDeleted = 0
            WHERE s.Key = $key COLLATE NOCASE
              AND s.TenantId IS NULL
              AND s.IsDeleted = 0
            ORDER BY sv.Version DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new EncryptedSecretRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2));
    }

    private static async Task TryWriteAuditEntryAsync(
        SqliteConnection connection,
        string key,
        string? author,
        int version,
        CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO AuditEntries (Id, TenantId, SecretKey, Operation, Author, TimestampTicks, Details)
                VALUES ($id, NULL, $secretKey, $operation, $author, $timestampTicks, $details);
                """;
            command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
            command.Parameters.AddWithValue("$secretKey", key);
            command.Parameters.AddWithValue("$operation", AuditOperation.GetSecret.ToString());
            command.Parameters.AddWithValue("$author", string.IsNullOrWhiteSpace(author) ? DefaultAuthor : author);
            command.Parameters.AddWithValue("$timestampTicks", DateTimeOffset.UtcNow.UtcTicks);
            command.Parameters.AddWithValue("$details", $"Read version {version}");
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException)
        {
            // Reading a secret should still work if audit writing is blocked by a
            // read-only filesystem, a transient lock, or an older database shape.
        }
    }

    private readonly record struct EncryptedSecretRow(string EncryptedValue, string PrivateKeyPem, int Version);
}

public sealed record KeyVaultSecretLookupResult(string Key, string Value);


