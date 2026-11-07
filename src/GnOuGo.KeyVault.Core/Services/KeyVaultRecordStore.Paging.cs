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

    public Task<KeyVaultRecordMetadata?> GetMetadataAsync(string collection, string tenantId, string key, string author, CancellationToken ct = default)
        => ExecuteSqliteAsync(() => GetMetadataCoreAsync(collection, tenantId, key, author, ct), ct);

    private async Task<KeyVaultRecordMetadata?> GetMetadataCoreAsync(string collection, string tenantId, string key, string author, CancellationToken ct)
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

    public Task<KeyVaultRecordMetadataPage> QueryMetadataAsync(string collection, string tenantId, string prefix, string author,
        KeyVaultRecordCursor? after = null, int count = 100, CancellationToken ct = default)
        => ExecuteSqliteAsync(() => QueryMetadataCoreAsync(collection, tenantId, prefix, author, after, count, ct), ct);

    private async Task<KeyVaultRecordMetadataPage> QueryMetadataCoreAsync(string collection, string tenantId, string prefix, string author,
        KeyVaultRecordCursor? after, int count, CancellationToken ct)
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

    public Task<KeyVaultRecordRange?> ReadRangeAsync(string collection, string tenantId, string key, string author,
        string? version, int offset, int count, CancellationToken ct = default)
        => ExecuteSqliteAsync(() => ReadRangeCoreAsync(collection, tenantId, key, author, version, offset, count, ct), ct);

    private async Task<KeyVaultRecordRange?> ReadRangeCoreAsync(string collection, string tenantId, string key, string author,
        string? version, int offset, int count, CancellationToken ct)
    {
        ValidateCoordinates(collection, tenantId, key, author);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count is < 2 or > 65536) throw new ArgumentOutOfRangeException(nameof(count));
        await _databaseGate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            var (pub, priv) = await EnsureDefaultKeyPairAsync(connection, ct);
            var initial = await PayloadRowAsync(connection, collection, tenantId, key, ct, null);
            if (initial is null) return null;
            if (initial.Value.Version is null)
                await PrepareHistoricalPagesAsync(connection, collection, tenantId, key, author, pub, priv, ct);
            // Normal inspection takes only a read snapshot, released before decryption
            // and the mandatory audit write. Historical conversion rechecks under its own writer.
            await using var transaction = connection.BeginTransaction(deferred: true);
            var row = await PayloadRowAsync(connection, collection, tenantId, key, ct, transaction);
            if (row is null) return null;
            if (row.Value.Version is null) throw new PublicationConflictException();
            var value = row!.Value;
            if (version is not null && version != value.Version) throw new KeyVaultRecordVersionException();
            if (offset > value.Characters) throw new ArgumentOutOfRangeException(nameof(offset));
            var end = (int)Math.Min((long)offset + count, value.Characters);
            var start = Math.Max(0, offset - 1);
            var readEnd = Math.Min(value.Characters, end + 1);
            var chunks = await ReadEncryptedChunksAsync(connection, value, start, readEnd, ct, transaction);
            await transaction.CommitAsync(ct);
            await WriteAuditAsync(connection, collection, tenantId, key, "ReadRecordRange", author, null, ct);
            VerifyHeader(value, priv);
            var textPage = DecryptChunks(value, chunks, priv, start, readEnd);
            var startInPage = offset == 0 ? 0 : 1;
            if (offset < value.Characters && char.IsLowSurrogate(textPage[startInPage]))
                throw new ArgumentException("The offset splits a Unicode scalar.", nameof(offset));
            if (end < value.Characters && char.IsLowSurrogate(textPage[startInPage + end - offset])) end--;
            return new(value.Version!, offset, end, value.Characters, textPage.Substring(startInPage, end - offset));
        }
        finally { _databaseGate.Release(); }
    }

    private static async Task PrepareHistoricalPagesAsync(SqliteConnection connection, string collection, string tenant, string key,
        string author, string pub, string priv, CancellationToken ct)
    {
        await using var transaction = connection.BeginTransaction();
        var row = await PayloadRowAsync(connection, collection, tenant, key, ct, transaction);
        if (row is { Version: null })
        {
            var text = CryptoService.Decrypt(row.Value.Encrypted, priv);
            await WritePayloadAsync(connection, transaction, collection, tenant, key,
                PreparePayload(row.Value.Id, collection, tenant, key, text, pub, ct), ct);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE KeyVaultRecords SET EncryptedValue=$value WHERE Id=$id;";
            update.Parameters.AddWithValue("$id", row.Value.Id);
            update.Parameters.AddWithValue("$value", CryptoService.Encrypt(PayloadHeader(text, collection, tenant, key), pub));
            await update.ExecuteNonQueryAsync(ct);
            await WriteAuditAsync(connection, transaction, collection, tenant, key, "PrepareRecordPages", author, null, ct);
        }
        await transaction.CommitAsync(ct);
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
    private static async Task<PayloadRow?> PayloadRowAsync(SqliteConnection c, string collection, string tenant, string key, CancellationToken ct, SqliteTransaction? transaction)
    {
        await using var command = c.CreateCommand();
        command.Transaction = transaction;
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
    private sealed record EncryptedChunk(int Part, int Offset, int Characters, string Encrypted);
    private sealed record EncryptedPayload(string Encrypted, PayloadRow? Row, IReadOnlyList<EncryptedChunk> Chunks);

    private static async Task<EncryptedPayload> ReadEncryptedPayloadAsync(SqliteConnection c, string collection, string tenant, string key,
        string encrypted, CancellationToken ct, SqliteTransaction transaction)
    {
        var row = await PayloadRowAsync(c, collection, tenant, key, ct, transaction);
        if (row?.Version is null) return new(encrypted, null, []);
        await using var command = c.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Part,Offset,Characters,EncryptedValue FROM KeyVaultRecordChunks WHERE RecordId=$id AND Version=$version ORDER BY Part;";
        command.Parameters.AddWithValue("$id", row.Value.Id);
        command.Parameters.AddWithValue("$version", row.Value.Version);
        var chunks = new List<EncryptedChunk>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) chunks.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        return new(encrypted, row, chunks);
    }

    private static string DecryptPayload(EncryptedPayload payload, string privateKey)
    {
        if (payload.Row is not { } row) return CryptoService.Decrypt(payload.Encrypted, privateKey);
        VerifyHeader(row, privateKey);
        var result = new StringBuilder(row.Characters);
        var expectedPart = 0;
        foreach (var chunk in payload.Chunks)
        {
            var plain = CryptoService.Decrypt(chunk.Encrypted, privateKey);
            var prefix = $"{row.Id}\n{row.Scope}\n{row.Version}\n{chunk.Part.ToString(CultureInfo.InvariantCulture)}\n{chunk.Offset.ToString(CultureInfo.InvariantCulture)}\n";
            if (chunk.Part != expectedPart++ || chunk.Offset != result.Length ||
                !plain.StartsWith(prefix, StringComparison.Ordinal) || plain.Length - prefix.Length != chunk.Characters)
                throw new CryptographicException("Invalid authenticated record chunk.");
            result.Append(plain, prefix.Length, chunk.Characters);
        }
        var text = result.ToString();
        if (text.Length != row.Characters || VersionOf(text) != row.Version) throw new CryptographicException("Invalid record content hash.");
        return text;
    }
    private static async Task<IReadOnlyList<EncryptedChunk>> ReadEncryptedChunksAsync(SqliteConnection c, PayloadRow row, int start, int end, CancellationToken ct, SqliteTransaction transaction)
    {
        await using var command = c.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Part,Offset,Characters,EncryptedValue FROM KeyVaultRecordChunks
            WHERE RecordId=$id AND Version=$version AND Offset<$end AND Offset+Characters>$start ORDER BY Part;
            """;
        command.Parameters.AddWithValue("$id", row.Id);
        command.Parameters.AddWithValue("$version", row.Version);
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$end", end);
        var chunks = new List<EncryptedChunk>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            chunks.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        return chunks;
    }

    private static string DecryptChunks(PayloadRow row, IReadOnlyList<EncryptedChunk> chunks, string privateKey, int start, int end)
    {
        var result = new StringBuilder(end - start);
        var next = start;
        foreach (var chunk in chunks)
        {
            var part = chunk.Part; var offset = chunk.Offset; var chars = chunk.Characters;
            var plain = CryptoService.Decrypt(chunk.Encrypted, privateKey);
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
    private sealed record PreparedPayload(string Id, string? Version, int Characters, IReadOnlyList<EncryptedChunk> Chunks);

    private static PreparedPayload PreparePayload(string id, string collection, string tenant, string key, string? text, string publicKey, CancellationToken ct)
    {
        if (text is null) return new(id, null, 0, []);
        var version = VersionOf(text);
        var chunks = new List<EncryptedChunk>();
        for (var offset = 0; offset < text.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(PayloadChunkCharacters, text.Length - offset);
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1])) count--;
            var part = chunks.Count;
            var prefix = $"{id}\n{Scope(collection, tenant, key)}\n{version}\n{part.ToString(CultureInfo.InvariantCulture)}\n{offset.ToString(CultureInfo.InvariantCulture)}\n";
            chunks.Add(new(part, offset, count, CryptoService.Encrypt(prefix + text.Substring(offset, count), publicKey)));
            offset += count;
        }
        return new(id, version, text.Length, chunks);
    }

    private static async Task<string?> ReadRecordIdAsync(SqliteConnection c, string collection, string tenant, string key, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        await using var lookup = c.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT Id FROM KeyVaultRecords WHERE Collection=$collection AND TenantId=$tenant AND RecordKey=$key;";
        lookup.Parameters.AddWithValue("$collection", collection); lookup.Parameters.AddWithValue("$tenant", tenant); lookup.Parameters.AddWithValue("$key", key);
        return (string?)await lookup.ExecuteScalarAsync(ct);
    }

    private static async Task WritePayloadAsync(SqliteConnection c, SqliteTransaction transaction, string collection, string tenant, string key, PreparedPayload payload, CancellationToken ct)
    {
        var id = await ReadRecordIdAsync(c, collection, tenant, key, ct, transaction);
        if (id != payload.Id) throw new PublicationConflictException();
        await using var command = c.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM KeyVaultRecordChunks WHERE RecordId=$id; DELETE FROM KeyVaultRecordPayloads WHERE RecordId=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct);
        if (payload.Version is null) return;
        command.CommandText = "INSERT INTO KeyVaultRecordPayloads(RecordId,Version,Characters) VALUES($id,$version,$chars);";
        command.Parameters.AddWithValue("$version", payload.Version); command.Parameters.AddWithValue("$chars", payload.Characters);
        await command.ExecuteNonQueryAsync(ct);
        foreach (var chunk in payload.Chunks)
        {
            ct.ThrowIfCancellationRequested();
            await using var insert = c.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO KeyVaultRecordChunks(RecordId,Version,Part,Offset,Characters,EncryptedValue) VALUES($id,$version,$part,$offset,$chars,$encrypted);";
            insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$version", payload.Version);
            insert.Parameters.AddWithValue("$part", chunk.Part); insert.Parameters.AddWithValue("$offset", chunk.Offset); insert.Parameters.AddWithValue("$chars", chunk.Characters);
            insert.Parameters.AddWithValue("$encrypted", chunk.Encrypted);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<T> ExecuteSqliteAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try { return await RetryContentionAsync(action, ct); }
        catch (SqliteException ex)
        {
            throw new KeyVaultAccessException("KeyVault record inspection could not access storage.", ex, ex.SqliteErrorCode is 5 or 6);
        }
    }
}
