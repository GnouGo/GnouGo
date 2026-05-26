using System.Globalization;
using Microsoft.Data.Sqlite;
using GnOuGo.KeyVault.Core.Models;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.KeyVault.Mcp;

/// <summary>
/// NativeAOT-friendly KeyVault persistence using explicit SQLite commands.
/// This keeps the MCP executable away from EF Core runtime model building.
/// </summary>
public sealed class KeyVaultSqliteStore
{
    private const string DefaultTenantName = "__default__";
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public KeyVaultSqliteStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await ExecuteAsync(connection, SchemaSql, ct);
        await EnsureDefaultKeyPairAsync(connection, null, ct);
    }

    public async Task<TenantDto> CreateTenantAsync(string name, string author, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var (publicPem, privatePem) = CryptoService.GenerateKeyPair();
            var tenant = new TenantDto(Guid.CreateVersion7(), name, DateTimeOffset.UtcNow, author);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO Tenants (Id, Name, PublicKeyPem, PrivateKeyPem, CreatedAtTicks, CreatedBy, IsDeleted)
                    VALUES ($id, $name, $publicKeyPem, $privateKeyPem, $createdAtTicks, $createdBy, 0);
                    """;
                command.Parameters.AddWithValue("$id", FormatGuid(tenant.Id));
                command.Parameters.AddWithValue("$name", tenant.Name);
                command.Parameters.AddWithValue("$publicKeyPem", publicPem);
                command.Parameters.AddWithValue("$privateKeyPem", privatePem);
                command.Parameters.AddWithValue("$createdAtTicks", tenant.CreatedAt.UtcTicks);
                command.Parameters.AddWithValue("$createdBy", tenant.CreatedBy);
                await command.ExecuteNonQueryAsync(ct);
            }

            await WriteAuditEntryAsync(connection, transaction, null, null, AuditOperation.CreateTenant, author, $"Created tenant '{name}'", ct);
            await transaction.CommitAsync(ct);
            return tenant;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<TenantDto>> ListTenantsAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, CreatedAtTicks, CreatedBy
            FROM Tenants
            WHERE IsDeleted = 0 AND Name <> $defaultTenantName
            ORDER BY Name;
            """;
        command.Parameters.AddWithValue("$defaultTenantName", DefaultTenantName);

        var tenants = new List<TenantDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tenants.Add(new TenantDto(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                FromTicks(reader.GetInt64(2)),
                reader.GetString(3)));
        }

        return tenants;
    }

    public async Task<KeyVaultSecretMetadataResult> SetSecretAsync(string key, string value, Guid? tenantId, string author, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var (publicPem, _) = await GetKeyPairAsync(connection, transaction, tenantId, ct);
            var encrypted = CryptoService.Encrypt(value, publicPem);

            var secret = await FindSecretAsync(connection, transaction, key, tenantId, ct);
            if (secret is null)
            {
                secret = new SecretRow(Guid.CreateVersion7(), key, tenantId, DateTimeOffset.UtcNow.UtcTicks, author);
                await InsertSecretAsync(connection, transaction, secret.Value, ct);
            }

            var nextVersion = await GetNextVersionAsync(connection, transaction, secret.Value.Id, ct);
            var createdAt = DateTimeOffset.UtcNow;
            await InsertSecretVersionAsync(connection, transaction, secret.Value.Id, nextVersion, encrypted, createdAt.UtcTicks, author, ct);
            await WriteAuditEntryAsync(connection, transaction, tenantId, key, AuditOperation.SetSecret, author, $"Set version {nextVersion}", ct);
            await transaction.CommitAsync(ct);

            return new KeyVaultSecretMetadataResult(secret.Value.Id, key, nextVersion, tenantId, createdAt);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<SecretDto>> ListSecretsAsync(Guid? tenantId, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.Key, s.TenantId, t.Name, COALESCE(MAX(v.Version), 0), s.CreatedAtTicks, s.CreatedBy
            FROM Secrets AS s
            LEFT JOIN Tenants AS t ON t.Id = s.TenantId AND t.IsDeleted = 0
            LEFT JOIN SecretVersions AS v ON v.SecretId = s.Id
            WHERE s.IsDeleted = 0
              AND (($tenantId IS NULL AND s.TenantId IS NULL) OR s.TenantId = $tenantId)
            GROUP BY s.Id, s.Key, s.TenantId, t.Name, s.CreatedAtTicks, s.CreatedBy
            ORDER BY s.Key;
            """;
        AddNullableGuidParameter(command, "$tenantId", tenantId);

        var secrets = new List<SecretDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            secrets.Add(new SecretDto(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ReadNullableGuid(reader, 2),
                ReadNullableString(reader, 3),
                reader.GetInt32(4),
                FromTicks(reader.GetInt64(5)),
                reader.GetString(6)));
        }

        return secrets;
    }

    public async Task<KeyVaultSecretValueResult?> GetSecretAsync(string key, Guid? tenantId, string author, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            var secret = await ReadLatestSecretVersionAsync(connection, transaction, key, tenantId, ct);
            if (secret is null)
                return null;

            var (_, privatePem) = await GetKeyPairAsync(connection, transaction, tenantId, ct);
            var decrypted = CryptoService.Decrypt(secret.Value.EncryptedValue, privatePem);
            await WriteAuditEntryAsync(connection, transaction, tenantId, key, AuditOperation.GetSecret, author, $"Read version {secret.Value.Version}", ct);
            await transaction.CommitAsync(ct);

            return new KeyVaultSecretValueResult(
                secret.Value.SecretId,
                key,
                decrypted,
                secret.Value.Version,
                tenantId,
                FromTicks(secret.Value.CreatedAtTicks));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteSecretAsync(string key, Guid? tenantId, string author, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var connection = await OpenConnectionAsync(ct);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE Secrets
                SET IsDeleted = 1
                WHERE Key = $key
                  AND IsDeleted = 0
                  AND (($tenantId IS NULL AND TenantId IS NULL) OR TenantId = $tenantId);
                """;
            command.Parameters.AddWithValue("$key", key);
            AddNullableGuidParameter(command, "$tenantId", tenantId);
            var rows = await command.ExecuteNonQueryAsync(ct);
            if (rows == 0)
                return false;

            await WriteAuditEntryAsync(connection, transaction, tenantId, key, AuditOperation.DeleteSecret, author, null, ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task EnsureDefaultKeyPairAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT 1 FROM Tenants WHERE Name = $name AND IsDeleted = 0 LIMIT 1;";
        select.Parameters.AddWithValue("$name", DefaultTenantName);
        if (await select.ExecuteScalarAsync(ct) is not null)
            return;

        var (publicPem, privatePem) = CryptoService.GenerateKeyPair();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO Tenants (Id, Name, PublicKeyPem, PrivateKeyPem, CreatedAtTicks, CreatedBy, IsDeleted)
            VALUES ($id, $name, $publicKeyPem, $privateKeyPem, $createdAtTicks, $createdBy, 0);
            """;
        insert.Parameters.AddWithValue("$id", FormatGuid(Guid.CreateVersion7()));
        insert.Parameters.AddWithValue("$name", DefaultTenantName);
        insert.Parameters.AddWithValue("$publicKeyPem", publicPem);
        insert.Parameters.AddWithValue("$privateKeyPem", privatePem);
        insert.Parameters.AddWithValue("$createdAtTicks", DateTimeOffset.UtcNow.UtcTicks);
        insert.Parameters.AddWithValue("$createdBy", "system");
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string PublicPem, string PrivatePem)> GetKeyPairAsync(SqliteConnection connection, SqliteTransaction transaction, Guid? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            await EnsureDefaultKeyPairAsync(connection, transaction, ct);
            return await ReadKeyPairByDefaultTenantAsync(connection, transaction, ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT PublicKeyPem, PrivateKeyPem
            FROM Tenants
            WHERE Id = $id AND IsDeleted = 0
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(tenantId.Value));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"Tenant '{tenantId}' not found or has been deleted.");

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<(string PublicPem, string PrivatePem)> ReadKeyPairByDefaultTenantAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT PublicKeyPem, PrivateKeyPem
            FROM Tenants
            WHERE Name = $name AND IsDeleted = 0
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", DefaultTenantName);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Default KeyVault tenant key pair could not be initialized.");

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<SecretRow?> FindSecretAsync(SqliteConnection connection, SqliteTransaction transaction, string key, Guid? tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, Key, TenantId, CreatedAtTicks, CreatedBy
            FROM Secrets
            WHERE Key = $key
              AND IsDeleted = 0
              AND (($tenantId IS NULL AND TenantId IS NULL) OR TenantId = $tenantId)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);
        AddNullableGuidParameter(command, "$tenantId", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new SecretRow(Guid.Parse(reader.GetString(0)), reader.GetString(1), ReadNullableGuid(reader, 2), reader.GetInt64(3), reader.GetString(4))
            : null;
    }

    private static async Task InsertSecretAsync(SqliteConnection connection, SqliteTransaction transaction, SecretRow secret, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Secrets (Id, Key, TenantId, IsDeleted, CreatedAtTicks, CreatedBy)
            VALUES ($id, $key, $tenantId, 0, $createdAtTicks, $createdBy);
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(secret.Id));
        command.Parameters.AddWithValue("$key", secret.Key);
        AddNullableGuidParameter(command, "$tenantId", secret.TenantId);
        command.Parameters.AddWithValue("$createdAtTicks", secret.CreatedAtTicks);
        command.Parameters.AddWithValue("$createdBy", secret.CreatedBy);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetNextVersionAsync(SqliteConnection connection, SqliteTransaction transaction, Guid secretId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) + 1 FROM SecretVersions WHERE SecretId = $secretId;";
        command.Parameters.AddWithValue("$secretId", FormatGuid(secretId));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task InsertSecretVersionAsync(SqliteConnection connection, SqliteTransaction transaction, Guid secretId, int version, string encryptedValue, long createdAtTicks, string author, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SecretVersions (Id, SecretId, Version, EncryptedValue, CreatedAtTicks, CreatedBy)
            VALUES ($id, $secretId, $version, $encryptedValue, $createdAtTicks, $createdBy);
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(Guid.CreateVersion7()));
        command.Parameters.AddWithValue("$secretId", FormatGuid(secretId));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$encryptedValue", encryptedValue);
        command.Parameters.AddWithValue("$createdAtTicks", createdAtTicks);
        command.Parameters.AddWithValue("$createdBy", author);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<LatestSecretVersionRow?> ReadLatestSecretVersionAsync(SqliteConnection connection, SqliteTransaction transaction, string key, Guid? tenantId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.Id, v.Version, v.EncryptedValue, v.CreatedAtTicks
            FROM Secrets AS s
            INNER JOIN SecretVersions AS v ON v.SecretId = s.Id
            WHERE s.Key = $key
              AND s.IsDeleted = 0
              AND (($tenantId IS NULL AND s.TenantId IS NULL) OR s.TenantId = $tenantId)
            ORDER BY v.Version DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", key);
        AddNullableGuidParameter(command, "$tenantId", tenantId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new LatestSecretVersionRow(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3))
            : null;
    }

    private static async Task WriteAuditEntryAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid? tenantId, string? secretKey, AuditOperation operation, string author, string? details, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AuditEntries (Id, TenantId, SecretKey, Operation, Author, TimestampTicks, Details)
            VALUES ($id, $tenantId, $secretKey, $operation, $author, $timestampTicks, $details);
            """;
        command.Parameters.AddWithValue("$id", FormatGuid(Guid.CreateVersion7()));
        AddNullableGuidParameter(command, "$tenantId", tenantId);
        command.Parameters.AddWithValue("$secretKey", secretKey is null ? DBNull.Value : secretKey);
        command.Parameters.AddWithValue("$operation", operation.ToString());
        command.Parameters.AddWithValue("$author", author);
        command.Parameters.AddWithValue("$timestampTicks", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$details", details is null ? DBNull.Value : details);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddNullableGuidParameter(SqliteCommand command, string name, Guid? value)
        => command.Parameters.AddWithValue(name, value.HasValue ? FormatGuid(value.Value) : DBNull.Value);

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string FormatGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    private readonly record struct SecretRow(Guid Id, string Key, Guid? TenantId, long CreatedAtTicks, string CreatedBy);

    private readonly record struct LatestSecretVersionRow(Guid SecretId, int Version, string EncryptedValue, long CreatedAtTicks);

    private const string SchemaSql = """
        PRAGMA foreign_keys = ON;
        PRAGMA journal_mode = WAL;

        CREATE TABLE IF NOT EXISTS Tenants (
            Id TEXT NOT NULL PRIMARY KEY,
            Name TEXT NOT NULL,
            PublicKeyPem TEXT NOT NULL,
            PrivateKeyPem TEXT NOT NULL,
            CreatedAtTicks INTEGER NOT NULL,
            CreatedBy TEXT NOT NULL,
            IsDeleted INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Tenants_Name_Active ON Tenants (Name) WHERE IsDeleted = 0;

        CREATE TABLE IF NOT EXISTS Secrets (
            Id TEXT NOT NULL PRIMARY KEY,
            Key TEXT NOT NULL,
            TenantId TEXT NULL,
            IsDeleted INTEGER NOT NULL DEFAULT 0,
            CreatedAtTicks INTEGER NOT NULL,
            CreatedBy TEXT NOT NULL,
            FOREIGN KEY (TenantId) REFERENCES Tenants (Id) ON DELETE SET NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Secrets_Key_TenantId_Active ON Secrets (Key, TenantId) WHERE IsDeleted = 0 AND TenantId IS NOT NULL;
        CREATE UNIQUE INDEX IF NOT EXISTS IX_Secrets_Key_Default_Active ON Secrets (Key) WHERE IsDeleted = 0 AND TenantId IS NULL;

        CREATE TABLE IF NOT EXISTS SecretVersions (
            Id TEXT NOT NULL PRIMARY KEY,
            SecretId TEXT NOT NULL,
            Version INTEGER NOT NULL,
            EncryptedValue TEXT NOT NULL,
            CreatedAtTicks INTEGER NOT NULL,
            CreatedBy TEXT NOT NULL,
            FOREIGN KEY (SecretId) REFERENCES Secrets (Id) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_SecretVersions_SecretId_Version ON SecretVersions (SecretId, Version);

        CREATE TABLE IF NOT EXISTS AuditEntries (
            Id TEXT NOT NULL PRIMARY KEY,
            TenantId TEXT NULL,
            SecretKey TEXT NULL,
            Operation TEXT NOT NULL,
            Author TEXT NOT NULL,
            TimestampTicks INTEGER NOT NULL,
            Details TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_AuditEntries_TenantId_TimestampTicks ON AuditEntries (TenantId, TimestampTicks);
        CREATE INDEX IF NOT EXISTS IX_AuditEntries_TimestampTicks ON AuditEntries (TimestampTicks);
        """;
}


