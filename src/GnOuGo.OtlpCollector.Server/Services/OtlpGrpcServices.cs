using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Options;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Services;

public static class OtlpTenantAuth
{
    public const string TenantIdHeader = "x-tenant-id";

    /// <summary>
    /// Extracts tenant ID from gRPC metadata. Returns null if not present.
    /// In non-DevMode, throws RpcException if missing/invalid.
    /// In DevMode, returns null when absent.
    /// </summary>
    public static Guid? ResolveTenantId(ServerCallContext context, bool devMode, ILogger logger)
    {
        string? tenantIdString = null;
        foreach (var header in context.RequestHeaders)
        {
            if (string.Equals(header.Key, TenantIdHeader, StringComparison.OrdinalIgnoreCase))
            {
                tenantIdString = header.Value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(tenantIdString))
        {
            if (devMode)
            {
                logger.LogDebug("[OTLP gRPC] No x-tenant-id header — DevMode: using null tenant.");
                return null;
            }
            throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, "x-tenant-id header is required"));
        }

        if (!Guid.TryParse(tenantIdString, out var tenantId))
        {
            if (devMode)
            {
                logger.LogWarning("[OTLP gRPC] Invalid tenant ID format: {TenantId}. DevMode: using null.", tenantIdString);
                return null;
            }
            throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, $"Invalid tenant ID format: {tenantIdString}"));
        }

        return tenantId;
    }
}

// -------- OTLP/gRPC: traces --------
public sealed class OtlpTraceGrpcService : OpenTelemetry.Proto.Collector.Trace.V1.TraceService.TraceServiceBase
{
    private readonly TelemetryIngestQueue _queue;
    private readonly ILogger<OtlpTraceGrpcService> _logger;
    private readonly bool _devMode;

    public OtlpTraceGrpcService(TelemetryIngestQueue queue, ILogger<OtlpTraceGrpcService> logger, IOptions<DevModeOptions> devModeOptions)
    {
        _queue = queue;
        _logger = logger;
        _devMode = devModeOptions.Value.Enabled;
    }

