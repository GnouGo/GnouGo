using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Web;

public static class OtlpHttpReceiver
{
    public static void MapOtlpHttpReceiver(this WebApplication app)
    {
        // Endpoints standard OTLP avec header X-Tenant-Id
        app.MapPost("/v1/traces", async (HttpRequest req, TelemetryIngestQueue queue, ILogger<Program> logger, IOptions<DevModeOptions> devOpts, CancellationToken ct) =>
        {
            var tenantId = ResolveHttpTenantId(req.Headers["X-Tenant-Id"].ToString(), devOpts.Value.Enabled, logger);
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "X-Tenant-Id header is required" });

            return await ProcessTracesAsync(tenantId, req, queue, logger, ct);
        });

        app.MapPost("/v1/logs", async (HttpRequest req, TelemetryIngestQueue queue, ILogger<Program> logger, IOptions<DevModeOptions> devOpts, CancellationToken ct) =>
        {
            var tenantId = ResolveHttpTenantId(req.Headers["X-Tenant-Id"].ToString(), devOpts.Value.Enabled, logger);
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "X-Tenant-Id header is required" });

            return await ProcessLogsAsync(tenantId, req, queue, logger, ct);
        });

        // Endpoints alternatifs avec tenant-id dans l'URL (rétrocompatibilité)
        app.MapPost("/{tenantId}/v1/traces", async (string tenantId, HttpRequest req, TelemetryIngestQueue queue, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!Guid.TryParse(tenantId, out var tid))
                return Results.BadRequest(new { error = "Invalid tenant ID format" });
            return await ProcessTracesAsync(tid, req, queue, logger, ct);
        });

        app.MapPost("/{tenantId}/v1/logs", async (string tenantId, HttpRequest req, TelemetryIngestQueue queue, ILogger<Program> logger, CancellationToken ct) =>
        {
            if (!Guid.TryParse(tenantId, out var tid))
                return Results.BadRequest(new { error = "Invalid tenant ID format" });
            return await ProcessLogsAsync(tid, req, queue, logger, ct);
        });

        app.MapPost("/{tenantId}/v1/metrics", (string tenantId) => 
            Results.Bytes(Array.Empty<byte>(), "application/x-protobuf"));
            
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    }

    private static async Task<IResult> ProcessTracesAsync(Guid? tenantId, HttpRequest req, TelemetryIngestQueue queue, ILogger<Program> logger, CancellationToken ct)
    {
        logger.LogInformation("[OTLP HTTP] POST /v1/traces - Receiving traces for tenant {TenantId}", tenantId?.ToString() ?? "(null)");
        
        if (!IsProtobuf(req.ContentType)) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var payload = await ReadBodyAsync(req, ct);
        var exportReq = OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest.Parser.ParseFrom(payload);

        logger.LogInformation("[OTLP HTTP] Received {ResourceSpansCount} ResourceSpans for tenant {TenantId}",
            exportReq.ResourceSpans.Count, tenantId);
        
        var receivedUtc = DateTimeOffset.UtcNow;
        int totalSpans = 0;
        foreach (var rs in exportReq.ResourceSpans)
        {
            var resourceJson = OtlpJson.ToJsonResource(rs.Resource);
            
            // Extraire le ServiceName depuis les attributs Resource
            var serviceName = rs.Resource?.Attributes
                .FirstOrDefault(a => a.Key == "service.name")
                ?.Value?.StringValue ?? "unknown-service";
            
            foreach (var ss in rs.ScopeSpans)
            {
                var scopeJson = OtlpJson.ToJsonScope(ss.Scope);
                var scopeName = ss.Scope?.Name ?? "unknown";
                totalSpans += ss.Spans.Count;
                logger.LogInformation("[OTLP HTTP] Processing {SpansCount} spans — service='{ServiceName}' scope='{ScopeName}'",
                    ss.Spans.Count, serviceName, scopeName);

                foreach (var s in ss.Spans)
                {
                    var attrsJson = OtlpJson.ToJsonAttributes(s.Attributes);
                    var eventsJson = OtlpJson.ToJsonEvents(s.Events);
                    var statusCode = s.Status?.Code != null ? (int)s.Status.Code : 0;
                    var statusMsg = s.Status?.Message;

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
                    }

                    // Validation et correction des timestamps
                    var startUnixNs = (long)s.StartTimeUnixNano;
                    var endUnixNs = (long)s.EndTimeUnixNano;
                    
                    if (endUnixNs == 0 && startUnixNs > 0)
                    {
                        logger.LogWarning("[OTLP HTTP] Span '{SpanName}' [Service: {ServiceName}] has EndTimeUnixNano=0 (incomplete). Client likely uses BatchExportProcessor — switch to SimpleExportProcessor.", 
                            s.Name, serviceName);
                        endUnixNs = startUnixNs;
                    }
                    else if (endUnixNs < startUnixNs)
                    {
                        logger.LogWarning("[OTLP HTTP] Span '{SpanName}' [Service: {ServiceName}] has End < Start (end={End}, start={Start}). Correcting.", 
                            s.Name, serviceName, endUnixNs, startUnixNs);
                        endUnixNs = startUnixNs;
                    }

                    await queue.EnqueueAsync(new SpanRow(
                        TenantId: tenantId,
                        ReceivedUtc: receivedUtc,
                        TraceId: s.TraceId.ToByteArray(),
                        SpanId: s.SpanId.ToByteArray(),
                        ParentSpanId: s.ParentSpanId?.Length > 0 ? s.ParentSpanId.ToByteArray() : null,
                        Name: spanName,
                        Kind: (int)s.Kind,
                        StartUnixNs: startUnixNs,
                        EndUnixNs: endUnixNs,
                        StatusCode: statusCode,
                        StatusMessage: statusMsg,
                        AttributesJson: attrsJson,
                        EventsJson: eventsJson,
                        ResourceJson: resourceJson,
                        ScopeJson: scopeJson,
                        ServiceName: serviceName
                    ), ct);
                    
                    var durationMs = (endUnixNs - startUnixNs) / 1_000_000.0;
                    logger.LogInformation(
                        "[OTLP HTTP] Span '{SpanName}' service='{ServiceName}' scope='{ScopeName}' kind={Kind} duration={Duration}ms status={StatusCode} attrs={AttrCount} events={EventCount}",
                        spanName, serviceName, scopeName, (int)s.Kind, durationMs, statusCode, s.Attributes.Count, s.Events.Count);
                }
            }
        }
        
        logger.LogInformation("[OTLP HTTP] Enqueued {TotalSpans} spans total for tenant {TenantId}", totalSpans, tenantId);

        return Results.Bytes(Array.Empty<byte>(), "application/x-protobuf");
    }

    private static async Task<IResult> ProcessLogsAsync(Guid? tenantId, HttpRequest req, TelemetryIngestQueue queue, ILogger<Program> logger, CancellationToken ct)
    {
        logger.LogInformation("[OTLP HTTP] POST /v1/logs - Receiving logs for tenant {TenantId}", tenantId?.ToString() ?? "(null)");
        
        if (!IsProtobuf(req.ContentType)) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var payload = await ReadBodyAsync(req, ct);
        var exportReq = OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest.Parser.ParseFrom(payload);

        logger.LogInformation("[OTLP HTTP] Received {ResourceLogsCount} ResourceLogs for tenant {TenantId}",
            exportReq.ResourceLogs.Count, tenantId);

        var receivedUtc = DateTimeOffset.UtcNow;
        int totalLogs = 0;
        foreach (var rl in exportReq.ResourceLogs)
        {
            var resourceJson = OtlpJson.ToJsonResource(rl.Resource);
            
            // Extraire le ServiceName depuis les attributs Resource
            var serviceName = rl.Resource?.Attributes
                .FirstOrDefault(a => a.Key == "service.name")
                ?.Value?.StringValue ?? "unknown-service";
            
            foreach (var sl in rl.ScopeLogs)
            {
                var scopeJson = OtlpJson.ToJsonScope(sl.Scope);
                var scopeName = sl.Scope?.Name ?? "unknown";
                totalLogs += sl.LogRecords.Count;
                logger.LogInformation("[OTLP HTTP] Processing {LogsCount} logs — service='{ServiceName}' scope='{ScopeName}'",
                    sl.LogRecords.Count, serviceName, scopeName);
                
                foreach (var lr in sl.LogRecords)
                {
                    var attrsJson = OtlpJson.ToJsonAttributes(lr.Attributes);
                    var body = lr.Body?.StringValue ?? lr.Body?.ToString() ?? "";
                    var bodyPreview = body.Length > 80 ? body[..80] + "…" : body;

                    await queue.EnqueueAsync(new LogRow(
                        TenantId: tenantId,
                        ReceivedUtc: receivedUtc,
                        TraceId: lr.TraceId?.Length > 0 ? lr.TraceId.ToByteArray() : null,
                        SpanId: lr.SpanId?.Length > 0 ? lr.SpanId.ToByteArray() : null,
                        SeverityNumber: (int)lr.SeverityNumber,
                        SeverityText: lr.SeverityText,
                        Body: body,
                        AttributesJson: attrsJson,
                        ResourceJson: resourceJson,
                        ScopeJson: scopeJson,
                        ServiceName: serviceName
                    ), ct);
                    
                    logger.LogInformation(
                        "[OTLP HTTP] Log service='{ServiceName}' scope='{ScopeName}' severity={SeverityText}({SeverityNumber}) attrs={AttrCount} body='{BodyPreview}'",
                        serviceName, scopeName, lr.SeverityText, (int)lr.SeverityNumber, lr.Attributes.Count, bodyPreview);
                }
            }
        }
        
        logger.LogInformation("[OTLP HTTP] Enqueued {TotalLogs} logs total for tenant {TenantId}", totalLogs, tenantId);

        return Results.Bytes(Array.Empty<byte>(), "application/x-protobuf");
    }

    /// <summary>
    /// Resolves tenant ID from HTTP header.
    /// In DevMode: returns null when absent. In production: returns null (caller must return 400).
    /// </summary>
    private static Guid? ResolveHttpTenantId(string? headerValue, bool devMode, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            if (devMode)
            {
                logger.LogDebug("[OTLP HTTP] No X-Tenant-Id header — DevMode: using null tenant.");
                return null;
            }
            logger.LogWarning("[OTLP HTTP] Missing X-Tenant-Id header");
            return null; // caller checks devMode and returns 400
        }

        if (!Guid.TryParse(headerValue, out var tenantId))
        {
            logger.LogWarning("[OTLP HTTP] Invalid X-Tenant-Id format: {Value}", headerValue);
            return null;
        }

        return tenantId;
    }

    private static bool IsProtobuf(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) &&
        contentType.StartsWith("application/x-protobuf", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        Stream body = req.Body;

        if (req.Headers.TryGetValue("Content-Encoding", out var enc) &&
            enc.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            body = new GZipStream(body, CompressionMode.Decompress, leaveOpen: false);
        }

        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
