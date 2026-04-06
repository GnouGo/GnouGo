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
        if (!string.IsNullOrWhiteSpace(resolvedTraceId))
        {
            var trace = await TryGetTraceAsync(resolvedTraceId).ConfigureAwait(false);
            if (trace is not null)
            {
                return new TraceDebugSnapshot(
                    availability,
                    correlationId,
                    resolvedTraceId,
                    Trace: trace,
                    Pending: false,
                    Message: "Trace loaded.");
            }
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            resolvedTraceId = _localTraceStore.ResolveTraceId(correlationId)
                ?? await TryResolveTraceIdByCorrelationIdAsync(correlationId).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(resolvedTraceId))
            {
                var trace = await TryGetTraceAsync(resolvedTraceId).ConfigureAwait(false);
                if (trace is not null)
                {
                    return new TraceDebugSnapshot(
                        availability,
                        correlationId,
                        resolvedTraceId,
                        Trace: trace,
                        Pending: false,
                        Message: "Trace loaded.");
                }
            }
        }

        return new TraceDebugSnapshot(
            availability,
            correlationId,
            resolvedTraceId,
            Trace: null,
            Pending: true,
            Message: BuildPendingMessage());
    }

    private async Task<string?> TryResolveTraceIdByCorrelationIdAsync(string correlationId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
            var serviceName = _openTelemetrySettings.CurrentValue.ServiceName;
            var traces = await store.GetRecentTracesAsync(
                tenantId: ResolveTenantId(),
                limit: 50,
                serviceName: serviceName,
                startUtc: null,
                endUtc: null,
                traceIdFilter: null,
                attributeContains: correlationId).ConfigureAwait(false);

            return traces
                .OrderByDescending(t => t.EndUtc)
                .FirstOrDefault()
                ?.TraceId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve trace id for correlation '{CorrelationId}'.", correlationId);
            return null;
        }
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
