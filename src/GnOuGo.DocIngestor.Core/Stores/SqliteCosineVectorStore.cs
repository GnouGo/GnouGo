using System.Buffers.Binary;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;
using Microsoft.Data.Sqlite;

namespace DocIngestor.Core.Stores;

/// <summary>
/// Real SQLite vector store with cosine similarity computed in .NET (brute-force scan).
/// Cross-platform reference implementation.
/// </summary>
public sealed class SqliteCosineVectorStore : IVectorSearchStore, IVectorStoreAdmin
{
    public string Name => "sqlite";

    private readonly string _dbPath;
    private readonly JsonSerializerOptions _json;

    public SqliteCosineVectorStore(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        Initialize();
    }

    public async ValueTask UpsertAsync(string collection, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("collection is required", nameof(collection));
        if (chunks.Count == 0) return;

        await using var conn = Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        foreach (var c in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var vec = c.Vector;
            var blob = EncodeVector(vec);
            var norm = ComputeNorm(vec);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO vectors(" +
                "collection, chunk_id, document_id, section_id, chunk_index, text," +
                "metadata_json, embedding_model, vector_dims, vector_norm, vector_blob, ingested_utc" +
                ") VALUES (" +
                "$collection, $chunk_id, $document_id, $section_id, $chunk_index, $text," +
                "$metadata_json, $embedding_model, $vector_dims, $vector_norm, $vector_blob, $ingested_utc" +
                ") ON CONFLICT(collection, chunk_id) DO UPDATE SET " +
                "document_id=excluded.document_id, " +
                "section_id=excluded.section_id, " +
                "chunk_index=excluded.chunk_index, " +
                "text=excluded.text, " +
                "metadata_json=excluded.metadata_json, " +
                "embedding_model=excluded.embedding_model, " +
                "vector_dims=excluded.vector_dims, " +
                "vector_norm=excluded.vector_norm, " +
                "vector_blob=excluded.vector_blob, " +
                "ingested_utc=excluded.ingested_utc;";

            cmd.Parameters.AddWithValue("$collection", collection);
            cmd.Parameters.AddWithValue("$chunk_id", c.Chunk.ChunkId);
            cmd.Parameters.AddWithValue("$document_id", c.Chunk.DocumentId);
            cmd.Parameters.AddWithValue("$section_id", c.Chunk.SectionId);
            cmd.Parameters.AddWithValue("$chunk_index", c.Chunk.Index);
            cmd.Parameters.AddWithValue("$text", c.Chunk.Text);
            cmd.Parameters.AddWithValue("$metadata_json", JsonSerializer.Serialize(c.Chunk.Metadata, _json));
            cmd.Parameters.AddWithValue("$embedding_model", c.EmbeddingModelName);
            cmd.Parameters.AddWithValue("$vector_dims", vec.Length);
            cmd.Parameters.AddWithValue("$vector_norm", norm);
            cmd.Parameters.Add("$vector_blob", SqliteType.Blob).Value = blob;
            cmd.Parameters.AddWithValue("$ingested_utc", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async ValueTask<IReadOnlyList<VectorSearchResult>> SearchAsync(string collection, float[] queryVector, int topK = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("collection is required", nameof(collection));
        if (queryVector is null || queryVector.Length == 0) throw new ArgumentException("queryVector is required", nameof(queryVector));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));

        var qNorm = ComputeNorm(queryVector);
        if (qNorm <= 1e-12) return Array.Empty<VectorSearchResult>();

        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT chunk_id, document_id, section_id, chunk_index, text, " +
            "metadata_json, embedding_model, vector_dims, vector_norm, vector_blob " +
            "FROM vectors WHERE collection = $collection;";
        cmd.Parameters.AddWithValue("$collection", collection);

        var best = new List<VectorSearchResult>(capacity: topK);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();

            var dims = reader.GetInt32(7);
            if (dims != queryVector.Length) continue;

            var vNorm = reader.GetDouble(8);
            if (vNorm <= 1e-12) continue;

            var blob = (byte[])reader["vector_blob"];
            var vec = DecodeVector(blob, dims);

            var score = Dot(queryVector, vec) / (qNorm * vNorm);

            var chunk = new TextChunk(
                ChunkId: reader.GetString(0),
                DocumentId: reader.GetString(1),
                SectionId: reader.GetString(2),
                Index: reader.GetInt32(3),
                Text: reader.GetString(4),
                Metadata: DeserializeMeta(reader.GetString(5))
            );

            var embedded = new EmbeddedChunk(chunk, reader.GetString(6), vec);
            InsertTopK(best, new VectorSearchResult(score, embedded), topK);
        }

        best.Sort((a, b) => b.Score.CompareTo(a.Score));
        return best;
    }

    private Dictionary<string, string> DeserializeMeta(string json)
        => string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : (JsonSerializer.Deserialize<Dictionary<string, string>>(json, _json)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    // ---- IVectorStoreAdmin ----

    /// <inheritdoc />
    public async ValueTask<int> DeleteByDocumentAsync(string collection, string documentId, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vectors WHERE collection = $collection AND document_id = $document_id;";
        cmd.Parameters.AddWithValue("$collection", collection);
        cmd.Parameters.AddWithValue("$document_id", documentId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask<int> DeleteByDocumentPrefixAsync(string collection, string documentIdPrefix, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vectors WHERE collection = $collection AND document_id LIKE $prefix;";
        cmd.Parameters.AddWithValue("$collection", collection);
        cmd.Parameters.AddWithValue("$prefix", documentIdPrefix.Replace("%", "[%]").Replace("_", "[_]") + "%");
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ListCollectionsAsync(CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT collection FROM vectors ORDER BY collection;";
        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetString(0));
        return results;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<string>> ListDocumentsAsync(string collection, CancellationToken ct = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT document_id FROM vectors WHERE collection = $collection ORDER BY document_id;";
        cmd.Parameters.AddWithValue("$collection", collection);
        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(reader.GetString(0));
        return results;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            "CREATE TABLE IF NOT EXISTS vectors (" +
            "collection TEXT NOT NULL," +
            "chunk_id TEXT NOT NULL," +
            "document_id TEXT NOT NULL," +
            "section_id TEXT NOT NULL," +
            "chunk_index INTEGER NOT NULL," +
            "text TEXT NOT NULL," +
            "metadata_json TEXT NOT NULL," +
            "embedding_model TEXT NOT NULL," +
            "vector_dims INTEGER NOT NULL," +
            "vector_norm REAL NOT NULL," +
            "vector_blob BLOB NOT NULL," +
            "ingested_utc TEXT NOT NULL," +
            "PRIMARY KEY (collection, chunk_id)" +
            ");" +
            "CREATE INDEX IF NOT EXISTS idx_vectors_collection_document ON vectors(collection, document_id);";
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private static void InsertTopK(List<VectorSearchResult> best, VectorSearchResult cand, int topK)
    {
        if (best.Count < topK)
        {
            best.Add(cand);
            return;
        }

        int minIdx = 0;
        double minScore = best[0].Score;
        for (int i = 1; i < best.Count; i++)
        {
            if (best[i].Score < minScore)
            {
                minScore = best[i].Score;
                minIdx = i;
            }
        }

        if (cand.Score > minScore)
            best[minIdx] = cand;
    }

    private static byte[] EncodeVector(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), v[i]);
        return bytes;
    }

    private static float[] DecodeVector(byte[] bytes, int dims)
    {
        var v = new float[dims];
        var span = bytes.AsSpan();
        for (int i = 0; i < dims; i++)
            v[i] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * 4, 4));
        return v;
    }

    private static double ComputeNorm(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++)
            sum += (double)v[i] * v[i];
        return Math.Sqrt(sum);
    }

    private static double Dot(float[] a, float[] b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += (double)a[i] * b[i];
        return dot;
    }
}
