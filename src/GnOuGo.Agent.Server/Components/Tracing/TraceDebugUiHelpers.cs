using System.Globalization;
using System.Text.Json;
using GnOuGo.Agent.Server.Telemetry;
using GnOuGo.AI.Core;
namespace GnOuGo.Agent.Server.Components.Tracing;
internal static class TraceDebugUiHelpers
{
    public static List<FlatSpanModel> BuildTimeline(TraceGroupDto trace)
    {
        var spans = trace.Spans;
        if (spans.Count == 0)
            return [];

        var spanById = spans
            .GroupBy(span => NormalizeSpanId(span.SpanId), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(span => span.StartUtc).First(),
                StringComparer.OrdinalIgnoreCase);

        var byParent = spans
            .GroupBy(span => NormalizeSpanId(span.ParentSpanId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(span => span.StartUtc).ThenBy(span => span.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var roots = spans
            .Where(span =>
            {
                var parentId = NormalizeSpanId(span.ParentSpanId);
                return string.IsNullOrWhiteSpace(parentId) || !spanById.ContainsKey(parentId);
            })
            .OrderBy(span => span.StartUtc)
            .ThenBy(span => span.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (roots.Count == 0)
        {
            roots = spans
                .OrderBy(span => span.StartUtc)
                .ThenBy(span => span.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var flat = new List<FlatSpanModel>(spans.Count);
        foreach (var root in roots)
            Visit(root, 0, flat, byParent, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (flat.Count == 0)
            return [];

        var minTime = flat.Min(item => item.StartMs);
        var maxTime = flat.Max(item => item.EndMs);
        var totalMs = Math.Max(1d, maxTime - minTime);
        return flat.Select(item => item with
        {
            LeftPercent = (item.StartMs - minTime) * 100d / totalMs,
            WidthPercent = Math.Max(1d, item.EndMs - item.StartMs) * 100d / totalMs
        }).ToList();

        static void Visit(
            TraceSpanDto span,
            int depth,
            List<FlatSpanModel> output,
            Dictionary<string, List<TraceSpanDto>> byParent,
            HashSet<string> activePath)
        {
            var spanId = NormalizeSpanId(span.SpanId);
            if (!string.IsNullOrWhiteSpace(spanId) && !activePath.Add(spanId))
                return;

            var startMs = span.StartUtc.UtcDateTime.Subtract(DateTime.UnixEpoch).TotalMilliseconds;
            var endMs = span.EndUtc.UtcDateTime.Subtract(DateTime.UnixEpoch).TotalMilliseconds;
            if (endMs <= startMs)
                endMs = startMs + Math.Max(1d, span.DurationMs);

            var children = !string.IsNullOrWhiteSpace(spanId) && byParent.TryGetValue(spanId, out var childSpans)
                ? childSpans
                : [];
            output.Add(new FlatSpanModel(span, depth, children.Count, startMs, endMs, Math.Max(0d, endMs - startMs), 0d, 0d));
            foreach (var child in children)
                Visit(child, depth + 1, output, byParent, activePath);

            if (!string.IsNullOrWhiteSpace(spanId))
                activePath.Remove(spanId);
        }

        static string NormalizeSpanId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return value.Trim().ToLowerInvariant();
        }
    }
    public static SummaryModel BuildSummary(TraceGroupDto trace)
    {
        var llmMetrics = trace.Spans
            .Where(IsGenAiSpan)
            .Select(span => new LlmCallModel(
                OperationName: GetFirstAttribute(span.Attributes, "gen_ai.operation.name", "llm.operation.name") ?? span.Name,
                Model: GetFirstAttribute(span.Attributes, "gen_ai.request.model", "gen_ai.response.model", "llm.request.model") ?? "unknown",
                Provider: ResolveProvider(span.Attributes),
                PromptTokens: GetLongAttribute(span.Attributes, "gen_ai.usage.prompt_tokens", "gen_ai.usage.input_tokens", "llm.usage.prompt_tokens"),
                CompletionTokens: GetLongAttribute(span.Attributes, "gen_ai.usage.completion_tokens", "gen_ai.usage.output_tokens", "llm.usage.completion_tokens"),
                TotalTokens: GetLongAttribute(span.Attributes, "gen_ai.usage.total_tokens", "llm.usage.total_tokens"),
                DurationMs: GetSpanDurationMs(span),
                Cost: ResolveCost(span.Attributes)))
            .Select(metric => metric.TotalTokens > 0
                ? metric
                : metric with { TotalTokens = metric.PromptTokens + metric.CompletionTokens })
            .ToList();
        return new SummaryModel(
            TotalTokens: llmMetrics.Sum(metric => metric.TotalTokens),
            PromptTokens: llmMetrics.Sum(metric => metric.PromptTokens),
            CompletionTokens: llmMetrics.Sum(metric => metric.CompletionTokens),
            EstimatedCost: llmMetrics.Sum(metric => metric.Cost),
            TraceDurationMs: Math.Max(0d, (trace.EndUtc - trace.StartUtc).TotalMilliseconds),
            LlmCalls: llmMetrics.Count,
            LlmMetrics: llmMetrics,
            Providers: BuildDistinctTags(llmMetrics.Select(metric => metric.Provider)),
            Models: BuildDistinctTags(llmMetrics.Select(metric => metric.Model)),
            RagSteps: BuildRagSteps(trace.Spans));
    }
    public static bool IsGenAiSpan(TraceSpanDto span)
        => !string.IsNullOrWhiteSpace(GetFirstAttribute(span.Attributes, "gen_ai.request.model", "gen_ai.response.model", "llm.request.model"));
    public static double GetSpanDurationMs(TraceSpanDto span)
        => span.DurationMs > 0 ? span.DurationMs : Math.Max(0d, (span.EndUtc - span.StartUtc).TotalMilliseconds);
    public static string GetStatusLabel(int statusCode)
        => statusCode switch { 1 => "OK", 2 => "ERROR", _ => "UNSET" };
    public static string GetKindLabel(int kind)
        => kind switch
        {
            1 => "INTERNAL",
            2 => "SERVER",
            3 => "CLIENT",
            4 => "PRODUCER",
            5 => "CONSUMER",
            _ => "UNSPECIFIED"
        };
    public static string GetSeverityLabel(TraceLogDto log)
    {
        if (!string.IsNullOrWhiteSpace(log.SeverityText))
            return log.SeverityText;
        return log.SeverityNumber switch
        {
            >= 1 and <= 4 => "TRACE",
            >= 5 and <= 8 => "DEBUG",
            >= 9 and <= 12 => "INFO",
            >= 13 and <= 16 => "WARN",
            >= 17 and <= 20 => "ERROR",
            >= 21 and <= 24 => "FATAL",
            _ => "UNSPECIFIED"
        };
    }
    public static string GetSeverityClass(TraceLogDto log)
        => log.SeverityNumber switch
        {
            >= 1 and <= 4 => "trace",
            >= 5 and <= 8 => "debug",
            >= 9 and <= 12 => "info",
            >= 13 and <= 16 => "warn",
            >= 17 and <= 20 => "error",
            >= 21 and <= 24 => "fatal",
            _ => "unspecified"
        };
    public static string GetSpanIcon(TraceSpanDto span)
    {
        if (IsGenAiSpan(span)) return "AI";
        if (ContainsAny(span.Name, "search", "retrieval", "query")) return "Search";
        if (ContainsAny(span.Name, "embed")) return "Embed";
        if (ContainsAny(span.Name, "chunk")) return "Chunk";
        if (ContainsAny(span.Name, "store", "upsert")) return "Store";
        return span.StatusCode == 2 ? "Error" : "Span";
    }
    public static string BuildWidthStyle(double percentage)
        => $"width:{percentage.ToString("0.##", CultureInfo.InvariantCulture)}%";
    public static string BuildPaddingLeftStyle(int depth)
        => $"padding-left:{depth * 12 + 8}px";
    public static string BuildTimelineDepthStyle(int depth)
    {
        var clampedDepth = Math.Clamp(depth, 0, 8);
        var alpha = Math.Min(0.34d, clampedDepth * 0.035d);
        var offset = clampedDepth * 10 + 18;
        return $"--trace-depth:{clampedDepth.ToString(CultureInfo.InvariantCulture)};--trace-depth-alpha:{alpha.ToString("0.###", CultureInfo.InvariantCulture)};--trace-depth-offset:{offset.ToString(CultureInfo.InvariantCulture)}px";
    }
    public static string BuildTimelineBarStyle(FlatSpanModel item)
        => $"left:{item.LeftPercent.ToString("0.##", CultureInfo.InvariantCulture)}%;width:{item.WidthPercent.ToString("0.##", CultureInfo.InvariantCulture)}%;background:{GetSpanColor(item.Span)}";
    public static string FormatDuration(double milliseconds)
    {
        if (milliseconds <= 0)
            return "< 1ms";
        if (milliseconds < 1)
            return $"{milliseconds * 1000d:0}us";
        if (milliseconds < 1000)
            return $"{milliseconds:0.0}ms";
        return $"{milliseconds / 1000d:0.00}s";
    }
    public static string ToDisplayString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            JsonElement json => json.ValueKind switch
            {
                JsonValueKind.Null => string.Empty,
                JsonValueKind.String => json.GetString() ?? string.Empty,
                JsonValueKind.Object or JsonValueKind.Array => json.GetRawText(),
                _ => json.ToString()
            },
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }
    private static List<RagStepModel> BuildRagSteps(List<TraceSpanDto> spans)
    {
        var categories = new (string Name, string Icon, Func<TraceSpanDto, bool> Predicate)[]
        {
            ("Retrieval", "Search", span => ContainsAny(span.Name, "search", "retrieval", "query")),
            ("Embedding", "Embed", span => ContainsAny(span.Name, "embed") || string.Equals(GetFirstAttribute(span.Attributes, "gen_ai.operation.name"), "embeddings", StringComparison.OrdinalIgnoreCase)),
            ("Generation", "AI", IsGenAiSpan),
            ("Chunking", "Chunk", span => ContainsAny(span.Name, "chunk")),
            ("Storage", "Store", span => ContainsAny(span.Name, "store", "upsert"))
        };
        var steps = new List<RagStepModel>();
        foreach (var category in categories)
        {
            var matches = spans.Where(category.Predicate).ToList();
            if (matches.Count == 0)
                continue;
            steps.Add(new RagStepModel(category.Name, category.Icon, matches.Count, matches.Sum(GetSpanDurationMs), 0d));
        }
        var total = steps.Sum(step => step.DurationMs);
        if (total > 0)
        {
            for (var i = 0; i < steps.Count; i++)
                steps[i] = steps[i] with { Percentage = steps[i].DurationMs * 100d / total };
        }
        return steps;
    }
    private static List<string> BuildDistinctTags(IEnumerable<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    private static string ResolveProvider(Dictionary<string, object?> attributes)
    {
        var model = GetFirstAttribute(attributes, "gen_ai.request.model", "gen_ai.response.model", "llm.request.model") ?? string.Empty;
        var system = GetFirstAttribute(attributes, "gen_ai.system")?.ToLowerInvariant();
        if (system is "openai" or "ollama" or "anthropic")
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(system);
        if (model.Contains("gpt", StringComparison.OrdinalIgnoreCase))
            return "OpenAI";
        if (model.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return "Anthropic";
        if (model.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            return "Google";
        return string.IsNullOrWhiteSpace(system) ? "unknown" : system;
    }
    private static string GetSpanColor(TraceSpanDto span)
    {
        if (span.StatusCode == 2) return "#ef4444";
        if (IsGenAiSpan(span)) return "#8b5cf6";
        if (ContainsAny(span.Name, "search", "retrieval", "query")) return "#06b6d4";
        if (ContainsAny(span.Name, "embed")) return "#10b981";
        if (ContainsAny(span.Name, "chunk")) return "#f59e0b";
        return "#64748b";
    }
    private static bool ContainsAny(string? value, params string[] terms)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var term in terms)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
    private static string? GetFirstAttribute(Dictionary<string, object?> attributes, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (attributes.TryGetValue(key, out var value))
            {
                var text = ToDisplayString(value);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        return null;
    }
    private static long GetLongAttribute(Dictionary<string, object?> attributes, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!attributes.TryGetValue(key, out var value))
                continue;
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
                    return number;
                if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                    return number;
            }
            else if (long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }
        return 0L;
    }
    private static decimal GetDecimalAttribute(Dictionary<string, object?> attributes, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!attributes.TryGetValue(key, out var value))
                continue;
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
                    return number;
                if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                    return number;
            }
            else if (decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }
        return 0m;
    }

    private static decimal ResolveCost(Dictionary<string, object?> attributes)
    {
        var explicitCost = GetDecimalAttribute(attributes, "gen_ai.usage.cost");
        if (explicitCost > 0m)
            return explicitCost;

        var model = GetFirstAttribute(attributes, "gen_ai.request.model", "gen_ai.response.model", "llm.request.model");
        if (string.IsNullOrWhiteSpace(model))
            return explicitCost;

        var inputTokens = GetLongAttribute(attributes, "gen_ai.usage.prompt_tokens", "gen_ai.usage.input_tokens", "llm.usage.prompt_tokens");
        var outputTokens = GetLongAttribute(attributes, "gen_ai.usage.completion_tokens", "gen_ai.usage.output_tokens", "llm.usage.completion_tokens");
        if (inputTokens <= 0 && outputTokens <= 0)
            return explicitCost;

        var providerType = GetFirstAttribute(attributes, "gen_ai.system");
        return ModelMetadataCatalog.EstimateCost(model, inputTokens, outputTokens, providerType: providerType) ?? explicitCost;
    }
}
