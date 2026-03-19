namespace DocIngestor.Core.Models;

public sealed record VectorSearchResult(
    double Score,
    EmbeddedChunk Chunk
);
