using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace GnOuGo.KeyVault.Core.Services;

/// <summary>
/// Native AOT-friendly encrypted record store backed by the shared KeyVault database.
/// </summary>
public sealed class KeyVaultRecordStore : IKeyVaultRecordStore
{
    private const string DefaultTenantName = "__default__";
    private const string DefaultAuthor = "GnOuGo.KeyVault.Core";

    private readonly string _databasePath;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private bool _initialized;

    public KeyVaultRecordStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public Task<KeyVaultRecordValue?> GetAsync(
        string collection,
        string tenantId,
        string key,
        string author,
        CancellationToken ct = default)
    {
        ValidateCoordinates(collection, tenantId, key, author);
        return ExecuteAsync(
            () => GetCoreAsync(collection, tenantId, key, author, ct),
            $"read record '{collection}/{key}' for tenant '{tenantId}'",
            ct);
    }

    public Task<KeyVaultRecordValue> UpsertAsync(
        string collection,
        string tenantId,
        string key,
        string value,
        string author,
        CancellationToken ct = default)
    {
        ValidateCoordinates(collection, tenantId, key, author);
        ArgumentNullException.ThrowIfNull(value);
        return ExecuteAsync(
            () => UpsertCoreAsync(collection, tenantId, key, value, author, ct),
            $"write record '{collection}/{key}' for tenant '{tenantId}'",
            ct);
    }

    public Task<IReadOnlyList<KeyVaultRecordValue>> ListAsync(
        string collection,
        string tenantId,
        string author,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        return ExecuteAsync(
            () => ListCoreAsync(collection, tenantId, author, ct),
            $"list records in '{collection}' for tenant '{tenantId}'",
            ct);
    }

    public Task<bool> DeleteAsync(
        string collection,
        string tenantId,
        string key,
        string author,
        CancellationToken ct = default)
    {
        ValidateCoordinates(collection, tenantId, key, author);
        return ExecuteAsync(
            () => DeleteCoreAsync(collection, tenantId, key, author, ct),
            $"delete record '{collection}/{key}' for tenant '{tenantId}'",
            ct);
    }

