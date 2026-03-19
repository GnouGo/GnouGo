using System.Diagnostics;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using GnOuGo.Auth.Core;
using GnOuGo.AI.Core;
using DocIngestor.Core.Telemetry;

namespace DocIngestor.Core.Ocr;

/// <summary>
/// OCR engine that uses the OpenAI Vision API (GPT-4o / GPT-4o-mini) to extract text from images.
/// Sends the image as a base64 data URL in a vision-capable chat completion request.
/// </summary>
public sealed class OpenAiVisionOcrEngine : IOcrEngine
{
    private readonly HttpClient _http;
    private readonly IApiKeyProvider _apiKeyProvider;
    private readonly string _endpointUrl;
    private readonly string _model;
    private readonly GenAiTelemetry? _telemetry;

    /// <param name="endpointUrl">OpenAI-compatible endpoint (e.g. "https://api.openai.com/v1").</param>
    /// <param name="model">Vision-capable model name (e.g. "gpt-4o", "gpt-4o-mini").</param>
    /// <param name="apiKeyProvider">Provides the Bearer token.</param>
    /// <param name="http">Shared HttpClient.</param>
    /// <param name="telemetry">Optional GenAI telemetry for cost/trace tracking.</param>
    public OpenAiVisionOcrEngine(
        string endpointUrl,
        string model,
        IApiKeyProvider apiKeyProvider,
        HttpClient http,
        GenAiTelemetry? telemetry = null)
    {
        _endpointUrl = endpointUrl ?? "https://api.openai.com/v1";
        _model = model ?? "gpt-4o-mini";
        _apiKeyProvider = apiKeyProvider;
        _http = http;
        _telemetry = telemetry;
    }

    /// <inheritdoc />
    public async ValueTask<string> RecognizeAsync(byte[] imageBytes, OcrOptions options, CancellationToken ct = default)
    {
        if (imageBytes is null || imageBytes.Length == 0)
            return string.Empty;

        var originalSize = imageBytes.Length;

        // Down-scale large images to avoid excessive token usage / payload size
        imageBytes = OcrImageHelper.ResizeIfNeeded(imageBytes);

        var url = OpenAiEndpoints.ChatCompletions(_endpointUrl);
        var dataUrl = ImageHelper.ToDataUrl(imageBytes);

        var systemPrompt = BuildSystemPrompt(options.Language);
        byte[] payload = VisionRequestBuilder.OpenAi(_model, systemPrompt, dataUrl);

        // OpenAI Vision token estimation
        var systemTokens = systemPrompt.Length / 4;
        var imageTokens = EstimateImageTokens(imageBytes.Length);
        var estimatedInputTokens = systemTokens + imageTokens;

        using var activity = _telemetry?.StartOcrActivity(_model, "openai");
        activity?.SetTag("ocr.image.original_size_bytes", originalSize);
        activity?.SetTag("ocr.image.processed_size_bytes", imageBytes.Length);
        activity?.SetTag("ocr.language", options.Language);

        var startTime = Stopwatch.GetTimestamp();

        try
        {
            using var req = HttpRequestHelper.CreateJsonPost(url, payload);
            var apiKey = await _apiKeyProvider.GetApiKeyAsync(ct);
            HttpRequestHelper.SetBearerAuth(req, apiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
                var errMsg = $"OpenAI Vision OCR failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}";

                var errDuration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                _telemetry?.CompleteOcrActivity(activity, estimatedInputTokens, 0, originalSize, false, errMsg);
                _telemetry?.RecordOcrMetrics(_model, "openai", estimatedInputTokens, 0, errDuration, false);

                throw new InvalidOperationException(errMsg);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var content = ChatResponseParser.ExtractOpenAiContent(json.RootElement);

            var estimatedOutputTokens = Math.Max(1, content.Length / 4);

            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteOcrActivity(activity, estimatedInputTokens, estimatedOutputTokens, originalSize, true);
            _telemetry?.RecordOcrMetrics(_model, "openai", estimatedInputTokens, estimatedOutputTokens, duration, true);

            return content;
        }
        catch (Exception ex) when (activity != null && ex is not InvalidOperationException)
        {
            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteOcrActivity(activity, estimatedInputTokens, 0, originalSize, false, ex.Message);
            _telemetry?.RecordOcrMetrics(_model, "openai", estimatedInputTokens, 0, duration, false);
            throw;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static int EstimateImageTokens(int imageSizeBytes)
    {
        const int baseTiles = 1;
        var extraTiles = Math.Max(0, (imageSizeBytes - 100_000) / 50_000);
        return 85 + (baseTiles + extraTiles) * 170;
    }

    private static string BuildSystemPrompt(string language) =>
        $"""
        You are an OCR engine. Extract ALL visible text from the provided image.
        Return ONLY the raw extracted text, preserving the original layout as much as possible.
        Do not add any commentary, explanation, or formatting beyond what is in the image.
        The primary language of the text is: {language}.
        If no text is visible, return an empty string.
        """;
}

