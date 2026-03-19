using DocIngestor.Core.Abstractions;

namespace DocIngestor.Core.Ocr;

/// <summary>
/// Fake OCR engine (placeholder). Always returns empty text.
/// Swap with a real OCR implementation later (e.g., Tesseract, Azure OCR, etc.).
/// </summary>
public sealed class FakeOcrEngine : IOcrEngine
{
    public ValueTask<string> RecognizeAsync(byte[] imageBytes, OcrOptions options, CancellationToken ct = default)
        => ValueTask.FromResult(string.Empty);
}
