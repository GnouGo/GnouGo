using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using GnOuGo.KeyVault.Core.Models;

namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// AOT-friendly read-only helper for trusted local tools that need to resolve
/// default-tenant secrets from the shared KeyVault SQLite database without
/// constructing the EF Core model in a Native AOT process.
/// </summary>
public sealed class KeyVaultSecretReader : IKeyVaultSecretCatalogReader
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

        try
        {
            return await GetDefaultTenantSecretValueCoreAsync(key, author, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            throw new KeyVaultAccessException($"KeyVault could not read secret '{key}'.", ex);
        }
    }

    private async Task<string?> GetDefaultTenantSecretValueCoreAsync(
        string key,
        string? author,
        CancellationToken ct)
    {
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

    public async Task<IReadOnlyList<KeyVaultSecretLookupResult>> GetDefaultTenantSecretValuesByPrefixAsync(
        string keyPrefix,
        string? author = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        try
        {
            return await GetDefaultTenantSecretValuesByPrefixCoreAsync(keyPrefix, author, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            throw new KeyVaultAccessException(
                $"KeyVault could not read secrets with prefix '{keyPrefix}'.",
                ex);
        }
    }

    private async Task<IReadOnlyList<KeyVaultSecretLookupResult>> GetDefaultTenantSecretValuesByPrefixCoreAsync(
        string keyPrefix,
        string? author,
        CancellationToken ct)
    {
        if (!File.Exists(_databasePath))
            return [];

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Key, sv.EncryptedValue, dt.PrivateKeyPem, sv.Version
            FROM Secrets AS s
            INNER JOIN SecretVersions AS sv ON sv.SecretId = s.Id
            INNER JOIN Tenants AS dt ON dt.Name = '__default__' AND dt.IsDeleted = 0
            WHERE substr(s.Key, 1, length($keyPrefix)) = $keyPrefix COLLATE NOCASE
              AND s.TenantId IS NULL
              AND s.IsDeleted = 0
              AND sv.Version = (
                  SELECT MAX(latest.Version)
                  FROM SecretVersions AS latest
                  WHERE latest.SecretId = s.Id
              )
            ORDER BY s.Key COLLATE NOCASE, s.Key;
            """;
        command.Parameters.AddWithValue("$keyPrefix", keyPrefix);

        var encryptedRows = new List<EncryptedCatalogSecretRow>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                encryptedRows.Add(new EncryptedCatalogSecretRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3)));
            }
        }

        var results = encryptedRows
            .Select(row => new KeyVaultSecretLookupResult(
                row.Key,
                CryptoService.Decrypt(row.EncryptedValue, row.PrivateKeyPem)))
            .ToArray();

        foreach (var row in encryptedRows)
            await TryWriteAuditEntryAsync(connection, row.Key, author, row.Version, ct);

        return results;
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

    private static bool IsAccessFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or FormatException
            or SqliteException
            or CryptographicException;

    private readonly record struct EncryptedSecretRow(string EncryptedValue, string PrivateKeyPem, int Version);
    private readonly record struct EncryptedCatalogSecretRow(
        string Key,
        string EncryptedValue,
        string PrivateKeyPem,
        int Version);
}

public sealed record KeyVaultSecretLookupResult(string Key, string Value);


