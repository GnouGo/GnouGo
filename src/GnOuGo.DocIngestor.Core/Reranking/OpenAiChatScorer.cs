using System.Diagnostics;
using System.Text.Json;
using DocIngestor.Core.Abstractions;
using GnOuGo.Auth.Core;
using GnOuGo.AI.Core;
using DocIngestor.Core.Telemetry;

namespace DocIngestor.Core.Reranking;

/// <summary>
/// Scores (query, passage) relevance via an OpenAI-compatible Chat Completions API.
/// Uses the model as a cross-encoder by asking it to output a relevance score from 0 to 10.
/// </summary>
public sealed class OpenAiChatScorer : IChatScorer
{
    /// <inheritdoc />
    public string Name => "openai";

    private readonly HttpClient _http;
    private readonly ModelProviderOptions _transportOptions;
    private readonly IApiKeyProvider _apiKeyProvider;
    private readonly string _endpointUrl;
    private readonly string _model;
    private readonly GenAiTelemetry? _telemetry;

    /// <summary>Initializes an OpenAI-compatible passage relevance scorer.</summary>
    /// <param name="endpointUrl">Compatible chat-completions endpoint URL.</param>
    /// <param name="model">Chat model name.</param>
    /// <param name="apiKeyProvider">Provider for the API credential.</param>
    /// <param name="http">HTTP client used for requests.</param>
    /// <param name="telemetry">Optional telemetry recorder.</param>
    /// <param name="providerOptions">Common generation transport policy and exact model overrides; the constructor endpoint remains authoritative.</param>
    public OpenAiChatScorer(
        string endpointUrl,
        string model,
        IApiKeyProvider apiKeyProvider,
        HttpClient http,
        GenAiTelemetry? telemetry = null,
        ModelProviderOptions? providerOptions = null)
    {
        _endpointUrl = endpointUrl;
        _model = model;
        _apiKeyProvider = apiKeyProvider;
        _http = http;
        _telemetry = telemetry;
        _transportOptions = new ModelProviderOptions
        {
            Type = "openai", Url = _endpointUrl,
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
            new LLMModelMetadataResolver(new LLMOptions()).ResolveDeclaredMetadata("openai", _model)?.Capabilities);
        var url = OpenAiEndpoints.ChatCompletions(_endpointUrl);
        var truncated = passage.Length > 2000 ? passage[..2000] + "…" : passage;
        var userMessage = $"Query: {query}\n\nPassage: {truncated}";

        byte[] payload = ChatRequestBuilder.OpenAi(_model, SystemPrompt, userMessage, maxTokens: 4, temperature: 0);

        var estimatedInputTokens = (SystemPrompt.Length + userMessage.Length) / 4;
        const int estimatedOutputTokens = 2;

        using var activity = _telemetry?.StartChatScoringActivity(_model, "openai");
        var startTime = Stopwatch.GetTimestamp();

        try
        {
            var apiKey = await _apiKeyProvider.GetApiKeyAsync(ct);
            HttpRequestMessage CreateRequest()
            {
                var req = HttpRequestHelper.CreateJsonPost(url, payload);
                LLMTransportResolver.Apply(req, transport);
                HttpRequestHelper.SetBearerAuth(req, apiKey);
                return req;
            }
            using var resp = await HttpRequestHelper.SendGenerationAsync(_http, CreateRequest, _transportOptions.RetryPolicy, ct);
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var durationSeconds = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
                _telemetry?.CompleteChatScoringActivity(activity, estimatedInputTokens, 0, 0, false, $"HTTP {(int)resp.StatusCode}");
                _telemetry?.RecordChatScoringMetrics(_model, "openai", estimatedInputTokens, 0, durationSeconds, false);
                return 0;
            }

            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var content = ChatResponseParser.ExtractOpenAiContent(json.RootElement);

            var score = ScoreParser.Parse(content);

            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteChatScoringActivity(activity, estimatedInputTokens, estimatedOutputTokens, score, true);
            _telemetry?.RecordChatScoringMetrics(_model, "openai", estimatedInputTokens, estimatedOutputTokens, duration, true);

            return score;
        }
        catch (Exception ex) when (activity != null)
        {
            var duration = Stopwatch.GetElapsedTime(startTime).TotalSeconds;
            _telemetry?.CompleteChatScoringActivity(activity, estimatedInputTokens, 0, 0, false, ex.Message);
            _telemetry?.RecordChatScoringMetrics(_model, "openai", estimatedInputTokens, 0, duration, false);
            throw;
        }
    }

    // ── Shared ───────────────────────────────────────────────────────

    internal const string SystemPrompt =
        """
        You are a relevance scoring engine. Given a search query and a text passage,
        output ONLY a single integer from 0 to 10 representing how relevant the passage
        is to the query.

        Scoring guide:
        0 = completely irrelevant
        1-3 = marginally related, different topic
        4-6 = somewhat relevant, partially answers the query
        7-9 = highly relevant, directly addresses the query
        10 = perfect match, fully answers the query

        Output ONLY the number, nothing else.
        """;
}
