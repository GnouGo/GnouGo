using System.Diagnostics;
using System.Diagnostics.Metrics;
using GnOuGo.AI.Core;

namespace DocIngestor.Core.Telemetry;

/// <summary>
/// OpenTelemetry instrumentation for GenAI operations (embeddings, chat scoring).
/// Follows the semantic conventions for GenAI:
/// https://opentelemetry.io/docs/specs/semconv/gen-ai/
/// </summary>
public sealed class GenAiTelemetry : IDisposable
{
    private static readonly ActivitySource ActivitySource = new("DocIngestor.GenAI", "1.0.0");
    private static readonly Meter Meter = new("DocIngestor.GenAI", "1.0.0");

    // Metrics selon les conventions GenAI
    private readonly Counter<long> _tokenUsageCounter;
    private readonly Histogram<double> _operationDurationHistogram;
    private readonly Counter<long> _requestCounter;
    private readonly Counter<double> _costCounter;

    public GenAiTelemetry()
    {
        // gen_ai.client.token.usage (nombre de tokens)
        _tokenUsageCounter = Meter.CreateCounter<long>(
            "gen_ai.client.token.usage",
            unit: "{token}",
            description: "Number of tokens used in GenAI operations");

        // gen_ai.client.operation.duration (durée en secondes)
        _operationDurationHistogram = Meter.CreateHistogram<double>(
            "gen_ai.client.operation.duration",
            unit: "s",
            description: "Duration of GenAI operations");

        // gen_ai.client.request.count (nombre de requêtes)
        _requestCounter = Meter.CreateCounter<long>(
            "gen_ai.client.request.count",
            description: "Number of GenAI requests");

        // gen_ai.client.usage.cost (coût estimé en USD)
        _costCounter = Meter.CreateCounter<double>(
            "gen_ai.client.usage.cost",
            unit: "USD",
            description: "Estimated cost of GenAI operations in USD");
    }

    // ── Embedding telemetry ──────────────────────────────────────────

    /// <summary>
    /// Crée une activité (trace) pour une opération d'embedding.
    /// </summary>
    public Activity? StartEmbeddingActivity(string modelName, string systemName = "openai")
    {
        var activity = ActivitySource.StartActivity("gen_ai.embedding", ActivityKind.Client);
        if (activity == null) return null;

        // Attributs sémantiques GenAI
        activity.SetTag("gen_ai.operation.name", "embedding");
        activity.SetTag("gen_ai.system", systemName);
        activity.SetTag("gen_ai.request.model", modelName);
        
        return activity;
    }

    /// <summary>
    /// Enregistre les métriques d'une opération d'embedding.
    /// </summary>
    public void RecordEmbeddingMetrics(
        string modelName,
        string systemName,
        int inputTokens,
        double durationSeconds,
        bool success = true)
    {
        var tags = new TagList
        {
            { "gen_ai.operation.name", "embedding" },
            { "gen_ai.system", systemName },
            { "gen_ai.request.model", modelName },
            { "gen_ai.response.finish_reason", success ? "success" : "error" }
        };

        // Tokens utilisés
        _tokenUsageCounter.Add(inputTokens, tags);

        // Durée
        _operationDurationHistogram.Record(durationSeconds, tags);

        // Nombre de requêtes
        _requestCounter.Add(1, tags);

        // Enregistre le coût estimé
        if (success)
        {
            var cost = ModelMetadataCatalog.EstimateCost(modelName, inputTokens);
            if (cost.HasValue && cost.Value > 0)
                _costCounter.Add((double)cost.Value, tags);
        }
    }

    /// <summary>
    /// Complète une activité d'embedding avec les détails de réponse.
    /// </summary>
    public void CompleteEmbeddingActivity(
        Activity? activity,
        int inputTokens,
        int dimensions,
        bool success = true,
        string? errorMessage = null)
    {
        if (activity == null) return;

        // Attributs de requête/réponse
        activity.SetTag("gen_ai.usage.input_tokens", inputTokens);
        activity.SetTag("gen_ai.response.dimensions", dimensions);
        activity.SetTag("gen_ai.response.finish_reason", success ? "success" : "error");

        // Ajoute le coût à la trace
        var modelName = activity.GetTagItem("gen_ai.request.model") as string;
        if (success && modelName != null)
        {
            var cost = ModelMetadataCatalog.EstimateCost(modelName, inputTokens);
            if (cost.HasValue)
                activity.SetTag("gen_ai.usage.cost", (double)cost.Value);
        }

        if (!success && errorMessage != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorMessage);
            activity.SetTag("error.type", "embedding_error");
            activity.SetTag("error.message", errorMessage);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }

    // ── Chat scoring telemetry ───────────────────────────────────────

