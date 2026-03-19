using DocIngestor.Core.Models;

namespace DocIngestor.Core.Abstractions;

public enum ChunkingMode
{
    Recursive = 0,
    Semantic = 1,
    /// <summary>
    /// Auto-selects the best chunking strategy based on the document type:
    /// Recursive for plain-text files (code, config, JSON, YAML, etc.),
    /// Semantic for narrative documents (PDF, DOCX, PPTX).
    /// </summary>
    Auto = 2,
}

public sealed record ChunkSizePolicy(
    int MinTokens = 200,
    int TargetTokens = 600,
    int MaxTokens = 900,
    int OverlapTokens = 60,
    int[]? AllowedTargetTokens = null
);

public interface ITokenCounter
{
    int CountTokens(string text);
}

public interface IChunker
{
    ChunkingMode Mode { get; }
    ValueTask<IReadOnlyList<TextChunk>> ChunkAsync(ExtractedDocument doc, ChunkSizePolicy policy, CancellationToken ct = default);
}
