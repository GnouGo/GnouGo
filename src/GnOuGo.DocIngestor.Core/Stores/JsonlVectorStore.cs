using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Models;

namespace DocIngestor.Core.Stores;

/// <summary>
/// Very portable store: appends one JSON object per line (JSONL).
/// Vectors are stored as base64 float32 little-endian.
/// </summary>
public sealed class JsonlVectorStore : IVectorStore
{
    public string Name => "jsonl";

    private readonly string _directory;
    private readonly JsonSerializerOptions _json;

    public JsonlVectorStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public async ValueTask UpsertAsync(string collection, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(collection)) throw new ArgumentException("collection is required", nameof(collection));
        if (chunks.Count == 0) return;

        var file = Path.Combine(_directory, $"{Sanitize(collection)}-{DateTime.UtcNow:yyyyMMdd}.jsonl");

        await using var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        await using var sw = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var c in chunks)
        {
            ct.ThrowIfCancellationRequested();

            var dto = new
            {
                collection,
                chunkId = c.Chunk.ChunkId,
                documentId = c.Chunk.DocumentId,
                sectionId = c.Chunk.SectionId,
                chunkIndex = c.Chunk.Index,
                text = c.Chunk.Text,
                metadata = c.Chunk.Metadata,
                embeddingModel = c.EmbeddingModelName,
                dims = c.Vector.Length,
                vectorB64 = Convert.ToBase64String(EncodeVector(c.Vector)),
                ingestedUtc = DateTime.UtcNow.ToString("O")
            };

            var json = JsonSerializer.Serialize(dto, _json);
            await sw.WriteLineAsync(json);
        }
    }

    private static byte[] EncodeVector(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), v[i]);
        return bytes;
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }
}
