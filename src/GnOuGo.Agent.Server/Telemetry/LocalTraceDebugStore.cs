using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.SmartFlow;

namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>
/// Keeps a small in-memory buffer of recent chat traces so the debug sidebar
/// remains usable even when OpenTelemetry export is disabled.
/// </summary>
public sealed class LocalTraceDebugStore
{
    private const int MaxTraceCount = 128;
    private static readonly TimeSpan MaxTraceAge = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, TraceState> _traces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _correlationIndex = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<OpenTelemetrySettings> _openTelemetrySettings;

    public LocalTraceDebugStore(IOptionsMonitor<OpenTelemetrySettings> openTelemetrySettings)
    {
        _openTelemetrySettings = openTelemetrySettings;
    }

    public void Track(Activity activity)
    {
        var traceId = activity.TraceId.ToHexString();
        if (string.IsNullOrWhiteSpace(traceId))
            return;

        var trace = _traces.GetOrAdd(traceId, _ => new TraceState(traceId));
        lock (trace.SyncRoot)
        {
            var correlationId = ResolveCorrelationId(activity);
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                trace.CorrelationId = correlationId;
                _correlationIndex[correlationId] = traceId;
            }

            var spanId = activity.SpanId.ToHexString();
            if (string.IsNullOrWhiteSpace(spanId))
                return;

            if (!trace.Spans.TryGetValue(spanId, out var span))
            {
                span = new SpanState(spanId);
                trace.Spans[spanId] = span;
            }

            UpdateSpan(trace, span, activity);
        }