    public override async Task<OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceResponse> Export(
        OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest request,
        ServerCallContext context)
    {
        var tenantId = OtlpTenantAuth.ResolveTenantId(context, _devMode, _logger);

        var receivedUtc = DateTimeOffset.UtcNow;
        var receivedUnixNs = receivedUtc.ToUnixTimeMilliseconds() * 1_000_000L;

        int totalSpans = 0;
        foreach (var rs in request.ResourceSpans)
        {
            var resourceJson = OtlpJson.ToJsonResource(rs.Resource);

            var serviceName = rs.Resource?.Attributes
                .FirstOrDefault(a => a.Key == "service.name")?.Value?.StringValue ?? "unknown-service";

            _logger.LogDebug("[OTLP gRPC] ResourceSpans tenant={TenantId} service='{ServiceName}' scopeCount={ScopeCount}",
                tenantId, serviceName, rs.ScopeSpans.Count);

            foreach (var ss in rs.ScopeSpans)
            {
                var scopeJson = OtlpJson.ToJsonScope(ss.Scope);
                var scopeName = ss.Scope?.Name ?? "unknown";
                totalSpans += ss.Spans.Count;

                _logger.LogDebug("[OTLP gRPC] Processing {SpansCount} spans — service='{ServiceName}' scope='{ScopeName}'",
                    ss.Spans.Count, serviceName, scopeName);

                foreach (var s in ss.Spans)
                {
                    var attrsJson = OtlpJson.ToJsonAttributes(s.Attributes);

                    // Nom de fallback pour les spans sans nom
                    var spanName = s.Name;
                    if (string.IsNullOrWhiteSpace(spanName))
                    {
                        var httpMethod = s.Attributes.FirstOrDefault(a => a.Key == "http.request.method" || a.Key == "http.method")?.Value?.StringValue;
                        if (!string.IsNullOrEmpty(httpMethod))
                        {
                            var host = s.Attributes.FirstOrDefault(a => a.Key == "http.host" || a.Key == "server.address")?.Value?.StringValue;
                            spanName = host != null ? $"{httpMethod} {host}" : $"HTTP {httpMethod}";
                        }
                        else
                        {
                            spanName = !string.IsNullOrWhiteSpace(scopeName) ? scopeName : $"span-{s.Kind}";
                        }
                        _logger.LogDebug("[OTLP gRPC] Span sans nom → fallback: '{FallbackName}'", spanName);
                    }

                    // Utiliser OtlpJson.ToJsonEvents pour garantir la préservation des clés
                    var eventsJson = OtlpJson.ToJsonEvents(s.Events);

                    var statusCode = s.Status != null ? (int)s.Status.Code : 0;
                    var statusMsg = s.Status?.Message;

                    var startUnixNs = (long)s.StartTimeUnixNano;
                    var endUnixNs   = (long)s.EndTimeUnixNano;

                    if (endUnixNs == 0 && startUnixNs > 0)
                    {
                        // Le client a exporté le span avant qu'il soit fermé (BatchProcessor ou span root non fermé).
                        // On utilise ReceivedUtc comme approximation du EndTime réel.
                        _logger.LogWarning(
                            "[OTLP gRPC] Span '{SpanName}' [service={ServiceName}] EndTimeUnixNano=0 — span exporté avant fermeture. Correction: end=ReceivedUtc ({ReceivedUtc})",
                            spanName, serviceName, receivedUtc);
                        endUnixNs = receivedUnixNs;
                    }
                    else if (endUnixNs < startUnixNs)
                    {
                        _logger.LogWarning(
                            "[OTLP gRPC] Span '{SpanName}' [service={ServiceName}] End < Start (end={End}, start={Start}). Correction: end=start.",
                            spanName, serviceName, endUnixNs, startUnixNs);
                        endUnixNs = startUnixNs;
                    }

                    var durationMs = (endUnixNs - startUnixNs) / 1_000_000.0;

                    // Logger tous les attributs importants
                    var genAiSystem   = s.Attributes.FirstOrDefault(a => a.Key == "gen_ai.system")?.Value?.StringValue;
                    var genAiOp       = s.Attributes.FirstOrDefault(a => a.Key == "gen_ai.operation.name")?.Value?.StringValue;
                    var genAiModel    = s.Attributes.FirstOrDefault(a => a.Key == "gen_ai.request.model")?.Value?.StringValue;
                    var genAiInTok    = s.Attributes.FirstOrDefault(a => a.Key == "gen_ai.usage.input_tokens")?.Value;
                    var genAiOutTok   = s.Attributes.FirstOrDefault(a => a.Key == "gen_ai.usage.output_tokens")?.Value;
                    var dbSystem      = s.Attributes.FirstOrDefault(a => a.Key == "db.system")?.Value?.StringValue;
                    var dbOp          = s.Attributes.FirstOrDefault(a => a.Key == "db.operation")?.Value?.StringValue;

                    _logger.LogDebug(
                        "[OTLP gRPC] Span '{SpanName}' service='{ServiceName}' scope='{ScopeName}' kind={Kind} duration={Duration}ms status={StatusCode} attrs={AttrCount} events={EventCount}",
                        spanName, serviceName, scopeName, (int)s.Kind, durationMs, statusCode, s.Attributes.Count, s.Events.Count);

                    if (genAiSystem != null)
                        _logger.LogDebug(
                            "[OTLP gRPC]   gen_ai: system={System} op={Op} model={Model} input_tokens={InTok} output_tokens={OutTok}",
                            genAiSystem, genAiOp, genAiModel,
                            genAiInTok != null ? OtlpJson.AnyValueToObject(genAiInTok) : null,
                            genAiOutTok != null ? OtlpJson.AnyValueToObject(genAiOutTok) : null);

                    if (dbSystem != null)
                        _logger.LogDebug(
                            "[OTLP gRPC]   db: system={System} op={Op}",
                            dbSystem, dbOp);

                    if (s.Events.Count > 0)
                        _logger.LogDebug(
                            "[OTLP gRPC]   events: {EventNames}",
                            string.Join(", ", s.Events.Select(e => e.Name)));

                    await _queue.EnqueueAsync(new SpanRow(
                        TenantId:       tenantId,
                        ReceivedUtc:    receivedUtc,
                        TraceId:        s.TraceId.ToByteArray(),
                        SpanId:         s.SpanId.ToByteArray(),
                        ParentSpanId:   s.ParentSpanId?.Length > 0 ? s.ParentSpanId.ToByteArray() : null,
                        Name:           spanName,
                        Kind:           (int)s.Kind,
                        StartUnixNs:    startUnixNs,
                        EndUnixNs:      endUnixNs,
                        StatusCode:     statusCode,
                        StatusMessage:  statusMsg,
                        AttributesJson: attrsJson,
                        EventsJson:     eventsJson,
                        ResourceJson:   resourceJson,
                        ScopeJson:      scopeJson,
                        ServiceName:    serviceName
                    ), context.CancellationToken);
                }
            }
        }

        _logger.LogDebug("[OTLP gRPC] Enqueued {TotalSpans} spans total pour tenant {TenantId}", totalSpans, tenantId);
        return new OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceResponse();
    }

