using DocIngestor.Core.Models;

namespace DocIngestor.Core.Abstractions;

public interface IDocumentTextExtractor
{
    /// <summary>Returns true when this extractor supports the given file name (and optional content type).</summary>
    bool CanHandle(string fileName, string? contentType = null);

    /// <summary>Extract text from a seekable <see cref="DocumentSource"/>. The stream must be seekable.</summary>
    ValueTask<ExtractedDocument> ExtractAsync(DocumentSource source, CancellationToken ct = default);
}
