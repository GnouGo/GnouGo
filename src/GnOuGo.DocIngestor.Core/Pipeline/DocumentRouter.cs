using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Pipeline;

public sealed class DocumentRouter
{
    private readonly IReadOnlyList<IDocumentTextExtractor> _extractors;
    private readonly IReadOnlyList<IImageExtractor> _imageExtractors;

    public DocumentRouter(IEnumerable<IDocumentTextExtractor> extractors, IEnumerable<IImageExtractor>? imageExtractors = null)
    {
        _extractors = extractors.ToList().AsReadOnly();
        _imageExtractors = (imageExtractors ?? Enumerable.Empty<IImageExtractor>()).ToList().AsReadOnly();
    }

    public IDocumentTextExtractor GetTextExtractor(string fileName, string? contentType = null)
        => _extractors.FirstOrDefault(e => e.CanHandle(fileName, contentType))
           ?? throw new NotSupportedException($"No text extractor for: {fileName}");

    public IImageExtractor? TryGetImageExtractor(string fileName, string? contentType = null)
        => _imageExtractors.FirstOrDefault(e => e.CanHandle(fileName, contentType));
}
