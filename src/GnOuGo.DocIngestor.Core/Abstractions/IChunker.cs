using DocIngestor.Core.Models;

namespace DocIngestor.Core.Abstractions;

/// <summary>Available strategies for splitting an extracted document into chunks.</summary>
public enum ChunkingMode
{
    /// <summary>Splits text recursively using structural separators and token limits.</summary>
    Recursive = 0,
    /// <summary>Splits text at boundaries selected using embedding similarity.</summary>
    Semantic = 1,
    /// <summary>
    /// Auto-selects the best chunking strategy based on the document type:
    /// Recursive for plain-text files (code, config, JSON, YAML, etc.),
    /// Semantic for narrative documents (PDF, DOCX, PPTX).
    /// </summary>
    Auto = 2,
}

/// <summary>Defines token limits and overlap behavior for document chunking.</summary>
/// <param name="MinTokens">Preferred minimum number of tokens per chunk.</param>
/// <param name="TargetTokens">Target number of tokens per chunk.</param>
/// <param name="MaxTokens">Maximum number of tokens permitted per chunk.</param>
/// <param name="OverlapTokens">Number of tokens repeated between adjacent chunks.</param>
/// <param name="AllowedTargetTokens">Optional discrete target sizes from which a chunker may select.</param>
public sealed record ChunkSizePolicy(
    int MinTokens = 200,
    int TargetTokens = 600,
    int MaxTokens = 900,
    int OverlapTokens = 60,
    int[]? AllowedTargetTokens = null
);

/// <summary>Counts model tokens in text without coupling chunkers to a tokenizer implementation.</summary>
public interface ITokenCounter
{
    /// <summary>Counts the tokens in <paramref name="text"/>.</summary>
    /// <param name="text">Text to measure.</param>
    /// <returns>The token count.</returns>
    int CountTokens(string text);
}

/// <summary>Splits extracted documents using a specific chunking strategy.</summary>
public interface IChunker
{
    /// <summary>Gets the strategy implemented by this chunker.</summary>
    ChunkingMode Mode { get; }

    /// <summary>Splits a document according to the supplied token policy.</summary>
    /// <param name="doc">Document to split.</param>
    /// <param name="policy">Token size and overlap policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ordered document chunks.</returns>
    ValueTask<IReadOnlyList<TextChunk>> ChunkAsync(ExtractedDocument doc, ChunkSizePolicy policy, CancellationToken ct = default);
}
