using System.Text.Json.Serialization;

namespace DocIngestor.Core;

/// <summary>
/// Source-generated JSON context for DocIngestor.Core internal types.
/// Used by vector stores to avoid reflection-based JSON serialization.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonlChunkRecord))]
internal partial class DocIngestorCoreJsonContext : JsonSerializerContext;

/// <summary>DTO for one line in a JSONL vector store file.</summary>
internal sealed record JsonlChunkRecord(
    string Collection,
    string ChunkId,
    string DocumentId,
    string SectionId,
    int ChunkIndex,
    string Text,
    IReadOnlyDictionary<string, string> Metadata,
    string EmbeddingModel,
    int Dims,
    string VectorB64,
    string IngestedUtc);

