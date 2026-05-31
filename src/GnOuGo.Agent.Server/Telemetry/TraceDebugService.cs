using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text.Json;
using GnOuGo.Agent.Server.Configuration;
using OtlpTenantCollector.Services;

namespace GnOuGo.Agent.Server.Telemetry;

/// <summary>
/// Reads GenAI/OpenTelemetry traces for the chat debug sidebar from the local
/// in-memory capture first, then from the embedded OTLP collector storage.
/// </summary>
public sealed class TraceDebugService
{
    public const string HttpClientName = "TraceDebug";

    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LocalTraceDebugStore _localTraceStore;
    private readonly TelemetryEventBus _telemetryEventBus;
    private readonly IOptionsMonitor<OpenTelemetrySettings> _openTelemetrySettings;
    private readonly ILogger<TraceDebugService> _logger;

    public TraceDebugService(
        IServiceScopeFactory scopeFactory,
        LocalTraceDebugStore localTraceStore,
        TelemetryEventBus telemetryEventBus,
        IOptionsMonitor<OpenTelemetrySettings> openTelemetrySettings,
        ILogger<TraceDebugService> logger)
    {
        _scopeFactory = scopeFactory;
        _localTraceStore = localTraceStore;
        _telemetryEventBus = telemetryEventBus;
        _openTelemetrySettings = openTelemetrySettings;
        _logger = logger;
    }

    public TimeSpan RefreshInterval => DefaultRefreshInterval;

    /// <summary>
    /// Streams trace debug snapshots. The first snapshot is returned immediately,
    /// then subsequent snapshots are pushed when the embedded OTLP collector flushes
    /// new spans/logs. A lightweight heartbeat keeps local in-memory traces fresh
    /// even before a collector flush occurs.
    /// </summary>
    public async IAsyncEnumerable<TraceDebugSnapshot> StreamSnapshotsAsync(
        string? correlationId,
        string? traceId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return await GetSnapshotAsync(correlationId, traceId, ct).ConfigureAwait(false);

        var subscription = _telemetryEventBus.Subscribe();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var waitForFlushTask = subscription.Reader.WaitToReadAsync(ct).AsTask();
                var heartbeatTask = Task.Delay(DefaultRefreshInterval, ct);
                var completedTask = await Task.WhenAny(waitForFlushTask, heartbeatTask).ConfigureAwait(false);

                if (completedTask == waitForFlushTask)
                {
                    if (!await waitForFlushTask.ConfigureAwait(false))
                        yield break;

                    while (subscription.Reader.TryRead(out _))
                    {
                        // Drain coalesced flush events; one fresh snapshot is enough.
                    }
                }

                yield return await GetSnapshotAsync(correlationId, traceId, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _telemetryEventBus.Unsubscribe(subscription);
        }
    }

    public TraceDebugAvailability GetAvailability()
    {
        var otelSettings = _openTelemetrySettings.CurrentValue;
        return otelSettings.Enabled
            ? new TraceDebugAvailability(true, "Live trace debugging is ready (local capture + embedded collector storage).")
            : new TraceDebugAvailability(true, "OpenTelemetry export is disabled, but local trace debugging remains available.");
    }

    public async Task<TraceDebugSnapshot> GetSnapshotAsync(
        string? correlationId,
        string? traceId,
        CancellationToken ct)
    {
        var availability = GetAvailability();

        if (string.IsNullOrWhiteSpace(correlationId) && string.IsNullOrWhiteSpace(traceId))
        {
            return new TraceDebugSnapshot(
                availability,
                correlationId,
                traceId,
                Trace: null,
                Logs: [],
                Pending: false,
                Message: "This message does not carry telemetry metadata yet. Only newly generated GnOuGo messages can be traced.");
        }

        var resolvedTraceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId.Trim();
        var traceIds = await ResolveCandidateTraceIdsAsync(correlationId, resolvedTraceId).ConfigureAwait(false);
        var logs = await TryGetLogsAsync(traceIds, correlationId).ConfigureAwait(false);
        var trace = await TryGetMergedTraceAsync(traceIds).ConfigureAwait(false);
        if (trace is not null)
        {
            return new TraceDebugSnapshot(
                availability,
                correlationId,
                trace.TraceId,
                Trace: trace,
                Logs: logs,
                Pending: false,
                Message: traceIds.Count > 1
                    ? $"Trace loaded from {traceIds.Count} related traces."
                    : "Trace loaded.");
        }

        return new TraceDebugSnapshot(
            availability,
            correlationId,
            resolvedTraceId,
            Trace: null,
            Logs: logs,
            Pending: true,
            Message: BuildPendingMessage());
    }