    private async Task<KeyVaultRecordValue?> GetCoreAsync(
        string collection,
        string tenantId,
        string key,
        string author,
        CancellationToken ct)
    {
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            var (_, privateKeyPem) = await EnsureDefaultKeyPairAsync(connection, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EncryptedValue, CreatedAtTicks, UpdatedAtTicks
                FROM KeyVaultRecords
                WHERE Collection = $collection AND TenantId = $tenantId AND RecordKey = $recordKey
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$collection", collection);
            command.Parameters.AddWithValue("$tenantId", tenantId);
            command.Parameters.AddWithValue("$recordKey", key);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var encryptedValue = reader.GetString(0);
            var createdAtTicks = reader.GetInt64(1);
            var updatedAtTicks = reader.GetInt64(2);
            await reader.DisposeAsync();

            var value = CryptoService.Decrypt(encryptedValue, privateKeyPem);
            await WriteAuditAsync(connection, collection, tenantId, key, "GetRecord", author, null, ct);
            return ToValue(collection, tenantId, key, value, createdAtTicks, updatedAtTicks);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<KeyVaultRecordValue> UpsertCoreAsync(
        string collection,
        string tenantId,
        string key,
        string value,
        string author,
        CancellationToken ct)
    {
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            var (publicKeyPem, _) = await EnsureDefaultKeyPairAsync(connection, ct);
            var encryptedValue = CryptoService.Encrypt(value, publicKeyPem);
            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;

            await using var transaction = connection.BeginTransaction();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO KeyVaultRecords
                        (Id, Collection, TenantId, RecordKey, EncryptedValue, CreatedAtTicks, UpdatedAtTicks, UpdatedBy)
                    VALUES
                        ($id, $collection, $tenantId, $recordKey, $encryptedValue, $createdAt, $updatedAt, $updatedBy)
                    ON CONFLICT(Collection, TenantId, RecordKey) DO UPDATE SET
                        EncryptedValue = excluded.EncryptedValue,
                        UpdatedAtTicks = excluded.UpdatedAtTicks,
                        UpdatedBy = excluded.UpdatedBy;
                    """;
                command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$tenantId", tenantId);
                command.Parameters.AddWithValue("$recordKey", key);
                command.Parameters.AddWithValue("$encryptedValue", encryptedValue);
                command.Parameters.AddWithValue("$createdAt", nowTicks);
                command.Parameters.AddWithValue("$updatedAt", nowTicks);
                command.Parameters.AddWithValue("$updatedBy", author);
                await command.ExecuteNonQueryAsync(ct);
            }

            var timestamps = await ReadTimestampsAsync(connection, transaction, collection, tenantId, key, ct);
            await WriteAuditAsync(
                connection,
                transaction,
                collection,
                tenantId,
                key,
                "UpsertRecord",
                author,
                null,
                ct);
            await transaction.CommitAsync(ct);

            return ToValue(collection, tenantId, key, value, timestamps.CreatedAtTicks, timestamps.UpdatedAtTicks);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<IReadOnlyList<KeyVaultRecordValue>> ListCoreAsync(
        string collection,
        string tenantId,
        string author,
        CancellationToken ct)
    {
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            var (_, privateKeyPem) = await EnsureDefaultKeyPairAsync(connection, ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT RecordKey, EncryptedValue, CreatedAtTicks, UpdatedAtTicks
                FROM KeyVaultRecords
                WHERE Collection = $collection AND TenantId = $tenantId
                ORDER BY RecordKey COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$collection", collection);
            command.Parameters.AddWithValue("$tenantId", tenantId);

            var encryptedRecords = new List<EncryptedRecord>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    encryptedRecords.Add(new EncryptedRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetInt64(3)));
                }
            }

            var records = encryptedRecords
                .Select(record => ToValue(
                    collection,
                    tenantId,
                    record.Key,
                    CryptoService.Decrypt(record.EncryptedValue, privateKeyPem),
                    record.CreatedAtTicks,
                    record.UpdatedAtTicks))
                .ToArray();
            await WriteAuditAsync(
                connection,
                collection,
                tenantId,
                recordKey: null,
                "ListRecords",
                author,
                $"Returned {records.Length} record(s)",
                ct);
            return records;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<bool> DeleteCoreAsync(
        string collection,
        string tenantId,
        string key,
        string author,
        CancellationToken ct)
    {
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = connection.BeginTransaction();
            int deleted;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM KeyVaultRecords
                    WHERE Collection = $collection AND TenantId = $tenantId AND RecordKey = $recordKey;
                    """;
                command.Parameters.AddWithValue("$collection", collection);
                command.Parameters.AddWithValue("$tenantId", tenantId);
                command.Parameters.AddWithValue("$recordKey", key);
                deleted = await command.ExecuteNonQueryAsync(ct);
            }

            if (deleted > 0)
            {
                await WriteAuditAsync(
                    connection,
                    transaction,
                    collection,
                    tenantId,
                    key,
                    "DeleteRecord",
                    author,
                    null,
                    ct);
            }

            await transaction.CommitAsync(ct);
            return deleted == 1;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath))
            ?? throw new InvalidOperationException("The KeyVault database directory could not be resolved.");
        Directory.CreateDirectory(directory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(ct);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        if (!_initialized)
        {
            await EnsureSchemaAsync(connection, ct);
            _initialized = true;
        }
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Tenants (
                Id TEXT NOT NULL CONSTRAINT PK_Tenants PRIMARY KEY,
                Name TEXT NOT NULL,
                PublicKeyPem TEXT NOT NULL,
                PrivateKeyPem TEXT NOT NULL,
                CreatedAtTicks INTEGER NOT NULL,
                CreatedBy TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Tenants_Name
                ON Tenants (Name) WHERE IsDeleted = 0;

            CREATE TABLE IF NOT EXISTS Secrets (
                Id TEXT NOT NULL CONSTRAINT PK_Secrets PRIMARY KEY,
                Key TEXT NOT NULL,
                TenantId TEXT NULL,
                IsDeleted INTEGER NOT NULL,
                CreatedAtTicks INTEGER NOT NULL,
                CreatedBy TEXT NOT NULL,
                CONSTRAINT FK_Secrets_Tenants_TenantId FOREIGN KEY (TenantId) REFERENCES Tenants (Id) ON DELETE SET NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Secrets_Key_TenantId
                ON Secrets (Key, TenantId) WHERE IsDeleted = 0;

            CREATE TABLE IF NOT EXISTS SecretVersions (
                Id TEXT NOT NULL CONSTRAINT PK_SecretVersions PRIMARY KEY,
                SecretId TEXT NOT NULL,
                Version INTEGER NOT NULL,
                EncryptedValue TEXT NOT NULL,
                CreatedAtTicks INTEGER NOT NULL,
                CreatedBy TEXT NOT NULL,
                CONSTRAINT FK_SecretVersions_Secrets_SecretId FOREIGN KEY (SecretId) REFERENCES Secrets (Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SecretVersions_SecretId_Version
                ON SecretVersions (SecretId, Version);

            CREATE TABLE IF NOT EXISTS AuditEntries (
                Id TEXT NOT NULL CONSTRAINT PK_AuditEntries PRIMARY KEY,
                TenantId TEXT NULL,
                SecretKey TEXT NULL,
                Operation TEXT NOT NULL,
                Author TEXT NOT NULL,
                TimestampTicks INTEGER NOT NULL,
                Details TEXT NULL,
                CONSTRAINT FK_AuditEntries_Tenants_TenantId FOREIGN KEY (TenantId) REFERENCES Tenants (Id)
            );
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_TenantId_TimestampTicks
                ON AuditEntries (TenantId, TimestampTicks);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_TimestampTicks
                ON AuditEntries (TimestampTicks);

            CREATE TABLE IF NOT EXISTS KeyVaultRecords (
                Id TEXT NOT NULL CONSTRAINT PK_KeyVaultRecords PRIMARY KEY,
                Collection TEXT NOT NULL,
                TenantId TEXT NOT NULL,
                RecordKey TEXT NOT NULL,
                EncryptedValue TEXT NOT NULL,
                CreatedAtTicks INTEGER NOT NULL,
                UpdatedAtTicks INTEGER NOT NULL,
                UpdatedBy TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_KeyVaultRecords_Collection_TenantId_RecordKey
                ON KeyVaultRecords (Collection, TenantId, RecordKey);
            CREATE INDEX IF NOT EXISTS IX_KeyVaultRecords_TenantId_UpdatedAtTicks
                ON KeyVaultRecords (TenantId, UpdatedAtTicks);

            CREATE TABLE IF NOT EXISTS KeyVaultRecordAuditEntries (
                Id TEXT NOT NULL CONSTRAINT PK_KeyVaultRecordAuditEntries PRIMARY KEY,
                Collection TEXT NOT NULL,
                TenantId TEXT NOT NULL,
                RecordKey TEXT NULL,
                Operation TEXT NOT NULL,
                Author TEXT NOT NULL,
                TimestampTicks INTEGER NOT NULL,
                Details TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_KeyVaultRecordAuditEntries_TenantId_TimestampTicks
                ON KeyVaultRecordAuditEntries (TenantId, TimestampTicks);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string PublicKeyPem, string PrivateKeyPem)> EnsureDefaultKeyPairAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var existing = await ReadDefaultKeyPairAsync(connection, ct);
        if (existing is not null)
            return existing.Value;

        var (publicKeyPem, privateKeyPem) = CryptoService.GenerateKeyPair();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO Tenants
                (Id, Name, PublicKeyPem, PrivateKeyPem, CreatedAtTicks, CreatedBy, IsDeleted)
            VALUES
                ($id, $name, $publicKeyPem, $privateKeyPem, $createdAtTicks, $createdBy, 0);
            """;
        command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        command.Parameters.AddWithValue("$name", DefaultTenantName);
        command.Parameters.AddWithValue("$publicKeyPem", publicKeyPem);
        command.Parameters.AddWithValue("$privateKeyPem", privateKeyPem);
        command.Parameters.AddWithValue("$createdAtTicks", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$createdBy", DefaultAuthor);
        await command.ExecuteNonQueryAsync(ct);

        return await ReadDefaultKeyPairAsync(connection, ct)
               ?? throw new InvalidOperationException("The default KeyVault key pair could not be initialized.");
    }

    private static async Task<(string PublicKeyPem, string PrivateKeyPem)?> ReadDefaultKeyPairAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PublicKeyPem, PrivateKeyPem
            FROM Tenants
            WHERE Name = $name AND IsDeleted = 0
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", DefaultTenantName);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<(long CreatedAtTicks, long UpdatedAtTicks)> ReadTimestampsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string collection,
        string tenantId,
        string key,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CreatedAtTicks, UpdatedAtTicks
            FROM KeyVaultRecords
            WHERE Collection = $collection AND TenantId = $tenantId AND RecordKey = $recordKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$tenantId", tenantId);
        command.Parameters.AddWithValue("$recordKey", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The KeyVault record could not be read after saving.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static Task WriteAuditAsync(
        SqliteConnection connection,
        string collection,
        string tenantId,
        string? recordKey,
        string operation,
        string author,
        string? details,
        CancellationToken ct)
        => WriteAuditAsync(connection, null, collection, tenantId, recordKey, operation, author, details, ct);

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string collection,
        string tenantId,
        string? recordKey,
        string operation,
        string author,
        string? details,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO KeyVaultRecordAuditEntries
                (Id, Collection, TenantId, RecordKey, Operation, Author, TimestampTicks, Details)
            VALUES
                ($id, $collection, $tenantId, $recordKey, $operation, $author, $timestampTicks, $details);
            """;
        command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$tenantId", tenantId);
        command.Parameters.AddWithValue("$recordKey", (object?)recordKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$author", author);
        command.Parameters.AddWithValue("$timestampTicks", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static KeyVaultRecordValue ToValue(
        string collection,
        string tenantId,
        string key,
        string value,
        long createdAtTicks,
        long updatedAtTicks)
        => new(
            collection,
            tenantId,
            key,
            value,
            new DateTimeOffset(createdAtTicks, TimeSpan.Zero),
            new DateTimeOffset(updatedAtTicks, TimeSpan.Zero));

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> action, string operation, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsAccessFailure(ex))
        {
            throw new KeyVaultAccessException($"KeyVault could not {operation}.", ex);
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

    private static void ValidateCoordinates(string collection, string tenantId, string key, string author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
    }

    private sealed record EncryptedRecord(
        string Key,
        string EncryptedValue,
        long CreatedAtTicks,
        long UpdatedAtTicks);
}
