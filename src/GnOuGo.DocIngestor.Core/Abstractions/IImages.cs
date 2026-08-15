namespace DocIngestor.Core.Abstractions;

/// <summary>Controls image discovery, loading, sizing, and OCR during ingestion.</summary>
/// <param name="EnableImageDiscovery">Whether document images should be discovered.</param>
/// <param name="LoadImageBytes">Whether extracted artifacts include their binary content.</param>
/// <param name="EnableOcr">Whether discovered images should be sent to OCR.</param>
/// <param name="OcrLanguage">OCR language code.</param>
/// <param name="OcrDpi">Optional OCR resolution.</param>
/// <param name="MaxImagesPerSection">Maximum number of images retained per section.</param>
/// <param name="MaxImageBytes">Maximum image payload size under the normal loading policy.</param>
/// <param name="MaxWidth">Optional maximum output width.</param>
/// <param name="MaxHeight">Optional maximum output height.</param>
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

/// <summary>Describes an image discovered in a document and optionally carries its bytes.</summary>
/// <param name="Id">Stable image identifier within the document.</param>
/// <param name="PageNumber">One-based source page or slide number, when known.</param>
/// <param name="SectionId">Associated extracted section identifier, when known.</param>
/// <param name="Name">Optional source image name.</param>
/// <param name="ContentType">Optional image MIME type.</param>
/// <param name="Width">Image width in pixels or samples, when known.</param>
/// <param name="Height">Image height in pixels or samples, when known.</param>
/// <param name="LengthBytes">Image payload length, when known.</param>
/// <param name="Bytes">Optional loaded image content.</param>
/// <param name="Metadata">Format-specific image metadata.</param>
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

/// <summary>Discovers and optionally loads images from supported document formats.</summary>
public interface IImageExtractor
{
    /// <summary>Returns true when this extractor supports the given file name (and optional content type).</summary>
    bool CanHandle(string fileName, string? contentType = null);

    /// <summary>Extract images from a seekable <see cref="DocumentSource"/>.</summary>
    ValueTask<IReadOnlyList<ImageArtifact>> ExtractImagesAsync(DocumentSource source, ImageExtractionOptions options, CancellationToken ct = default);
}
