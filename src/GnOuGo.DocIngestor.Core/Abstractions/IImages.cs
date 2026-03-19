namespace DocIngestor.Core.Abstractions;

public readonly record struct ImageExtractionOptions(
    bool EnableImageDiscovery = false,
    bool LoadImageBytes = false,
    bool EnableOcr = false,
    string OcrLanguage = "eng",
    int? OcrDpi = 300,
    int MaxImagesPerSection = 10,
    long MaxImageBytes = 2_000_000,
    int? MaxWidth = 1280,
    int? MaxHeight = 1280
);

public sealed record ImageArtifact(
    string Id,
    int? PageNumber,
    string? SectionId,
    string? Name,
    string? ContentType,
    int? Width,
    int? Height,
    long? LengthBytes,
    byte[]? Bytes,
    IReadOnlyDictionary<string, string> Metadata
);

public interface IImageExtractor
{
    /// <summary>Returns true when this extractor supports the given file name (and optional content type).</summary>
    bool CanHandle(string fileName, string? contentType = null);

    /// <summary>Extract images from a seekable <see cref="DocumentSource"/>.</summary>
    ValueTask<IReadOnlyList<ImageArtifact>> ExtractImagesAsync(DocumentSource source, ImageExtractionOptions options, CancellationToken ct = default);
}