    private static DateTimeOffset UnixNsToDto(ulong ns)
    {
        var ticks = (long)ns / 100;
        return DateTimeOffset.FromUnixTimeSeconds(0).AddTicks(ticks);
    }
}

// -------- OTLP/gRPC: logs --------
public sealed class OtlpLogsGrpcService : OpenTelemetry.Proto.Collector.Logs.V1.LogsService.LogsServiceBase
{
    private readonly TelemetryIngestQueue _queue;
    private readonly ILogger<OtlpLogsGrpcService> _logger;
    private readonly bool _devMode;

    public OtlpLogsGrpcService(TelemetryIngestQueue queue, ILogger<OtlpLogsGrpcService> logger, IOptions<DevModeOptions> devModeOptions)
    {
        _queue = queue;
        _logger = logger;
        _devMode = devModeOptions.Value.Enabled;
    }

    public override async Task<OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceResponse> Export(
        OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest request,
        ServerCallContext context)
    {
        var tenantId = OtlpTenantAuth.ResolveTenantId(context, _devMode, _logger);

        var receivedUtc = DateTimeOffset.UtcNow;
        int totalLogs = 0;

        foreach (var rl in request.ResourceLogs)
        {
            var resourceJson = OtlpJson.ToJsonResource(rl.Resource);

            var serviceName = rl.Resource?.Attributes
                .FirstOrDefault(a => a.Key == "service.name")?.Value?.StringValue ?? "unknown-service";

            foreach (var sl in rl.ScopeLogs)
            {
                var scopeJson = OtlpJson.ToJsonScope(sl.Scope);
                var scopeName = sl.Scope?.Name ?? "unknown";
                totalLogs += sl.LogRecords.Count;

                _logger.LogDebug("[OTLP gRPC] Processing {LogsCount} logs — service='{ServiceName}' scope='{ScopeName}'",
                    sl.LogRecords.Count, serviceName, scopeName);

                foreach (var lr in sl.LogRecords)
                {
                    var attrsJson = OtlpJson.ToJsonAttributes(lr.Attributes);

                    var body = lr.Body?.ValueCase switch
                    {
                        OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.StringValue => lr.Body.StringValue,
                        OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.IntValue    => lr.Body.IntValue.ToString(),
                        OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.DoubleValue => lr.Body.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        OpenTelemetry.Proto.Common.V1.AnyValue.ValueOneofCase.BoolValue   => lr.Body.BoolValue.ToString(),
                        _ => lr.Body?.ToString()
                    };

                    var bodyPreview = body?.Length > 80 ? body[..80] + "…" : body;

                    _logger.LogDebug(
                        "[OTLP gRPC] Log service='{ServiceName}' scope='{ScopeName}' severity={SeverityText}({SeverityNumber}) attrs={AttrCount} body='{BodyPreview}'",
                        serviceName, scopeName, lr.SeverityText, (int)lr.SeverityNumber, lr.Attributes.Count, bodyPreview);

                    await _queue.EnqueueAsync(new LogRow(
                        TenantId:       tenantId,
                        ReceivedUtc:    receivedUtc,
                        TraceId:        lr.TraceId?.Length > 0 ? lr.TraceId.ToByteArray() : null,
                        SpanId:         lr.SpanId?.Length > 0  ? lr.SpanId.ToByteArray()  : null,
                        SeverityNumber: (int)lr.SeverityNumber,
                        SeverityText:   lr.SeverityText,
                        Body:           body,
                        AttributesJson: attrsJson,
                        ResourceJson:   resourceJson,
                        ScopeJson:      scopeJson,
                        ServiceName:    serviceName
                    ), context.CancellationToken);
                }
            }
        }

        _logger.LogDebug("[OTLP gRPC] Enqueued {TotalLogs} logs total pour tenant {TenantId}", totalLogs, tenantId);
        return new OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceResponse();
    }
}

// -------- OTLP/gRPC: metrics (accepted but not stored) --------
public sealed class OtlpMetricsGrpcService : OpenTelemetry.Proto.Collector.Metrics.V1.MetricsService.MetricsServiceBase
{
    public override Task<OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceResponse> Export(
        OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceRequest request,
        ServerCallContext context)
        => Task.FromResult(new OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceResponse());
}
