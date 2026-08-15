namespace DocIngestor.Core.Models;

/// <summary>Represents the normalized content and metadata extracted from a source document.</summary>
/// <param name="DocumentId">Stable document identifier.</param>
/// <param name="SourceName">Logical source file name.</param>
/// <param name="MimeType">Resolved document MIME type.</param>
/// <param name="Sections">Ordered extracted sections.</param>
/// <param name="Metadata">Document-level metadata.</param>
public sealed record ExtractedDocument(
    string DocumentId,
    string SourceName,
    string MimeType,
    IReadOnlyList<ExtractedSection> Sections,
    IReadOnlyDictionary<string, string> Metadata
);

/// <summary>Represents one logical section extracted from a document.</summary>
/// <param name="SectionId">Stable section identifier within the document.</param>
/// <param name="Title">Human-readable section title.</param>
/// <param name="PageNumber">One-based page or slide number, when known.</param>
/// <param name="Text">Normalized plain text.</param>
/// <param name="Metadata">Section-level metadata.</param>
public sealed record ExtractedSection(
    string SectionId,
    string Title,
    int? PageNumber,
    string Text,
    IReadOnlyDictionary<string, string> Metadata
)
{
    /// <summary>Gets optional Markdown preserving document structure.</summary>
    public string? Markdown { get; init; }

    /// <summary>Gets optional CSV-like tabular content.</summary>
    public string? CsvLike { get; init; }
}


/// <summary>Represents an ordered text fragment produced by a chunking strategy.</summary>
/// <param name="ChunkId">Stable chunk identifier.</param>
/// <param name="DocumentId">Owning document identifier.</param>
/// <param name="SectionId">Owning section identifier.</param>
/// <param name="Index">Zero-based chunk position.</param>
/// <param name="Text">Plain chunk text.</param>
/// <param name="Metadata">Chunk metadata.</param>
public sealed record TextChunk(
    string ChunkId,
    string DocumentId,
    string SectionId,
    int Index,
    string Text,
    IReadOnlyDictionary<string, string> Metadata
)
{
    /// <summary>Gets optional Markdown corresponding to the chunk.</summary>
    public string? Markdown { get; init; }

    /// <summary>Gets optional CSV-like tabular content corresponding to the chunk.</summary>
    public string? CsvLike { get; init; }
}


/// <summary>Associates a text chunk with the vector generated for it.</summary>
/// <param name="Chunk">Embedded text chunk.</param>
/// <param name="EmbeddingModelName">Name of the model that generated the vector.</param>
/// <param name="Vector">Generated embedding vector.</param>
public sealed record EmbeddedChunk(
    TextChunk Chunk,
    string EmbeddingModelName,
    float[] Vector
);
