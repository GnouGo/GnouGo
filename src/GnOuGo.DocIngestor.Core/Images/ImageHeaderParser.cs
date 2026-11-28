namespace DocIngestor.Core.Images;

/// <summary>Reads common image dimensions directly from encoded headers.</summary>
public static class ImageHeaderParser
{
    /// <summary>Returns null dimensions for unsupported or unrecognized headers.</summary>
    public static (int? width, int? height) TryGetSize(string contentType, ReadOnlySpan<byte> bytes)
        => GnOuGo.Images.EncodedImageHeader.TryGetSize(contentType, bytes);
}
