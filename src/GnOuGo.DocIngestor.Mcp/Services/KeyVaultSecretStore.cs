using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GnOuGo.DocIngestor.Mcp.Services;

public sealed class KeyVaultSecretStore
{
    private const string DefaultTenantName = "__default__";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _databasePath;
    private string? _defaultPrivatePem;

    public KeyVaultSecretStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            "CREATE TABLE IF NOT EXISTS Tenants (" +
            "Id TEXT NOT NULL CONSTRAINT PK_Tenants PRIMARY KEY," +
            "Name TEXT NOT NULL," +
            "PublicKeyPem TEXT NOT NULL," +
            "PrivateKeyPem TEXT NOT NULL," +
            "CreatedAtTicks INTEGER NOT NULL," +
            "CreatedBy TEXT NOT NULL," +
            "IsDeleted INTEGER NOT NULL" +
            ");" +
            "CREATE TABLE IF NOT EXISTS Secrets (" +
            "Id TEXT NOT NULL CONSTRAINT PK_Secrets PRIMARY KEY," +
            "Key TEXT NOT NULL," +
            "TenantId TEXT NULL," +
            "IsDeleted INTEGER NOT NULL," +
            "CreatedAtTicks INTEGER NOT NULL," +
            "CreatedBy TEXT NOT NULL," +
            "CONSTRAINT FK_Secrets_Tenants_TenantId FOREIGN KEY (TenantId) REFERENCES Tenants (Id) ON DELETE SET NULL" +
            ");" +
            "CREATE TABLE IF NOT EXISTS SecretVersions (" +
            "Id TEXT NOT NULL CONSTRAINT PK_SecretVersions PRIMARY KEY," +
            "SecretId TEXT NOT NULL," +
            "Version INTEGER NOT NULL," +
            "EncryptedValue TEXT NOT NULL," +
            "CreatedAtTicks INTEGER NOT NULL," +
            "CreatedBy TEXT NOT NULL," +
            "CONSTRAINT FK_SecretVersions_Secrets_SecretId FOREIGN KEY (SecretId) REFERENCES Secrets (Id) ON DELETE CASCADE" +
            ");" +
            "CREATE TABLE IF NOT EXISTS AuditEntries (" +
            "Id TEXT NOT NULL CONSTRAINT PK_AuditEntries PRIMARY KEY," +
            "TenantId TEXT NULL," +
            "SecretKey TEXT NULL," +
            "Operation TEXT NOT NULL," +
            "Author TEXT NOT NULL," +
            "TimestampTicks INTEGER NOT NULL," +
            "Details TEXT NULL" +
            ");" +
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Tenants_Name ON Tenants (Name) WHERE IsDeleted = 0;" +
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Secrets_Key_TenantId ON Secrets (Key, TenantId) WHERE IsDeleted = 0;" +
            "CREATE INDEX IF NOT EXISTS IX_Secrets_TenantId ON Secrets (TenantId);" +
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_SecretVersions_SecretId_Version ON SecretVersions (SecretId, Version);" +
            "CREATE INDEX IF NOT EXISTS IX_AuditEntries_TenantId_TimestampTicks ON AuditEntries (TenantId, TimestampTicks);" +
            "CREATE INDEX IF NOT EXISTS IX_AuditEntries_TimestampTicks ON AuditEntries (TimestampTicks);";
        await cmd.ExecuteNonQueryAsync(ct);