    private async Task<List<TraceLogDto>> TryGetLogsAsync(IReadOnlyList<string> traceIds, string? correlationId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
            var tenantId = ResolveTenantId();
            var logs = new List<OtlpTenantCollector.Models.LogRecordEntity>();

            foreach (var traceId in traceIds)
            {
                if (string.IsNullOrWhiteSpace(traceId))
                    continue;

                try
                {
                    var traceIdBytes = Convert.FromHexString(traceId);
                    logs.AddRange(await store.GetLogsForTraceAsync(tenantId, traceIdBytes).ConfigureAwait(false));
                }
                catch (FormatException ex)
                {
                    _logger.LogDebug(ex, "Invalid trace id format '{TraceId}' while loading trace logs.", traceId);
                }
            }

            if (logs.Count == 0 && !string.IsNullOrWhiteSpace(correlationId))
            {
                logs.AddRange(await store.GetRecentLogsAsync(
                    tenantId: tenantId,
                    limit: 200,
                    serviceName: _openTelemetrySettings.CurrentValue.ServiceName,
                    startUtc: null,
                    endUtc: null,
                    severityLevels: null,
                    traceIdFilter: null,
                    attributeContains: correlationId).ConfigureAwait(false));
            }

            return logs
                .GroupBy(log => log.Id)
                .Select(group => group.First())
                .OrderBy(log => log.ReceivedUtc)
                .Select(MapLog)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load trace logs for correlation '{CorrelationId}'.", correlationId);
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> ResolveCandidateTraceIdsAsync(string? correlationId, string? traceId)
    {
        var candidates = new List<string>();
        AddCandidate(traceId);

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            foreach (var localTraceId in _localTraceStore.ResolveTraceIds(correlationId))
                AddCandidate(localTraceId);

            foreach (var persistedTraceId in await TryResolveTraceIdsByCorrelationIdAsync(correlationId).ConfigureAwait(false))
                AddCandidate(persistedTraceId);
        }

        return candidates;

        void AddCandidate(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                candidates.Add(candidate);
        }
    }

    private async Task<IReadOnlyList<string>> TryResolveTraceIdsByCorrelationIdAsync(string correlationId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
            var serviceName = _openTelemetrySettings.CurrentValue.ServiceName;
            var traces = await store.GetRecentTracesAsync(
                tenantId: ResolveTenantId(),
                limit: 200,
                serviceName: serviceName,
                startUtc: null,
                endUtc: null,
                traceIdFilter: null,
                attributeContains: correlationId).ConfigureAwait(false);

            return traces
                .OrderByDescending(t => t.EndUtc)
                .Select(t => t.TraceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve trace ids for correlation '{CorrelationId}'.", correlationId);
            return [];
        }
    }

    private async Task<TraceGroupDto?> TryGetMergedTraceAsync(IReadOnlyList<string> traceIds)
    {
        if (traceIds.Count == 0)
            return null;

        var groups = new List<TraceGroupDto>(traceIds.Count);
        foreach (var traceId in traceIds)
        {
            var trace = await TryGetTraceAsync(traceId).ConfigureAwait(false);
            if (trace is not null)
                groups.Add(trace);
        }

        if (groups.Count == 0)
            return null;

        if (groups.Count == 1)
            return groups[0];

        var spans = groups
            .SelectMany(group => group.Spans)
            .GroupBy(span => span.SpanId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(span => span.EndUtc)
                .ThenByDescending(span => span.Attributes.Count)
                .ThenByDescending(span => span.Events.Count)
                .First())
            .OrderBy(span => span.StartUtc)
            .ToList();

        return new TraceGroupDto(
            TraceId: traceIds[0],
            StartUtc: spans.Min(span => span.StartUtc),
            EndUtc: spans.Max(span => span.EndUtc),
            Spans: spans);
    }

