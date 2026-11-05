using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace GnOuGo.KeyVault.Core.Services;

public sealed partial class KeyVaultRecordStore
{
    private const int PayloadChunkCharacters = 32768;
    private static string VersionOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Scope(string collection, string tenant, string key) => VersionOf(
        collection.Length.ToString(CultureInfo.InvariantCulture) + ":" + collection +
        tenant.Length.ToString(CultureInfo.InvariantCulture) + ":" + tenant + key.Length.ToString(CultureInfo.InvariantCulture) + ":" + key);
    private static string PayloadHeader(string text, string collection, string tenant, string key) => $"KeyVault-pages-v1\n{Scope(collection, tenant, key)}\n{VersionOf(text)}\n{text.Length.ToString(CultureInfo.InvariantCulture)}";

    public async Task<KeyVaultRecordMetadata?> GetMetadataAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
    {
        ValidateCoordinates(collection, tenantId, key, author);
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = MetadataCommand(connection, collection, tenantId);
            command.CommandText += " AND r.RecordKey=$key LIMIT 1;";
            command.Parameters.AddWithValue("$key", key);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? Metadata(reader) : null;
        }
        finally { _databaseGate.Release(); }
    }

    public async Task<KeyVaultRecordMetadataPage> QueryMetadataAsync(string collection, string tenantId, string prefix, string author,
        KeyVaultRecordCursor? after = null, int count = 100, CancellationToken ct = default)
    {
        ValidateCoordinates(collection, tenantId, "metadata", author);
        ArgumentNullException.ThrowIfNull(prefix);
        if (count is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(count));
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = MetadataCommand(connection, collection, tenantId);
            var upper = PrefixSuccessor(prefix);
            command.CommandText += " AND r.RecordKey >= $prefix COLLATE BINARY";
            if (upper is not null)
            {
                command.CommandText += " AND r.RecordKey < $upper COLLATE BINARY";
                command.Parameters.AddWithValue("$upper", upper);
            }
            command.CommandText += """
                 AND substr(r.RecordKey,1,length($prefix))=$prefix COLLATE BINARY
                 AND (r.UpdatedAtTicks>$ticks OR (r.UpdatedAtTicks=$ticks AND r.RecordKey>$key COLLATE BINARY))
                 ORDER BY r.UpdatedAtTicks,r.RecordKey COLLATE BINARY LIMIT $count;
                """;
            command.Parameters.AddWithValue("$prefix", prefix);
            command.Parameters.AddWithValue("$ticks", after?.UpdatedAtTicks ?? 0);
            command.Parameters.AddWithValue("$key", after?.Key ?? "");
            command.Parameters.AddWithValue("$count", count + 1);
            var rows = new List<KeyVaultRecordMetadata>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) rows.Add(Metadata(reader));
            var more = rows.Count > count;
            if (more) rows.RemoveAt(count);
            await WriteAuditAsync(connection, collection, tenantId, null, "QueryRecordMetadata", author, null, ct);
            return new(rows, rows.Count == 0 ? after : new(rows[^1].UpdatedAt.UtcTicks, rows[^1].Key), more);
        }
        finally { _databaseGate.Release(); }
    }

    public async Task<KeyVaultRecordRange?> ReadRangeAsync(string collection, string tenantId, string key, string author,
        string? version, int offset, int count, CancellationToken ct = default)
    {
        ValidateCoordinates(collection, tenantId, key, author);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count is < 2 or > 65536) throw new ArgumentOutOfRangeException(nameof(count));
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            var (pub, priv) = await EnsureDefaultKeyPairAsync(connection, ct);
            var row = await PayloadRowAsync(connection, collection, tenantId, key, ct);
            if (row is null) return null;
            if (row.Value.Version is null)
            {
                // Explicit historical access only. The logical record and its timestamps are unchanged.
                var text = CryptoService.Decrypt(row.Value.Encrypted, priv);
                await using var transaction = connection.BeginTransaction();
                await WritePayloadAsync(connection, transaction, collection, tenantId, key, text, pub, ct);
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE KeyVaultRecords SET EncryptedValue=$value WHERE Id=$id;";
                update.Parameters.AddWithValue("$id", row.Value.Id);
                update.Parameters.AddWithValue("$value", CryptoService.Encrypt(PayloadHeader(text, collection, tenantId, key), pub));
                await update.ExecuteNonQueryAsync(ct);
                await WriteAuditAsync(connection, transaction, collection, tenantId, key, "PrepareRecordPages", author, null, ct);
                await transaction.CommitAsync(ct);
                row = await PayloadRowAsync(connection, collection, tenantId, key, ct);
            }
            var value = row!.Value;
            VerifyHeader(value, priv);
            if (version is not null && version != value.Version) throw new KeyVaultRecordVersionException();
            if (offset > value.Characters) throw new ArgumentOutOfRangeException(nameof(offset));
            var end = (int)Math.Min((long)offset + count, value.Characters);
            var textPage = await ReadChunksAsync(connection, value, priv, Math.Max(0, offset - 1), Math.Min(value.Characters, end + 1), ct);
            var startInPage = offset == 0 ? 0 : 1;
            if (offset < value.Characters && char.IsLowSurrogate(textPage[startInPage]))
                throw new ArgumentException("The offset splits a Unicode scalar.", nameof(offset));
            if (end < value.Characters && char.IsLowSurrogate(textPage[startInPage + end - offset])) end--;
            await WriteAuditAsync(connection, collection, tenantId, key, "ReadRecordRange", author, null, ct);
            return new(value.Version!, offset, end, value.Characters, textPage.Substring(startInPage, end - offset));
        }
        finally { _databaseGate.Release(); }
    }

    private static SqliteCommand MetadataCommand(SqliteConnection connection, string collection, string tenant)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.RecordKey,r.CreatedAtTicks,r.UpdatedAtTicks,p.Version,p.Characters
            FROM KeyVaultRecords r INDEXED BY IX_KeyVaultRecords_Collection_TenantId_RecordKey
            LEFT JOIN KeyVaultRecordPayloads p ON p.RecordId=r.Id
            WHERE r.Collection=$collection AND r.TenantId=$tenant
            """;
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$tenant", tenant);
        return command;
    }
    private static KeyVaultRecordMetadata Metadata(SqliteDataReader r) => new(r.GetString(0), new(r.GetInt64(1), TimeSpan.Zero),
        new(r.GetInt64(2), TimeSpan.Zero), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4));

    private static string? PrefixSuccessor(string prefix)
    {
        var scalars = prefix.EnumerateRunes().ToArray();
        for (var i = scalars.Length - 1; i >= 0; i--)
        {
            if (scalars[i].Value == 0x10ffff) continue;
            var next = scalars[i].Value + 1;
            if (next == 0xd800) next = 0xe000;
            return string.Concat(scalars.Take(i).Select(r => r.ToString())) + new Rune(next);
        }
        return null;
    }

    private readonly record struct PayloadRow(string Id, string Encrypted, string? Version, int Characters, string Scope);
    private static async Task<PayloadRow?> PayloadRowAsync(SqliteConnection c, string collection, string tenant, string key, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.CommandText = """
            SELECT r.Id,r.EncryptedValue,p.Version,p.Characters FROM KeyVaultRecords r
            LEFT JOIN KeyVaultRecordPayloads p ON r.Id=p.RecordId
            WHERE r.Collection=$collection AND r.TenantId=$tenant AND r.RecordKey=$key;
            """;
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$tenant", tenant);
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3), Scope(collection, tenant, key)) : null;
    }
    private static void VerifyHeader(PayloadRow row, string privateKey)
    {
        if (CryptoService.Decrypt(row.Encrypted, privateKey) != $"KeyVault-pages-v1\n{row.Scope}\n{row.Version}\n{row.Characters.ToString(CultureInfo.InvariantCulture)}")
            throw new CryptographicException("Invalid record page manifest.");
    }
    private static async Task<string> ReadPayloadAsync(SqliteConnection c, string collection, string tenant, string key, string encrypted, string privateKey, CancellationToken ct)
    {
        var row = await PayloadRowAsync(c, collection, tenant, key, ct);
        if (row?.Version is null) return CryptoService.Decrypt(encrypted, privateKey);
        VerifyHeader(row.Value, privateKey);
        var text = await ReadChunksAsync(c, row.Value, privateKey, 0, row.Value.Characters, ct);
        if (VersionOf(text) != row.Value.Version) throw new CryptographicException("Invalid record content hash.");
        return text;
    }
    private static async Task<string> ReadChunksAsync(SqliteConnection c, PayloadRow row, string privateKey, int start, int end, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.CommandText = """
            SELECT Part,Offset,Characters,EncryptedValue FROM KeyVaultRecordChunks
            WHERE RecordId=$id AND Version=$version AND Offset<$end AND Offset+Characters>$start ORDER BY Part;
            """;
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$version", row.Version);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        var result = new StringBuilder(end - start);
        var next = start;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var part = reader.GetInt32(0); var offset = reader.GetInt32(1); var chars = reader.GetInt32(2);
            var plain = CryptoService.Decrypt(reader.GetString(3), privateKey);
            var prefix = $"{row.Id}\n{row.Scope}\n{row.Version}\n{part.ToString(CultureInfo.InvariantCulture)}\n{offset.ToString(CultureInfo.InvariantCulture)}\n";
            if (!plain.StartsWith(prefix, StringComparison.Ordinal) || plain.Length - prefix.Length != chars || offset > next)
                throw new CryptographicException("Invalid authenticated record chunk.");
            var from = Math.Max(start, offset); var to = Math.Min(end, offset + chars);
            result.Append(plain, prefix.Length + from - offset, to - from);
            next = to;
        }
        if (next != end) throw new CryptographicException("Incomplete record pages.");
        return result.ToString();
    }
    private static async Task WritePayloadAsync(SqliteConnection c, SqliteTransaction transaction, string collection, string tenant, string key, string? text, string publicKey, CancellationToken ct)
    {
        await using var lookup = c.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT Id FROM KeyVaultRecords WHERE Collection=$collection AND TenantId=$tenant AND RecordKey=$key;";
        lookup.Parameters.AddWithValue("$collection", collection); lookup.Parameters.AddWithValue("$tenant", tenant); lookup.Parameters.AddWithValue("$key", key);
        var id = (string)(await lookup.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Missing record."));
        await using var command = c.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM KeyVaultRecordChunks WHERE RecordId=$id; DELETE FROM KeyVaultRecordPayloads WHERE RecordId=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
        if (text is null) return;
        var version = VersionOf(text);
        command.CommandText = "INSERT INTO KeyVaultRecordPayloads(RecordId,Version,Characters) VALUES($id,$version,$chars);";
        command.Parameters.AddWithValue("$version", version); command.Parameters.AddWithValue("$chars", text.Length);
        await command.ExecuteNonQueryAsync(ct);
        var part = 0;
        for (var offset = 0; offset < text.Length; part++)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(PayloadChunkCharacters, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
            var prefix = $"{id}\n{Scope(collection, tenant, key)}\n{version}\n{part.ToString(CultureInfo.InvariantCulture)}\n{offset.ToString(CultureInfo.InvariantCulture)}\n";
            await using var insert = c.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO KeyVaultRecordChunks(RecordId,Version,Part,Offset,Characters,EncryptedValue) VALUES($id,$version,$part,$offset,$chars,$encrypted);";
            insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$version", version);
            insert.Parameters.AddWithValue("$part", part); insert.Parameters.AddWithValue("$offset", offset); insert.Parameters.AddWithValue("$chars", count);
            insert.Parameters.AddWithValue("$encrypted", CryptoService.Encrypt(prefix + text.Substring(offset, count), publicKey));
            await insert.ExecuteNonQueryAsync(ct);
            offset += count;
        }
    }
}
