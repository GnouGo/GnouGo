using System.Diagnostics;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using GnOuGo.Auth.Core;
using GnOuGo.AI.Core;
using DocIngestor.Core.Formatting;
using DocIngestor.Core.Telemetry;

namespace DocIngestor.Core.Embeddings;

public sealed class OpenAiCompatibleEmbeddingModel : IEmbeddingModel
{
    public string Name { get; }
    public int Dimensions => _dims > 0 ? _dims : _defaultDims;

    private readonly HttpClient _http;
    private readonly IApiKeyProvider _apiKeyProvider;
    private readonly string _endpointUrl;
    private readonly string _model;
    private readonly int _defaultDims;
    private int _dims;
    private readonly GenAiTelemetry? _telemetry;

    public OpenAiCompatibleEmbeddingModel(
        string name,
        string endpointUrl,
        string model,
        IApiKeyProvider apiKeyProvider,
        HttpClient http,
        int defaultDims = 3072,
        GenAiTelemetry? telemetry = null)
    {
        Name = name;
        _endpointUrl = endpointUrl ?? "";
        _model = model ?? "";
        _apiKeyProvider = apiKeyProvider;
        _http = http;
        _defaultDims = defaultDims;
        _telemetry = telemetry;
    }

    public async ValueTask<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpointUrl))
            throw new InvalidOperationException("Embedding endpoint_url is required (e.g. https://... or https://.../v1).");

        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("Embedding model name is required.");

        var url = OpenAiEndpoints.Embeddings(_endpointUrl);
        var normalized = TextNormalization.NormalizeWhitespaceForEmbedding(text ?? "");

        using var activity = _telemetry?.StartEmbeddingActivity(_model, "openai");
        if (activity is not null)
        {
            var preview = normalized.Length > 500 ? normalized[..500] + "…" : normalized;
            var tags = new ActivityTagsCollection
            {
                ["gen_ai.content.prompt"] = preview,
                ["gen_ai.content.prompt.length"] = normalized.Length,
            };
            activity.AddEvent(new ActivityEvent("gen_ai.content.prompt", tags: tags));
        }
        var startTime = Stopwatch.GetTimestamp();

        try
        {
            byte[] payload = EmbeddingRequestBuilder.OpenAi(_model, normalized);
            var estimatedInputTokens = normalized.Length / 4;

            using var req = HttpRequestHelper.CreateJsonPost(url, payload);
            var apiKey = await _apiKeyProvider.GetApiKeyAsync(ct);
            HttpRequestHelper.SetBearerAuth(req, apiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
                var errorMessage = $"Embeddings call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}";
                
                var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                _telemetry?.CompleteEmbeddingActivity(activity, estimatedInputTokens, 0, false, errorMessage);
                _telemetry?.RecordEmbeddingMetrics(_model, "openai", estimatedInputTokens, duration, false);
                
                throw new InvalidOperationException(errorMessage);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var vec = EmbeddingResponseParser.ParseOpenAiSingle(json.RootElement);
            _dims = vec.Length;

            var durationSeconds = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteEmbeddingActivity(activity, estimatedInputTokens, vec.Length, true);
            _telemetry?.RecordEmbeddingMetrics(_model, "openai", estimatedInputTokens, durationSeconds, true);

            return vec;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            var estimatedTokens = normalized.Length / 4;
            
            _telemetry?.CompleteEmbeddingActivity(activity, estimatedTokens, 0, false, ex.Message);
            _telemetry?.RecordEmbeddingMetrics(_model, "openai", estimatedTokens, duration, false);
            
            throw;
        }
    }

    /// <summary>
    /// Embed multiple texts in a single API call (OpenAI supports input as string[]).
    /// Batches are split into groups of <see cref="MaxBatchSize"/> to stay within API limits.
    /// </summary>
    private const int MaxBatchSize = 2048;

    public async ValueTask<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();
        if (texts.Count == 1)
        {
            var single = await EmbedAsync(texts[0], ct);
            return new[] { single };
        }

        if (string.IsNullOrWhiteSpace(_endpointUrl))
            throw new InvalidOperationException("Embedding endpoint_url is required.");
        if (string.IsNullOrWhiteSpace(_model))
            throw new InvalidOperationException("Embedding model name is required.");

        var url = OpenAiEndpoints.Embeddings(_endpointUrl);
        var allResults = new float[texts.Count][];

        for (int batchStart = 0; batchStart < texts.Count; batchStart += MaxBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            int batchEnd = Math.Min(batchStart + MaxBatchSize, texts.Count);
            var batchTexts = new List<string>(batchEnd - batchStart);
            for (int j = batchStart; j < batchEnd; j++)
                batchTexts.Add(TextNormalization.NormalizeWhitespaceForEmbedding(texts[j] ?? ""));

            using var activity = _telemetry?.StartEmbeddingActivity(_model, "openai");
            if (activity is not null)
            {
                var first = batchTexts[0].Length > 200 ? batchTexts[0][..200] + "…" : batchTexts[0];
                var last = batchTexts.Count > 1
                    ? (batchTexts[^1].Length > 200 ? batchTexts[^1][..200] + "…" : batchTexts[^1])
                    : first;
                var tags = new ActivityTagsCollection
                {
                    ["gen_ai.content.prompt.first"] = first,
                    ["gen_ai.content.prompt.last"] = last,
                    ["gen_ai.content.prompt.count"] = batchTexts.Count,
                    ["gen_ai.content.prompt.total_chars"] = batchTexts.Sum(t => t.Length),
                };
                activity.AddEvent(new ActivityEvent("gen_ai.content.prompt", tags: tags));
            }
            var startTime = Stopwatch.GetTimestamp();
            int estimatedInputTokens = batchTexts.Sum(t => t.Length / 4);

            try
            {
                byte[] payload = EmbeddingRequestBuilder.OpenAiBatch(_model, batchTexts);

                using var req = HttpRequestHelper.CreateJsonPost(url, payload);
                var apiKey = await _apiKeyProvider.GetApiKeyAsync(ct);
                HttpRequestHelper.SetBearerAuth(req, apiKey);

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!resp.IsSuccessStatusCode)
                {
                    var body = await HttpRequestHelper.ReadErrorBodyAsync(resp, ct);
                    var errorMessage = $"Embeddings batch call failed: {(int)resp.StatusCode} {resp.ReasonPhrase ?? ""} - {body}";

                    var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                    _telemetry?.CompleteEmbeddingActivity(activity, estimatedInputTokens, 0, false, errorMessage);
                    _telemetry?.RecordEmbeddingMetrics(_model, "openai", estimatedInputTokens, duration, false);

                    throw new InvalidOperationException(errorMessage);
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var batchResults = EmbeddingResponseParser.ParseOpenAi(json.RootElement, batchTexts.Count);
                for (int i = 0; i < batchResults.Length; i++)
                {
                    allResults[batchStart + i] = batchResults[i];
                    _dims = batchResults[i].Length;
                }

                var durationSeconds = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                _telemetry?.CompleteEmbeddingActivity(activity, estimatedInputTokens, _dims, true);
                _telemetry?.RecordEmbeddingMetrics(_model, "openai", estimatedInputTokens, durationSeconds, true);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                _telemetry?.CompleteEmbeddingActivity(activity, estimatedInputTokens, 0, false, ex.Message);
                _telemetry?.RecordEmbeddingMetrics(_model, "openai", estimatedInputTokens, duration, false);
                throw;
            }
        }

        return allResults;
    }
}
