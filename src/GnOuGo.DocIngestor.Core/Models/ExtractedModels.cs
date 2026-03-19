namespace DocIngestor.Core.Models;

public sealed record ExtractedDocument(
    string DocumentId,
    string SourceName,
    string MimeType,
    IReadOnlyList<ExtractedSection> Sections,
    IReadOnlyDictionary<string, string> Metadata
);

public sealed record ExtractedSection(
    string SectionId,
    string Title,
    int? PageNumber,
    string Text,
    IReadOnlyDictionary<string, string> Metadata
)
{
    public string? Markdown { get; init; }
    public string? CsvLike { get; init; }
}


public sealed record TextChunk(
    string ChunkId,
    string DocumentId,
    string SectionId,
    int Index,
    string Text,
    IReadOnlyDictionary<string, string> Metadata
)
{
    public string? Markdown { get; init; }
    public string? CsvLike { get; init; }
}


public sealed record EmbeddedChunk(
    TextChunk Chunk,
    string EmbeddingModelName,
    float[] Vector
);
