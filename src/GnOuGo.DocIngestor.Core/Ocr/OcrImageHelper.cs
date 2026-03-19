using SkiaSharp;

namespace DocIngestor.Core.Ocr;

/// <summary>
/// Shared helper for OCR image pre-processing.
/// Resizes images that exceed a maximum dimension while preserving the aspect ratio,
/// and re-encodes to PNG.
/// </summary>
internal static class OcrImageHelper
{
    /// <summary>Default maximum width or height in pixels.</summary>
    internal const int DefaultMaxDimension = 3000;

    /// <summary>
    /// If the image exceeds <paramref name="maxDimension"/> pixels on either axis,
    /// it is down-scaled (preserving aspect ratio) and re-encoded as PNG.
    /// Otherwise the original bytes are returned untouched.
    /// </summary>
    internal static byte[] ResizeIfNeeded(byte[] imageBytes, int maxDimension = DefaultMaxDimension)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return imageBytes;

        SKBitmap? original;
        try
        {
            original = SKBitmap.Decode(imageBytes);
        }
        catch
        {
            return imageBytes; // can't decode → pass through
        }

        if (original is null)
            return imageBytes; // can't decode → pass through

        using var _ = original;

        var w = original.Width;
        var h = original.Height;

        if (w <= maxDimension && h <= maxDimension)
            return imageBytes; // already within limits

        // Compute new size keeping aspect ratio
        var ratio = Math.Min((double)maxDimension / w, (double)maxDimension / h);
        var newW = (int)Math.Round(w * ratio);
        var newH = (int)Math.Round(h * ratio);

        using var resized = original.Resize(new SKImageInfo(newW, newH), SKSamplingOptions.Default);
        if (resized is null)
            return imageBytes; // resize failed → pass through

        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);

        return data.ToArray();
    }
}


