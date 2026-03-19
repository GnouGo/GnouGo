namespace DocIngestor.Core.Abstractions;

public sealed record OcrOptions(
    string Language = "eng",
    int? Dpi = 300
);

public interface IOcrEngine
{
    /// <summary>Runs OCR on an image (preferably PNG/JPEG bytes).</summary>
    ValueTask<string> RecognizeAsync(byte[] imageBytes, OcrOptions options, CancellationToken ct = default);
}
