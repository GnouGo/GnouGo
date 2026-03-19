using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Pipeline;

public sealed record IngestionOptions(
    ChunkingMode ChunkingMode,
    ChunkSizePolicy ChunkPolicy,
    string EmbeddingModelName = "hash-384",
    double SemanticSimilarityThreshold = 0.80,
    bool EnableEmbedding = true,
    ImageExtractionOptions Images = default,
    StoreOptions? Store = null
);