        await EnsureDefaultKeyPairAsync(conn, ct);
    }

    public async Task<string?> GetSecretValueAsync(string key, Guid? tenantId, string author, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var conn = Open();
        var secret = await LoadLatestSecretAsync(conn, key, tenantId, ct);
        if (secret is null)
            return null;

        var privatePem = await GetPrivateKeyPemAsync(conn, tenantId, ct);
        var decrypted = Decrypt(secret.EncryptedValue, privatePem);
        await InsertAuditAsync(conn, tenantId, key, "GetSecret", author, $"Read version {secret.Version}", ct);
        return decrypted;
    }

    private async Task EnsureDefaultKeyPairAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT PrivateKeyPem FROM Tenants WHERE Name = $name AND IsDeleted = 0 LIMIT 1;";
            select.Parameters.AddWithValue("$name", DefaultTenantName);
            var existing = await select.ExecuteScalarAsync(ct) as string;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                _defaultPrivatePem = existing;
                return;
            }
        }

        var (publicPem, privatePem) = GenerateKeyPair();
        await using var insert = conn.CreateCommand();
        insert.CommandText =
            "INSERT INTO Tenants(Id, Name, PublicKeyPem, PrivateKeyPem, CreatedAtTicks, CreatedBy, IsDeleted) " +
            "VALUES ($id, $name, $public, $private, $created, $createdBy, 0);";
        insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        insert.Parameters.AddWithValue("$name", DefaultTenantName);
        insert.Parameters.AddWithValue("$public", publicPem);
        insert.Parameters.AddWithValue("$private", privatePem);
        insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.UtcTicks);
        insert.Parameters.AddWithValue("$createdBy", "system");
        await insert.ExecuteNonQueryAsync(ct);
        _defaultPrivatePem = privatePem;
    }

    private async Task<string> GetPrivateKeyPemAsync(SqliteConnection conn, Guid? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            if (!string.IsNullOrWhiteSpace(_defaultPrivatePem))
                return _defaultPrivatePem!;

            await EnsureDefaultKeyPairAsync(conn, ct);
            return _defaultPrivatePem!;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PrivateKeyPem FROM Tenants WHERE Id = $id AND IsDeleted = 0 LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", tenantId.Value.ToString());
        var value = await cmd.ExecuteScalarAsync(ct) as string;
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Tenant '{tenantId}' was not found in KeyVault.");
    }

    private static async Task<StoredSecretVersion?> LoadLatestSecretAsync(SqliteConnection conn, string key, Guid? tenantId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT s.Id, v.Version, v.EncryptedValue, v.CreatedAtTicks " +
            "FROM Secrets s " +
            "JOIN SecretVersions v ON v.SecretId = s.Id " +
            "WHERE s.Key = $key AND s.IsDeleted = 0 AND " +
            (tenantId.HasValue ? "s.TenantId = $tenantId " : "s.TenantId IS NULL ") +
            "ORDER BY v.Version DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        if (tenantId.HasValue)
            cmd.Parameters.AddWithValue("$tenantId", tenantId.Value.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new StoredSecretVersion(
            reader.GetInt32(1),
            reader.GetString(2));
    }

    private static async Task InsertAuditAsync(SqliteConnection conn, Guid? tenantId, string secretKey, string operation, string author, string details, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO AuditEntries(Id, TenantId, SecretKey, Operation, Author, TimestampTicks, Details) " +
            "VALUES ($id, $tenantId, $secretKey, $operation, $author, $timestamp, $details);";
        cmd.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        cmd.Parameters.AddWithValue("$tenantId", tenantId.HasValue ? tenantId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$secretKey", secretKey);
        cmd.Parameters.AddWithValue("$operation", operation);
        cmd.Parameters.AddWithValue("$author", author);
        cmd.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.UtcTicks);
        cmd.Parameters.AddWithValue("$details", details);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private static (string PublicPem, string PrivatePem) GenerateKeyPair(int keySizeInBits = 2048)
    {
        using var rsa = RSA.Create(keySizeInBits);
        return (rsa.ExportRSAPublicKeyPem(), rsa.ExportRSAPrivateKeyPem());
    }

    private static string Decrypt(string base64Encrypted, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var data = Convert.FromBase64String(base64Encrypted);
        var offset = 0;
        var encryptedKeyLength = BitConverter.ToUInt16(data, offset);
        offset += 2;

        var encryptedAesKey = data.AsSpan(offset, encryptedKeyLength);
        offset += encryptedKeyLength;
        var aesKey = rsa.Decrypt(encryptedAesKey.ToArray(), RSAEncryptionPadding.OaepSHA256);

        var nonce = data.AsSpan(offset, NonceSize);
        offset += NonceSize;
        var tag = data.AsSpan(offset, TagSize);
        offset += TagSize;
        var ciphertext = data.AsSpan(offset);
        var plainBytes = new byte[ciphertext.Length];

        using (var aes = new AesGcm(aesKey, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plainBytes);
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    private sealed record StoredSecretVersion(int Version, string EncryptedValue);
}