    /// <summary>
    /// Crée une activité (trace) pour une opération de chat scoring (cross-encoder reranking).
    /// </summary>
    public Activity? StartChatScoringActivity(string modelName, string systemName = "openai")
    {
        var activity = ActivitySource.StartActivity("gen_ai.chat_scoring", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag("gen_ai.operation.name", "chat_scoring");
        activity.SetTag("gen_ai.system", systemName);
        activity.SetTag("gen_ai.request.model", modelName);

        return activity;
    }

    /// <summary>
    /// Enregistre les métriques d'une opération de chat scoring.
    /// </summary>
    public void RecordChatScoringMetrics(
        string modelName,
        string systemName,
        int inputTokens,
        int outputTokens,
        double durationSeconds,
        bool success = true)
    {
        var tags = new TagList
        {
            { "gen_ai.operation.name", "chat_scoring" },
            { "gen_ai.system", systemName },
            { "gen_ai.request.model", modelName },
            { "gen_ai.response.finish_reason", success ? "success" : "error" }
        };

        _tokenUsageCounter.Add(inputTokens + outputTokens, tags);
        _operationDurationHistogram.Record(durationSeconds, tags);
        _requestCounter.Add(1, tags);

        if (success)
        {
            var cost = ModelMetadataCatalog.EstimateCost(modelName, inputTokens, outputTokens);
            if (cost.HasValue && cost.Value > 0)
                _costCounter.Add((double)cost.Value, tags);
        }
    }

    /// <summary>
    /// Complète une activité de chat scoring avec les détails de réponse.
    /// </summary>
    public void CompleteChatScoringActivity(
        Activity? activity,
        int inputTokens,
        int outputTokens,
        double score,
        bool success = true,
        string? errorMessage = null)
    {
        if (activity == null) return;

        activity.SetTag("gen_ai.usage.input_tokens", inputTokens);
        activity.SetTag("gen_ai.usage.output_tokens", outputTokens);
        activity.SetTag("gen_ai.scoring.result", score);
        activity.SetTag("gen_ai.response.finish_reason", success ? "success" : "error");

        var modelName = activity.GetTagItem("gen_ai.request.model") as string;
        if (success && modelName != null)
        {
            var cost = ModelMetadataCatalog.EstimateCost(modelName, inputTokens, outputTokens);
            if (cost.HasValue)
                activity.SetTag("gen_ai.usage.cost", (double)cost.Value);
        }

        if (!success && errorMessage != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorMessage);
            activity.SetTag("error.type", "chat_scoring_error");
            activity.SetTag("error.message", errorMessage);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }

    // ── OCR (Vision) telemetry ─────────────────────────────────────────

    /// <summary>
    /// Crée une activité (trace) pour une opération OCR via un modèle Vision.
    /// </summary>
    public Activity? StartOcrActivity(string modelName, string systemName = "openai")
    {
        var activity = ActivitySource.StartActivity("gen_ai.ocr", ActivityKind.Client);
        if (activity == null) return null;

        activity.SetTag("gen_ai.operation.name", "ocr");
        activity.SetTag("gen_ai.system", systemName);
        activity.SetTag("gen_ai.request.model", modelName);

        return activity;
    }

    /// <summary>
    /// Enregistre les métriques d'une opération OCR Vision.
    /// </summary>
    public void RecordOcrMetrics(
        string modelName,
        string systemName,
        int inputTokens,
        int outputTokens,
        double durationSeconds,
        bool success = true)
    {
        var tags = new TagList
        {
            { "gen_ai.operation.name", "ocr" },
            { "gen_ai.system", systemName },
            { "gen_ai.request.model", modelName },
            { "gen_ai.response.finish_reason", success ? "success" : "error" }
        };

        _tokenUsageCounter.Add(inputTokens + outputTokens, tags);
        _operationDurationHistogram.Record(durationSeconds, tags);
        _requestCounter.Add(1, tags);

        if (success)
        {
            var cost = ModelMetadataCatalog.EstimateCost(modelName, inputTokens, outputTokens);
            if (cost.HasValue && cost.Value > 0)
                _costCounter.Add((double)cost.Value, tags);
        }
    }

    /// <summary>
    /// Complète une activité OCR Vision avec les détails de réponse.
    /// </summary>
    public void CompleteOcrActivity(
        Activity? activity,
        int inputTokens,
        int outputTokens,
        int imageSizeBytes,
        bool success = true,
        string? errorMessage = null)
    {
        if (activity == null) return;

        activity.SetTag("gen_ai.usage.input_tokens", inputTokens);
        activity.SetTag("gen_ai.usage.output_tokens", outputTokens);
        activity.SetTag("ocr.image.size_bytes", imageSizeBytes);
        activity.SetTag("gen_ai.response.finish_reason", success ? "success" : "error");

        var modelName = activity.GetTagItem("gen_ai.request.model") as string;
        if (success && modelName != null)
        {
            var cost = ModelMetadataCatalog.EstimateCost(modelName, inputTokens, outputTokens);
            if (cost.HasValue)
                activity.SetTag("gen_ai.usage.cost", (double)cost.Value);
        }

        if (!success && errorMessage != null)
        {
            activity.SetStatus(ActivityStatusCode.Error, errorMessage);
            activity.SetTag("error.type", "ocr_error");
            activity.SetTag("error.message", errorMessage);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
    }

    public void Dispose()
    {
        // Les ActivitySource et Meter sont statiques, pas besoin de dispose
    }

    public static ActivitySource GetActivitySource() => ActivitySource;
    public static Meter GetMeter() => Meter;
}

