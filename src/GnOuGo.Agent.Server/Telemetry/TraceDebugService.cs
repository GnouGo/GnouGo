using Microsoft.Extensions.Options;
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
    private readonly IOptionsMonitor<OpenTelemetrySettings> _openTelemetrySettings;
    private readonly ILogger<TraceDebugService> _logger;

    public TraceDebugService(
        IServiceScopeFactory scopeFactory,
        LocalTraceDebugStore localTraceStore,
        IOptionsMonitor<OpenTelemetrySettings> openTelemetrySettings,
        ILogger<TraceDebugService> logger)
    {
        _scopeFactory = scopeFactory;
        _localTraceStore = localTraceStore;
        _openTelemetrySettings = openTelemetrySettings;
        _logger = logger;
    }

    public TimeSpan RefreshInterval => DefaultRefreshInterval;

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
                Pending: false,
                Message: "This message does not carry telemetry metadata yet. Only newly generated GnOuGo messages can be traced.");
        }

        var resolvedTraceId = string.IsNullOrWhiteSpace(traceId) ? null : traceId.Trim();
        var traceIds = await ResolveCandidateTraceIdsAsync(correlationId, resolvedTraceId).ConfigureAwait(false);
        var trace = await TryGetMergedTraceAsync(traceIds).ConfigureAwait(false);
        if (trace is not null)
        {
            return new TraceDebugSnapshot(
                availability,
                correlationId,
                trace.TraceId,
                Trace: trace,
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
            Pending: true,
            Message: BuildPendingMessage());
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
            Attributes: new Dictionary<string, object?>(span.Attributes, StringComparer.Ordinal),
            Events: span.Events
                .Select(evt => new TraceEventDto(evt.Name, evt.TimeUtc, new Dictionary<string, object?>(evt.Attributes, StringComparer.Ordinal)))
                .ToList(),
            Resource: new Dictionary<string, object?>(span.Resource, StringComparer.Ordinal),
            Scope: new Dictionary<string, object?>(span.Scope, StringComparer.Ordinal));
}
