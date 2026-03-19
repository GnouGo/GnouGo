using DocIngestor.Core.Abstractions;
using DocIngestor.Core.Ocr;
using SkiaSharp;
using Xunit;

namespace DocIngestor.Tests;

public sealed class OcrImageHelperTests
{
    /// <summary>Creates a solid-color PNG of the given dimensions using SkiaSharp.</summary>
    private static byte[] CreatePng(int width, int height)
    {
        using var bmp = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.CornflowerBlue);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void SmallImage_IsNotResized()
    {
        var original = CreatePng(800, 600);
        var result = OcrImageHelper.ResizeIfNeeded(original, 3000);

        // Should return the exact same reference (untouched)
        Assert.Same(original, result);
    }

    [Fact]
    public void LargeImage_IsResized_PreservingAspectRatio()
    {
        var original = CreatePng(6000, 4000);
        var result = OcrImageHelper.ResizeIfNeeded(original, 3000);

        // Must be different bytes (resized)
        Assert.NotSame(original, result);

        // Decode and verify dimensions
        using var decoded = SKBitmap.Decode(result);
        Assert.NotNull(decoded);
        Assert.True(decoded.Width <= 3000);
        Assert.True(decoded.Height <= 3000);

        // Aspect ratio should be preserved (6000:4000 = 3:2)
        var ratio = (double)decoded.Width / decoded.Height;
        Assert.InRange(ratio, 1.45, 1.55); // ~1.5
    }

    [Fact]
    public void LargeImage_Width_IsLimitingDimension()
    {
        // 5000 wide × 1000 tall → width is the limiting factor
        var original = CreatePng(5000, 1000);
        var result = OcrImageHelper.ResizeIfNeeded(original, 3000);

        using var decoded = SKBitmap.Decode(result);
        Assert.NotNull(decoded);
        Assert.Equal(3000, decoded.Width);
        Assert.True(decoded.Height <= 3000);
    }

    [Fact]
    public void LargeImage_Height_IsLimitingDimension()
    {
        // 1000 wide × 5000 tall → height is the limiting factor
        var original = CreatePng(1000, 5000);
        var result = OcrImageHelper.ResizeIfNeeded(original, 3000);

        using var decoded = SKBitmap.Decode(result);
        Assert.NotNull(decoded);
        Assert.Equal(3000, decoded.Height);
        Assert.True(decoded.Width <= 3000);
    }

    [Fact]
    public void ExactBoundary_IsNotResized()
    {
        var original = CreatePng(3000, 3000);
        var result = OcrImageHelper.ResizeIfNeeded(original, 3000);
        Assert.Same(original, result);
    }

    [Fact]
    public void CustomMaxDimension_IsRespected()
    {
        var original = CreatePng(2000, 1000);
        var result = OcrImageHelper.ResizeIfNeeded(original, 500);

        using var decoded = SKBitmap.Decode(result);
        Assert.NotNull(decoded);
        Assert.True(decoded.Width <= 500);
        Assert.True(decoded.Height <= 500);
    }

    [Fact]
    public void EmptyBytes_ReturnsEmpty()
    {
        var result = OcrImageHelper.ResizeIfNeeded(Array.Empty<byte>());
        Assert.Empty(result);
    }

    [Fact]
    public void InvalidBytes_PassThrough()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var result = OcrImageHelper.ResizeIfNeeded(garbage);
        Assert.Same(garbage, result);
    }

    [Fact]
    public void DefaultMaxDimension_Is3000()
    {
        Assert.Equal(3000, OcrImageHelper.DefaultMaxDimension);
    }
}