        Trim();
    }

    public void Complete(Activity activity)
        => Track(activity);

    public string? ResolveTraceId(string correlationId)
        => _correlationIndex.TryGetValue(correlationId, out var traceId) ? traceId : null;

    public IReadOnlyList<string> ResolveTraceIds(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return [];

        return _traces
            .Values
            .Select(trace =>
            {
                lock (trace.SyncRoot)
                {
                    return new
                    {
                        trace.TraceId,
                        trace.CorrelationId,
                        trace.LastUpdatedUtc
                    };
                }
            })
            .Where(trace => string.Equals(trace.CorrelationId, correlationId, StringComparison.Ordinal))
            .OrderByDescending(trace => trace.LastUpdatedUtc)
            .Select(trace => trace.TraceId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public TraceGroupDto? GetTrace(string traceId)
    {
        if (!_traces.TryGetValue(traceId, out var trace))
            return null;

        lock (trace.SyncRoot)
        {
            if (trace.Spans.Count == 0)
                return null;

            var spans = trace.Spans.Values
                .Select(span => new TraceSpanDto(
                    SpanId: span.SpanId,
                    ParentSpanId: span.ParentSpanId,
                    Name: span.Name,
                    Kind: span.Kind,
                    StartUtc: span.StartUtc,
                    EndUtc: span.EndUtc,
                    DurationMs: span.DurationMs,
                    StatusCode: span.StatusCode,
                    StatusMessage: span.StatusMessage,
                    Attributes: new Dictionary<string, object?>(span.Attributes, StringComparer.Ordinal),
                    Events: span.Events
                        .Select(evt => new TraceEventDto(evt.Name, evt.TimeUtc, new Dictionary<string, object?>(evt.Attributes, StringComparer.Ordinal)))
                        .ToList(),
                    Resource: new Dictionary<string, object?>(span.Resource, StringComparer.Ordinal),
                    Scope: new Dictionary<string, object?>(span.Scope, StringComparer.Ordinal)))
                .OrderBy(span => span.StartUtc)
                .ToList();

            return new TraceGroupDto(
                TraceId: traceId,
                StartUtc: spans.Min(span => span.StartUtc),
                EndUtc: spans.Max(span => span.EndUtc),
                Spans: spans);
        }
    }

    private void UpdateSpan(TraceState trace, SpanState span, Activity activity)
    {
        span.ParentSpanId = activity.ParentSpanId != default ? activity.ParentSpanId.ToHexString() : null;
        span.Name = activity.DisplayName;
        span.Kind = MapKind(activity.Kind);
        span.StartUtc = activity.StartTimeUtc;

        var endUtc = activity.Duration > TimeSpan.Zero
            ? activity.StartTimeUtc + activity.Duration
            : DateTime.UtcNow;

        span.EndUtc = endUtc;
        span.DurationMs = Math.Max(0d, (span.EndUtc - span.StartUtc).TotalMilliseconds);
        span.StatusCode = MapStatus(activity.Status);
        span.StatusMessage = activity.StatusDescription;
        span.Attributes = ExtractAttributes(activity);
        span.Events = ExtractEvents(activity);
        span.Resource = BuildResource();
        span.Scope = BuildScope();

        trace.LastUpdatedUtc = DateTimeOffset.UtcNow;
    }

    private static string? ResolveCorrelationId(Activity activity)
    {
        return activity.GetTagItem(AgentOTelTelemetry.CorrelationIdTagName) as string
            ?? activity.GetBaggageItem(AgentOTelTelemetry.CorrelationIdTagName)
            ?? activity.GetTagItem(AgentOTelTelemetry.ConversationIdTagName) as string
            ?? activity.GetBaggageItem(AgentOTelTelemetry.ConversationIdTagName);
    }

    private Dictionary<string, object?> ExtractAttributes(Activity activity)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in activity.TagObjects)
        {
            if (!string.IsNullOrWhiteSpace(tag.Key))
                attributes[tag.Key] = NormalizeValue(tag.Value);
        }

        var correlationId = ResolveCorrelationId(activity);
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            attributes[AgentOTelTelemetry.CorrelationIdTagName] = correlationId;
            attributes[AgentOTelTelemetry.ConversationIdTagName] = correlationId;
        }

        return attributes;
    }

    private static List<TraceEventDto> ExtractEvents(Activity activity)
    {
        var result = new List<TraceEventDto>(); 
        foreach (var evt in activity.Events)
        {
            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in evt.Tags)
            {
                if (!string.IsNullOrWhiteSpace(tag.Key))
                    attributes[tag.Key] = NormalizeValue(tag.Value);
            }

            result.Add(new TraceEventDto(evt.Name, evt.Timestamp.UtcDateTime, attributes));
        }

        return result;
    }

    private Dictionary<string, object?> BuildResource()
        => new(StringComparer.Ordinal)
        {
            ["service.name"] = _openTelemetrySettings.CurrentValue.ServiceName,
            ["telemetry.sdk.language"] = "dotnet"
        };

    private static Dictionary<string, object?> BuildScope()
        => new(StringComparer.Ordinal)
        {
            ["name"] = AgentOTelTelemetry.ActivitySourceName,
            ["version"] = "1.0.0-local"
        };

    private void Trim()
    {
        if (_traces.Count <= MaxTraceCount)
            return;

        var cutoff = DateTimeOffset.UtcNow - MaxTraceAge;
        var stale = _traces
            .Where(pair => pair.Value.LastUpdatedUtc < cutoff)
            .OrderBy(pair => pair.Value.LastUpdatedUtc)
            .ToList();

        foreach (var pair in stale)
        {
            if (_traces.TryRemove(pair.Key, out var removed)
                && !string.IsNullOrWhiteSpace(removed.CorrelationId))
            {
                _correlationIndex.TryRemove(removed.CorrelationId, out _);
            }
        }

        if (_traces.Count <= MaxTraceCount)
            return;

        foreach (var pair in _traces.OrderBy(pair => pair.Value.LastUpdatedUtc).Take(_traces.Count - MaxTraceCount).ToList())
        {
            if (_traces.TryRemove(pair.Key, out var removed)
                && !string.IsNullOrWhiteSpace(removed.CorrelationId))
            {
                _correlationIndex.TryRemove(removed.CorrelationId, out _);
            }
        }
    }

    private static object? NormalizeValue(object? value)
        => value switch
        {
            null => null,
            DateTimeOffset dto => dto,
            DateTime dt => dt,
            TimeSpan ts => ts.ToString(),
            ActivityTraceId traceId => traceId.ToHexString(),
            ActivitySpanId spanId => spanId.ToHexString(),
            _ => value
        };

    private static int MapStatus(ActivityStatusCode status)
        => status switch
        {
            ActivityStatusCode.Ok => 1,
            ActivityStatusCode.Error => 2,
            _ => 0
        };

    private static int MapKind(ActivityKind kind)
        => kind switch
        {
            ActivityKind.Internal => 1,
            ActivityKind.Server => 2,
            ActivityKind.Client => 3,
            ActivityKind.Producer => 4,
            ActivityKind.Consumer => 5,
            _ => 0
        };

    private sealed class TraceState
    {
        public TraceState(string traceId)
        {
            TraceId = traceId;
        }

        public string TraceId { get; }
        public string? CorrelationId { get; set; }
        public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public object SyncRoot { get; } = new();
        public Dictionary<string, SpanState> Spans { get; } = new(StringComparer.Ordinal);
    }

    private sealed class SpanState
    {
        public SpanState(string spanId)
        {
            SpanId = spanId;
        }

        public string SpanId { get; }
        public string? ParentSpanId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Kind { get; set; }
        public DateTimeOffset StartUtc { get; set; }
        public DateTimeOffset EndUtc { get; set; }
        public double DurationMs { get; set; }
        public int StatusCode { get; set; }
        public string? StatusMessage { get; set; }
        public Dictionary<string, object?> Attributes { get; set; } = new(StringComparer.Ordinal);
        public List<TraceEventDto> Events { get; set; } = [];
        public Dictionary<string, object?> Resource { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, object?> Scope { get; set; } = new(StringComparer.Ordinal);
    }
}

