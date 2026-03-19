using System.Text.Json;
using System.Text.Json.Serialization;
using GnOuGo.AI.Core.Telemetry;
using OpenTelemetry.Proto.Common.V1;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

public static class OtlpJson
{
    /// <summary>
    /// Options pour sérialiser les attributs OTLP : les clés doivent être préservées
    /// EXACTEMENT telles quelles (gen_ai.system, service.name, http.request.method...).
    /// On n'utilise PAS JsonSerializerDefaults.Web car il active DictionaryKeyPolicy=CamelCase
    /// qui corrompt les clés : "gen_ai.system" → "gen_Ai.system".
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Pas de DictionaryKeyPolicy : les clés de dict sont écrites telles quelles
    };

    /// <summary>
    /// Options pour lire les DTOs de l'API (camelCase toléré en lecture).
    /// </summary>
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    public static string ToJsonResource(OpenTelemetry.Proto.Resource.V1.Resource? res)
    {
        if (res is null) return "{}";
        var dict = new Dictionary<string, object?>();
        foreach (var kv in res.Attributes)
            dict[kv.Key] = AnyValueToObject(kv.Value);
        return JsonSerializer.Serialize(dict, Json);
    }

    public static string ToJsonScope(OpenTelemetry.Proto.Common.V1.InstrumentationScope? scope)
    {
        if (scope is null) return "{}";
        var dict = new Dictionary<string, object?>
        {
            ["name"]    = scope.Name,
            ["version"] = scope.Version
        };
        foreach (var kv in scope.Attributes)
            dict[kv.Key] = AnyValueToObject(kv.Value);
        return JsonSerializer.Serialize(dict, Json);
    }

    public static string ToJsonAttributes(IEnumerable<KeyValue> attrs)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var kv in attrs)
            dict[kv.Key] = AnyValueToObject(kv.Value);
        return JsonSerializer.Serialize(dict, Json);
    }

    public static string ToJsonEvents(IEnumerable<OpenTelemetry.Proto.Trace.V1.Span.Types.Event> events)
    {
        var eventList = new List<SpanEventDto>();

        foreach (var evt in events)
        {
            var attrs = new Dictionary<string, object?>();
            foreach (var kv in evt.Attributes)
                attrs[kv.Key] = AnyValueToObject(kv.Value);

            var ticks = (long)evt.TimeUnixNano / 100;
            var timeUtc = ticks > 0
                ? DateTimeOffset.FromUnixTimeSeconds(0).AddTicks(ticks)
                : DateTimeOffset.UtcNow;

            eventList.Add(new SpanEventDto(
                Name:       evt.Name,
                TimeUtc:    timeUtc,
                Attributes: attrs
            ));
        }

        return JsonSerializer.Serialize(eventList, Json);
    }

    public static object? AnyValueToObject(AnyValue v)
    {
        return v.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => v.StringValue,
            AnyValue.ValueOneofCase.BoolValue => v.BoolValue,
            AnyValue.ValueOneofCase.IntValue => v.IntValue,
            AnyValue.ValueOneofCase.DoubleValue => v.DoubleValue,
            AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(v.BytesValue.ToByteArray()),
            AnyValue.ValueOneofCase.ArrayValue => v.ArrayValue.Values.Select(AnyValueToObject).ToList(),
            AnyValue.ValueOneofCase.KvlistValue => v.KvlistValue.Values.ToDictionary(x => x.Key, x => AnyValueToObject(x.Value)),
            AnyValue.ValueOneofCase.None => null,
            _ => null
        };
    }

    public static SpanDto SpanRecordToDto(SpanRecordEntity span)
    {
        var attributes = string.IsNullOrEmpty(span.AttributesJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(span.AttributesJson, Json) ?? new();

        // Enrichissement GenAI : calculer gen_ai.usage.cost si absent mais model+tokens présents
        EnrichGenAiCost(attributes);

        var events = string.IsNullOrEmpty(span.EventsJson)
            ? new List<SpanEventDto>()
            : JsonSerializer.Deserialize<List<SpanEventDto>>(span.EventsJson, JsonWeb) ?? new();

        var resource = string.IsNullOrEmpty(span.ResourceJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(span.ResourceJson, Json) ?? new();

        var scope = string.IsNullOrEmpty(span.ScopeJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(span.ScopeJson, Json) ?? new();

        // EndUnixNs a déjà été corrigé à l'ingestion si invalide
        var endUnixNs = span.EndUnixNs;
        if (endUnixNs == 0 || endUnixNs < span.StartUnixNs)
        {
            endUnixNs = span.StartUnixNs;
        }

        var durationNs = endUnixNs - span.StartUnixNs;
        var durationMs = durationNs / 1_000_000.0;

        return new SpanDto(
            SpanId: Convert.ToHexString(span.SpanId).ToLowerInvariant(),
            ParentSpanId: span.ParentSpanId != null ? Convert.ToHexString(span.ParentSpanId).ToLowerInvariant() : null,
            Name: span.Name,
            Kind: span.Kind,
            StartUtc: DateTimeOffset.FromUnixTimeMilliseconds(span.StartUnixNs / 1_000_000),
            EndUtc: DateTimeOffset.FromUnixTimeMilliseconds(endUnixNs / 1_000_000),
            DurationMs: durationMs,
            StatusCode: span.StatusCode,
            StatusMessage: span.StatusMessage,
            Attributes: attributes,
            Events: events,
            Resource: resource,
            Scope: scope
        );
    }

    /// <summary>
    /// Enrichit les attributs d'un span GenAI avec le coût estimé (gen_ai.usage.cost)
    /// si celui-ci est absent mais que le modèle et les tokens sont présents.
    /// Utilise ModelPricingCatalog pour le calcul (même catalogue que Flow.Server / DocIngestor).
    /// </summary>
    private static void EnrichGenAiCost(Dictionary<string, object?> attributes)
    {
        // Ne pas écraser un coût déjà présent et > 0
        if (attributes.TryGetValue("gen_ai.usage.cost", out var existing) && existing != null)
        {
            try { if (Convert.ToDouble(existing) > 0) return; } catch { /* parse error → recompute */ }
        }

        // Résoudre le nom du modèle
        var model = GetStringAttr(attributes, "gen_ai.request.model")
                 ?? GetStringAttr(attributes, "gen_ai.response.model");
        if (model == null) return;

        // Résoudre les tokens (supporter les deux conventions OTel GenAI)
        var inputTokens = GetLongAttr(attributes, "gen_ai.usage.input_tokens")
                       ?? GetLongAttr(attributes, "gen_ai.usage.prompt_tokens");
        var outputTokens = GetLongAttr(attributes, "gen_ai.usage.output_tokens")
                        ?? GetLongAttr(attributes, "gen_ai.usage.completion_tokens");

        if (!inputTokens.HasValue && !outputTokens.HasValue) return;

        var cost = ModelPricingCatalog.EstimateCost(model, inputTokens ?? 0, outputTokens ?? 0);
        if (cost.HasValue && cost.Value > 0)
            attributes["gen_ai.usage.cost"] = (double)cost.Value;
    }

    private static string? GetStringAttr(Dictionary<string, object?> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var val) || val == null) return null;
        return val switch
        {
            string s => s,
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString(),
            _ => val.ToString()
        };
    }

    private static long? GetLongAttr(Dictionary<string, object?> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var val) || val == null) return null;
        return val switch
        {
            int i => i,
            long l => l,
            double d => (long)d,
            float f => (long)f,
            JsonElement e when e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var l) => l,
            string s when long.TryParse(s, out var l) => l,
            _ => null
        };
    }
}