    private async Task<TraceGroupDto?> TryGetTraceAsync(string traceId)
    {
        var localTrace = _localTraceStore.GetTrace(traceId);
        if (localTrace is not null)
            return localTrace;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
            var traceIdBytes = Convert.FromHexString(traceId);
            var spans = await store.GetTraceSpansAsync(ResolveTenantId(), traceIdBytes).ConfigureAwait(false);
            if (spans.Count == 0)
                return null;

            var spanDtos = spans
                .Select(OtlpJson.SpanRecordToDto)
                .Select(MapSpan)
                .GroupBy(s => s.SpanId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            return new TraceGroupDto(
                TraceId: traceId.ToLowerInvariant(),
                StartUtc: spanDtos.Min(s => s.StartUtc),
                EndUtc: spanDtos.Max(s => s.EndUtc),
                Spans: spanDtos);
        }
        catch (FormatException ex)
        {
            _logger.LogDebug(ex, "Invalid trace id format '{TraceId}'.", traceId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load trace '{TraceId}' from embedded OTLP storage.", traceId);
            return null;
        }
    }

    private Guid? ResolveTenantId()
    {
        var tenantId = _openTelemetrySettings.CurrentValue.TenantId;
        return Guid.TryParse(tenantId, out var parsedTenantId) ? parsedTenantId : null;
    }

    private static string BuildPendingMessage()
        => "Waiting for telemetry spans to arrive for this message.";

    private static TraceSpanDto MapSpan(OtlpTenantCollector.Models.SpanDto span)
        => new(
            SpanId: span.SpanId,
            ParentSpanId: span.ParentSpanId,
            Name: span.Name,
            Kind: span.Kind,
            StartUtc: span.StartUtc,
            EndUtc: span.EndUtc,
            DurationMs: span.DurationMs,
            StatusCode: span.StatusCode,
            StatusMessage: span.StatusMessage,
            Attributes: ToObjectDictionary(span.Attributes),
            Events: ToTraceEvents(span.Events),
            Resource: ToObjectDictionary(span.Resource),
            Scope: ToObjectDictionary(span.Scope));

    private static TraceLogDto MapLog(OtlpTenantCollector.Models.LogRecordEntity log)
        => new(
            ReceivedUtc: log.ReceivedUtc,
            TraceId: log.TraceId is null ? null : Convert.ToHexString(log.TraceId).ToLowerInvariant(),
            SpanId: log.SpanId is null ? null : Convert.ToHexString(log.SpanId).ToLowerInvariant(),
            SeverityNumber: log.SeverityNumber,
            SeverityText: log.SeverityText,
            Body: log.Body,
            ServiceName: log.ServiceName,
            Attributes: ToObjectDictionary(log.AttributesJson),
            Resource: ToObjectDictionary(log.ResourceJson),
            Scope: ToObjectDictionary(log.ScopeJson));

    private static Dictionary<string, object?> ToObjectDictionary(object? value)
    {
        if (value is null)
            return new Dictionary<string, object?>(StringComparer.Ordinal);

        if (value is string jsonText)
        {
            if (string.IsNullOrWhiteSpace(jsonText))
                return new Dictionary<string, object?>(StringComparer.Ordinal);

            try
            {
                using var document = JsonDocument.Parse(jsonText);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? JsonObjectToDictionary(document.RootElement)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }
        }

        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Object
                ? JsonObjectToDictionary(element)
                : new Dictionary<string, object?>(StringComparer.Ordinal);

        if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var pair in pairs)
                values[pair.Key] = ObjectValueToObject(pair.Value);

            return values;
        }

        if (value is System.Collections.IDictionary dictionary)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                var key = entry.Key.ToString();
                if (!string.IsNullOrWhiteSpace(key))
                    values[key] = ObjectValueToObject(entry.Value);
            }

            return values;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static List<TraceEventDto> ToTraceEvents(object? value)
    {
        if (value is null)
            return [];

        if (value is JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return [];

            var events = new List<TraceEventDto>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var name = TryGetProperty(item, "name", "Name", out var nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;
                var timeUtc = TryGetProperty(item, "timeUtc", "TimeUtc", out var timeElement) &&
                    TryReadDateTimeOffset(timeElement, out var parsedTimeUtc)
                    ? parsedTimeUtc
                    : DateTimeOffset.MinValue;
                var attributes = TryGetProperty(item, "attributes", "Attributes", out var attributesElement)
                    ? ToObjectDictionary(attributesElement)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);

                events.Add(new TraceEventDto(name, timeUtc, attributes));
            }

            return events;
        }

        if (value is IEnumerable<OtlpTenantCollector.Models.SpanEventDto> spanEvents)
            return spanEvents
                .Select(evt => new TraceEventDto(evt.Name, evt.TimeUtc, ToObjectDictionary(evt.Attributes)))
                .ToList();

        return [];
    }

    private static Dictionary<string, object?> JsonObjectToDictionary(JsonElement element)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            values[property.Name] = JsonValueToObject(property.Value);

        return values;
    }

    private static object? JsonValueToObject(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => JsonObjectToDictionary(element),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonValueToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.ToString()
        };

    private static object? ObjectValueToObject(object? value)
        => value is JsonElement element ? JsonValueToObject(element) : value;

    private static bool TryGetProperty(JsonElement element, string camelCaseName, string pascalCaseName, out JsonElement property)
        => element.TryGetProperty(camelCaseName, out property) || element.TryGetProperty(pascalCaseName, out property);

    private static bool TryReadDateTimeOffset(JsonElement element, out DateTimeOffset value)
    {
        if (element.ValueKind == JsonValueKind.String && element.TryGetDateTimeOffset(out value))
            return true;

        value = default;
        return false;
    }
}
