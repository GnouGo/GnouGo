using System.Text.Json;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Services.Routing;

public sealed class TelemetryRouteClassifier
{
    public string? SelectCollector(
        IReadOnlyList<SpanRow> spans,
        IReadOnlyList<LogRow> logs,
        TelemetryRoutingOptions options,
        ILogger logger)
    {
        foreach (var rule in options.Rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Collector))
                continue;

            if (!MatchesSignal(rule, spans.Count > 0, logs.Count > 0))
                continue;

            if (MatchesRule(rule, spans, logs))
            {
                logger.LogDebug(
                    "Telemetry route rule {RuleName} selected collector {Collector}",
                    string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name,
                    rule.Collector);
                return rule.Collector;
            }
        }

        return string.IsNullOrWhiteSpace(options.DefaultCollector) ? null : options.DefaultCollector;
    }

    private static bool MatchesSignal(TelemetryRouteRuleOptions rule, bool hasSpans, bool hasLogs)
    {
        if (rule.Signals.Count == 0)
            return true;

        foreach (var signal in rule.Signals)
        {
            if (hasSpans && string.Equals(signal, "traces", StringComparison.OrdinalIgnoreCase))
                return true;
            if (hasLogs && string.Equals(signal, "logs", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool MatchesRule(TelemetryRouteRuleOptions rule, IReadOnlyList<SpanRow> spans, IReadOnlyList<LogRow> logs)
    {
        if (rule.MatchAny.Count > 0)
            return rule.MatchAny.Any(match => MatchesAnyRow(match, spans, logs));

        return MatchesAnyRow(rule.Match, spans, logs);
    }

    private static bool MatchesAnyRow(TelemetryMatchOptions match, IReadOnlyList<SpanRow> spans, IReadOnlyList<LogRow> logs)
    {
        if (IsEmpty(match))
            return true;

        foreach (var span in spans)
        {
            if (MatchesSpan(match, span))
                return true;
        }

        foreach (var log in logs)
        {
            if (MatchesLog(match, log))
                return true;
        }

        return false;
    }

    private static bool MatchesSpan(TelemetryMatchOptions match, SpanRow span)
    {
        if (!MatchesTextSet(match.ServiceNames, span.ServiceName, exact: true))
            return false;
        if (!MatchesTextSet(match.ServiceNameContains, span.ServiceName, exact: false))
            return false;
        if (!MatchesTextSet(match.SpanNames, span.Name, exact: true))
            return false;
        if (!MatchesTextSet(match.SpanNameContains, span.Name, exact: false))
            return false;
        if (match.LogBodyContains.Count > 0)
            return false;

        return MatchesAttributes(match.Attributes, span.AttributesJson)
            && MatchesAttributes(match.ResourceAttributes, span.ResourceJson)
            && MatchesAttributes(match.ScopeAttributes, span.ScopeJson)
            && MatchesAnyAttributeSource(match.AnyAttributes, span.AttributesJson, span.ResourceJson, span.ScopeJson);
    }

    private static bool MatchesLog(TelemetryMatchOptions match, LogRow log)
    {
        if (!MatchesTextSet(match.ServiceNames, log.ServiceName, exact: true))
            return false;
        if (!MatchesTextSet(match.ServiceNameContains, log.ServiceName, exact: false))
            return false;
        if (match.SpanNames.Count > 0 || match.SpanNameContains.Count > 0)
            return false;
        if (!MatchesTextSet(match.LogBodyContains, log.Body, exact: false))
            return false;

        return MatchesAttributes(match.Attributes, log.AttributesJson)
            && MatchesAttributes(match.ResourceAttributes, log.ResourceJson)
            && MatchesAttributes(match.ScopeAttributes, log.ScopeJson)
            && MatchesAnyAttributeSource(match.AnyAttributes, log.AttributesJson, log.ResourceJson, log.ScopeJson);
    }

    private static bool MatchesTextSet(List<string> expected, string? actual, bool exact)
    {
        if (expected.Count == 0)
            return true;
        if (string.IsNullOrEmpty(actual))
            return false;

        return expected.Any(candidate => exact
            ? string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase)
            : actual.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesAttributes(List<TelemetryAttributeMatchOptions> expected, string? json)
    {
        if (expected.Count == 0)
            return true;

        var attributes = ReadJsonObject(json);
        foreach (var match in expected)
        {
            if (!MatchesAttribute(attributes, match))
                return false;
        }

        return true;
    }

    private static bool MatchesAnyAttributeSource(List<TelemetryAttributeMatchOptions> expected, params string?[] jsonSources)
    {
        if (expected.Count == 0)
            return true;

        var sources = jsonSources.Select(ReadJsonObject).ToArray();
        foreach (var match in expected)
        {
            if (!sources.Any(source => MatchesAttribute(source, match)))
                return false;
        }

        return true;
    }

    private static bool MatchesAttribute(Dictionary<string, object?> attributes, TelemetryAttributeMatchOptions match)
    {
        if (string.IsNullOrWhiteSpace(match.Key))
            return false;

        if (!attributes.TryGetValue(match.Key, out var value) || value is null)
            return !match.Exists;

        if (!match.Exists)
            return false;

        var actual = AttributeValueToString(value);
        var comparison = match.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (match.Value is not null && !string.Equals(actual, match.Value, comparison))
            return false;

        if (match.Values.Count > 0 && !match.Values.Any(candidate => string.Equals(actual, candidate, comparison)))
            return false;

        if (match.Contains is not null && !actual.Contains(match.Contains, comparison))
            return false;

        return true;
    }

    private static Dictionary<string, object?> ReadJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        try
        {
            return TelemetryJsonCodec.DeserializeObject(json);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static string AttributeValueToString(object value)
        => value switch
        {
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString(),
            IEnumerable<object?> sequence => string.Join(",", sequence.Select(item => item?.ToString() ?? string.Empty)),
            _ => value.ToString() ?? string.Empty
        };

    private static bool IsEmpty(TelemetryMatchOptions match)
        => match.ServiceNames.Count == 0
           && match.ServiceNameContains.Count == 0
           && match.SpanNames.Count == 0
           && match.SpanNameContains.Count == 0
           && match.LogBodyContains.Count == 0
           && match.Attributes.Count == 0
           && match.ResourceAttributes.Count == 0
           && match.ScopeAttributes.Count == 0
           && match.AnyAttributes.Count == 0;
}

