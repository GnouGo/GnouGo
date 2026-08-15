using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Pipeline;

/// <summary>Controls extraction, chunking, embedding, image processing, and storage for one ingestion.</summary>
/// <param name="ChunkingMode">Chunking strategy to use.</param>
/// <param name="ChunkPolicy">Token sizing policy.</param>
/// <param name="EmbeddingModelName">Embedding model selected through the model router.</param>
/// <param name="SemanticSimilarityThreshold">Similarity threshold used by semantic chunking.</param>
/// <param name="EnableEmbedding">Whether generated chunks should be embedded.</param>
/// <param name="Images">Image extraction and OCR options.</param>
/// <param name="Store">Optional vector-store options.</param>
public sealed record IngestionOptions(
    ChunkingMode ChunkingMode,
    ChunkSizePolicy ChunkPolicy,
    string EmbeddingModelName = "hash-384",
    double SemanticSimilarityThreshold = 0.80,
    bool EnableEmbedding = true,
    ImageExtractionOptions Images = default,
    StoreOptions? Store = null
);

