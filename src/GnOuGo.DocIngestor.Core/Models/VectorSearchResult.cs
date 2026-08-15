namespace DocIngestor.Core.Models;

/// <summary>Represents one scored vector-search match.</summary>
/// <param name="Score">Similarity score, where larger values are better.</param>
/// <param name="Chunk">Matched embedded chunk.</param>
public sealed record VectorSearchResult(
    double Score,
    EmbeddedChunk Chunk
);
