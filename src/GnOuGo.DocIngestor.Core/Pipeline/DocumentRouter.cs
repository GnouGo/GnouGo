using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Pipeline;

/// <summary>Selects text and image extractors from stable format contracts.</summary>
public sealed class DocumentRouter
{
    private readonly IReadOnlyList<IDocumentTextExtractor> _extractors;
    private readonly IReadOnlyList<IImageExtractor> _imageExtractors;

    /// <summary>Initializes a router from the available extractor implementations.</summary>
    /// <param name="extractors">Text extractors to register.</param>
    /// <param name="imageExtractors">Optional image extractors to register.</param>
    public DocumentRouter(IEnumerable<IDocumentTextExtractor> extractors, IEnumerable<IImageExtractor>? imageExtractors = null)
    {
        _extractors = extractors.ToList().AsReadOnly();
        _imageExtractors = (imageExtractors ?? Enumerable.Empty<IImageExtractor>()).ToList().AsReadOnly();
    }

    /// <summary>Gets the text extractor supporting the supplied source identity.</summary>
    /// <param name="fileName">Logical file name.</param>
    /// <param name="contentType">Optional MIME content type.</param>
    /// <returns>The matching extractor.</returns>
    public IDocumentTextExtractor GetTextExtractor(string fileName, string? contentType = null)
        => _extractors.FirstOrDefault(e => e.CanHandle(fileName, contentType))
           ?? throw new NotSupportedException($"No text extractor for: {fileName}");

    /// <summary>Attempts to get an image extractor supporting the supplied source identity.</summary>
    /// <param name="fileName">Logical file name.</param>
    /// <param name="contentType">Optional MIME content type.</param>
    /// <returns>The matching extractor, or <see langword="null"/> when none is registered.</returns>
    public IImageExtractor? TryGetImageExtractor(string fileName, string? contentType = null)
        => _imageExtractors.FirstOrDefault(e => e.CanHandle(fileName, contentType));
}
