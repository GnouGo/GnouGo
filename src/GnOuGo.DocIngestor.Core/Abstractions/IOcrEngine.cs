namespace DocIngestor.Core.Abstractions;

/// <summary>Controls optical character recognition for one image.</summary>
/// <param name="Language">OCR language code.</param>
/// <param name="Dpi">Optional source resolution.</param>
public sealed record OcrOptions(
    string Language = "eng",
    int? Dpi = 300
);

/// <summary>Recognizes text in image content.</summary>
public interface IOcrEngine
{
    /// <summary>Runs OCR on an image (preferably PNG/JPEG bytes).</summary>
    ValueTask<string> RecognizeAsync(byte[] imageBytes, OcrOptions options, CancellationToken ct = default);
}
