using System.Diagnostics;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using GnOuGo.AI.Core;
using DocIngestor.Core.Telemetry;

namespace DocIngestor.Core.Reranking;

/// <summary>
/// Scores (query, passage) relevance via the Ollama local Chat API (<c>/api/chat</c>).
/// Uses a local LLM (e.g. llama3.2, mistral) as a cross-encoder — no API key required.
/// </summary>
public sealed class OllamaChatScorer : IChatScorer
{
    /// <inheritdoc />
    public string Name => "ollama";

    private readonly HttpClient _http;
    private readonly ModelProviderOptions _transportOptions;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly GenAiTelemetry? _telemetry;

    /// <summary>Initializes an Ollama-backed passage relevance scorer.</summary>
    /// <param name="baseUrl">Ollama server base URL.</param>
    /// <param name="model">Chat model name.</param>
    /// <param name="http">HTTP client used for requests.</param>
    /// <param name="telemetry">Optional telemetry recorder.</param>
    /// <param name="providerOptions">Common generation transport policy and exact model overrides; the constructor endpoint remains authoritative.</param>
    public OllamaChatScorer(
        string baseUrl,
        string model,
        HttpClient http,
        GenAiTelemetry? telemetry = null,
        ModelProviderOptions? providerOptions = null)
    {
        _baseUrl = (baseUrl ?? "http://localhost:11434").TrimEnd('/');
        _model = model ?? "llama3.2";
        _http = http;
        _telemetry = telemetry;
        _transportOptions = new ModelProviderOptions
        {
            Type = "ollama", Url = _baseUrl,
            RequestPolicy = providerOptions?.RequestPolicy.Clone() ?? new(),
            RetryPolicy = providerOptions?.RetryPolicy.Clone() ?? new() { MaxAttempts = 1 },
            ModelTransportOverrides = LLMTransportResolver.CloneOverrides(providerOptions?.ModelTransportOverrides)
        };
        LLMOptionsValidation.ValidateProvider("generation", _transportOptions);
    }

    /// <inheritdoc />
    public async Task<double> ScoreAsync(string query, string passage, CancellationToken ct = default)
    {
        var transport = LLMTransportResolver.Resolve(_model, _transportOptions, false,
            new LLMModelMetadataResolver(new LLMOptions()).ResolveDeclaredMetadata("ollama", _model)?.Capabilities);
        var url = OllamaEndpoints.Chat(_baseUrl);
        var truncated = passage.Length > 2000 ? passage[..2000] + "…" : passage;
        var userMessage = $"Query: {query}\n\nPassage: {truncated}";

        byte[] payload = ChatRequestBuilder.Ollama(_model, OpenAiChatScorer.SystemPrompt, userMessage);

        var estimatedInputTokens = (OpenAiChatScorer.SystemPrompt.Length + userMessage.Length) / 4;
        const int estimatedOutputTokens = 2;

        using var activity = _telemetry?.StartChatScoringActivity(_model, "ollama");
        var startTime = Stopwatch.GetTimestamp();

        try
        {
            HttpRequestMessage CreateRequest()
            {
                var req = HttpRequestHelper.CreateJsonPost(url, payload);
                LLMTransportResolver.Apply(req, transport);
                return req;
            }
            using var resp = await HttpRequestHelper.SendGenerationAsync(_http, CreateRequest, _transportOptions.RetryPolicy, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var durationSeconds = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                _telemetry?.CompleteChatScoringActivity(activity, estimatedInputTokens, 0, 0, false, $"HTTP {(int)resp.StatusCode}");
                _telemetry?.RecordChatScoringMetrics(_model, "ollama", estimatedInputTokens, 0, durationSeconds, false);
                return 0;
            }

            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var content = ChatResponseParser.ExtractOllamaContent(json.RootElement);

            var score = ScoreParser.Parse(content);

            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteChatScoringActivity(activity, estimatedInputTokens, estimatedOutputTokens, score, true);
            _telemetry?.RecordChatScoringMetrics(_model, "ollama", estimatedInputTokens, estimatedOutputTokens, duration, true);

            return score;
        }
        catch (Exception ex) when (activity != null)
        {
            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteChatScoringActivity(activity, estimatedInputTokens, 0, 0, false, ex.Message);
            _telemetry?.RecordChatScoringMetrics(_model, "ollama", estimatedInputTokens, 0, duration, false);
            throw;
        }
    }
}
