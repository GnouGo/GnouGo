using GnOuGo.DocIngestor.Mcp.Models;
using Microsoft.Data.Sqlite;

namespace GnOuGo.DocIngestor.Mcp.Data;

public sealed class StoredDocumentRepository
{
    private readonly string _databasePath;

    public StoredDocumentRepository(string databasePath)
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
            "CREATE TABLE IF NOT EXISTS stored_documents (" +
            "id TEXT PRIMARY KEY," +
            "tenant_id TEXT NOT NULL," +
            "source_url TEXT NOT NULL," +
            "file_name TEXT NOT NULL," +
            "content_type TEXT NULL," +
            "size_bytes INTEGER NOT NULL," +
            "sha256 TEXT NOT NULL," +
            "collection TEXT NOT NULL," +
            "embedding_config_name TEXT NOT NULL," +
            "original_path TEXT NOT NULL," +
            "chunk_count INTEGER NOT NULL," +
            "created_utc TEXT NOT NULL," +
            "updated_utc TEXT NOT NULL" +
            ");" +
            "CREATE UNIQUE INDEX IF NOT EXISTS ux_stored_documents_tenant_collection_source " +
            "ON stored_documents(tenant_id, collection, source_url);" +
            "CREATE INDEX IF NOT EXISTS ix_stored_documents_tenant_collection " +
            "ON stored_documents(tenant_id, collection);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredDocumentRecord?> GetBySourceAsync(string tenantId, string collection, string sourceUrl, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, tenant_id, source_url, file_name, content_type, size_bytes, sha256, collection, " +
            "embedding_config_name, original_path, chunk_count, created_utc, updated_utc " +
            "FROM stored_documents WHERE tenant_id = $tenant_id AND collection = $collection AND source_url = $source_url;";
        cmd.Parameters.AddWithValue("$tenant_id", tenantId);
        cmd.Parameters.AddWithValue("$collection", collection);
        cmd.Parameters.AddWithValue("$source_url", sourceUrl);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<StoredDocumentRecord?> GetByIdAsync(string documentId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, tenant_id, source_url, file_name, content_type, size_bytes, sha256, collection, " +
            "embedding_config_name, original_path, chunk_count, created_utc, updated_utc " +
            "FROM stored_documents WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", documentId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<StoredDocumentRecord>> ListAsync(string? tenantId, string? collection, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            where.Add("tenant_id = $tenant_id");
            cmd.Parameters.AddWithValue("$tenant_id", tenantId);
        }

        if (!string.IsNullOrWhiteSpace(collection))
        {
            where.Add("collection = $collection");
            cmd.Parameters.AddWithValue("$collection", collection);
        }

        cmd.CommandText =
            "SELECT id, tenant_id, source_url, file_name, content_type, size_bytes, sha256, collection, " +
            "embedding_config_name, original_path, chunk_count, created_utc, updated_utc FROM stored_documents" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty) +
            " ORDER BY updated_utc DESC;";

        var results = new List<StoredDocumentRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRecord(reader));

        return results;
    }

    public async Task<IReadOnlyList<string>> ListEmbeddingConfigNamesAsync(string? tenantId, string collection, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();

        var where = new List<string> { "collection = $collection" };
        cmd.Parameters.AddWithValue("$collection", collection);

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            where.Add("tenant_id = $tenant_id");
            cmd.Parameters.AddWithValue("$tenant_id", tenantId);
        }

        cmd.CommandText =
            "SELECT DISTINCT embedding_config_name FROM stored_documents WHERE " +
            string.Join(" AND ", where) +
            " ORDER BY embedding_config_name;";

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var value = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(value))
                results.Add(value);
        }

        return results;
    }

    public async Task UpsertAsync(StoredDocumentRecord record, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO stored_documents(" +
            "id, tenant_id, source_url, file_name, content_type, size_bytes, sha256, collection, " +
            "embedding_config_name, original_path, chunk_count, created_utc, updated_utc) VALUES (" +
            "$id, $tenant_id, $source_url, $file_name, $content_type, $size_bytes, $sha256, $collection, " +
            "$embedding_config_name, $original_path, $chunk_count, $created_utc, $updated_utc) " +
            "ON CONFLICT(id) DO UPDATE SET " +
            "tenant_id=excluded.tenant_id, source_url=excluded.source_url, file_name=excluded.file_name, " +
            "content_type=excluded.content_type, size_bytes=excluded.size_bytes, sha256=excluded.sha256, " +
            "collection=excluded.collection, embedding_config_name=excluded.embedding_config_name, " +
            "original_path=excluded.original_path, chunk_count=excluded.chunk_count, updated_utc=excluded.updated_utc;";
        BindRecord(cmd, record);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string documentId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM stored_documents WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", documentId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
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

    private static StoredDocumentRecord ReadRecord(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        TenantId: reader.GetString(1),
        SourceUrl: reader.GetString(2),
        FileName: reader.GetString(3),
        ContentType: reader.IsDBNull(4) ? null : reader.GetString(4),
        SizeBytes: reader.GetInt64(5),
        Sha256: reader.GetString(6),
        Collection: reader.GetString(7),
        EmbeddingConfigName: reader.GetString(8),
        OriginalPath: reader.GetString(9),
        ChunkCount: reader.GetInt32(10),
        CreatedUtc: DateTimeOffset.Parse(reader.GetString(11)),
        UpdatedUtc: DateTimeOffset.Parse(reader.GetString(12)));

    private static void BindRecord(SqliteCommand cmd, StoredDocumentRecord record)
    {
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$tenant_id", record.TenantId);
        cmd.Parameters.AddWithValue("$source_url", record.SourceUrl);
        cmd.Parameters.AddWithValue("$file_name", record.FileName);
        cmd.Parameters.AddWithValue("$content_type", (object?)record.ContentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        cmd.Parameters.AddWithValue("$sha256", record.Sha256);
        cmd.Parameters.AddWithValue("$collection", record.Collection);
        cmd.Parameters.AddWithValue("$embedding_config_name", record.EmbeddingConfigName);
        cmd.Parameters.AddWithValue("$original_path", record.OriginalPath);
        cmd.Parameters.AddWithValue("$chunk_count", record.ChunkCount);
        cmd.Parameters.AddWithValue("$created_utc", record.CreatedUtc.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$updated_utc", record.UpdatedUtc.UtcDateTime.ToString("O"));
    }
}

