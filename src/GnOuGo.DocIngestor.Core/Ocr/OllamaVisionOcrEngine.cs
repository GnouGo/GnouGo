using System.Diagnostics;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using GnOuGo.AI.Core;
using DocIngestor.Core.Telemetry;

namespace DocIngestor.Core.Ocr;

/// <summary>
/// OCR engine that uses the Ollama local Vision API to extract text from images.
/// Requires a vision-capable model such as <c>llava</c>, <c>llava-llama3</c>, <c>moondream</c>, etc.
///
/// <para>Ollama API endpoint: <c>POST /api/chat</c></para>
/// <para>Images are sent as base64 in the <c>images</c> array field of the user message.</para>
/// </summary>
public sealed class OllamaVisionOcrEngine : IOcrEngine
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly GenAiTelemetry? _telemetry;

    /// <param name="baseUrl">Ollama base URL (e.g. "http://localhost:11434").</param>
    /// <param name="model">Vision-capable model name (e.g. "llava", "llava-llama3", "moondream").</param>
    /// <param name="http">Shared HttpClient.</param>
    /// <param name="telemetry">Optional GenAI telemetry for trace tracking.</param>
    public OllamaVisionOcrEngine(
        string baseUrl,
        string model,
        HttpClient http,
        GenAiTelemetry? telemetry = null)
    {
        _baseUrl = (baseUrl ?? "http://localhost:11434").TrimEnd('/');
        _model = model ?? "llava";
        _http = http;
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public async ValueTask<string> RecognizeAsync(byte[] imageBytes, OcrOptions options, CancellationToken ct = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return string.Empty;

        var originalSize = imageBytes.Length;

        // Down-scale large images to stay within model context limits
        imageBytes = OcrImageHelper.ResizeIfNeeded(imageBytes);

        var url = OllamaEndpoints.Chat(_baseUrl);
        var base64Image = Convert.ToBase64String(imageBytes);

        var systemPrompt = BuildSystemPrompt(options.Language);
        byte[] payload = VisionRequestBuilder.Ollama(_model, systemPrompt, base64Image);

        var estimatedInputTokens = (systemPrompt.Length / 4) + 765;

        using var activity = _telemetry?.StartOcrActivity(_model, "ollama");
        activity?.SetTag("ocr.image.original_size_bytes", originalSize);
        activity?.SetTag("ocr.image.processed_size_bytes", imageBytes.Length);
        activity?.SetTag("ocr.language", options.Language);

        var startTime = Stopwatch.GetTimestamp();

        try
        {
            using var req = HttpRequestHelper.CreateJsonPost(url, payload);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
                var errMsg = $"Ollama Vision OCR failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}";

                var errDuration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                _telemetry?.CompleteOcrActivity(activity, estimatedInputTokens, 0, originalSize, false, errMsg);
                _telemetry?.RecordOcrMetrics(_model, "ollama", estimatedInputTokens, 0, errDuration, false);

                throw new InvalidOperationException(errMsg);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var content = ChatResponseParser.ExtractOllamaContent(json.RootElement);

            var estimatedOutputTokens = Math.Max(1, content.Length / 4);

            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteOcrActivity(activity, estimatedInputTokens, estimatedOutputTokens, originalSize, true);
            _telemetry?.RecordOcrMetrics(_model, "ollama", estimatedInputTokens, estimatedOutputTokens, duration, true);

            return content;
        }
        catch (Exception ex) when (activity != null && ex is not InvalidOperationException)
        {
            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteOcrActivity(activity, estimatedInputTokens, 0, originalSize, false, ex.Message);
            _telemetry?.RecordOcrMetrics(_model, "ollama", estimatedInputTokens, 0, duration, false);
            throw;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string BuildSystemPrompt(string language) =>
        $"""
        You are an OCR engine. Extract ALL visible text from the provided image.
        Return ONLY the raw extracted text, preserving the original layout as much as possible.
        Do not add any commentary, explanation, or formatting beyond what is in the image.
        The primary language of the text is: {language}.
        If no text is visible, return an empty string.
        """;
}

