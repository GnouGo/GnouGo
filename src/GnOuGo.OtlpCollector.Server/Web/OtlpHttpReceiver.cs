using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Web;

public static class OtlpHttpReceiver
{
    public static IEndpointRouteBuilder MapOtlpHttpReceiver(this IEndpointRouteBuilder endpoints, bool includeHealthEndpoint = true)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/v1/traces", async (
            HttpRequest req,
            TelemetryIngestQueue queue,
            ILogger<OtlpHttpReceiverMarker> logger,
            IOptions<DevModeOptions> devOpts,
            CancellationToken ct) =>
        {
            var tenantId = ResolveHttpTenantId(req.Headers["X-Tenant-Id"].ToString(), devOpts.Value.Enabled, logger);
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "X-Tenant-Id header is required" });

            return await ProcessTracesAsync(tenantId, req, queue, logger, ct);
        });

        endpoints.MapPost("/v1/logs", async (
            HttpRequest req,
            TelemetryIngestQueue queue,
            ILogger<OtlpHttpReceiverMarker> logger,
            IOptions<DevModeOptions> devOpts,
            CancellationToken ct) =>
        {
            var tenantId = ResolveHttpTenantId(req.Headers["X-Tenant-Id"].ToString(), devOpts.Value.Enabled, logger);
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "X-Tenant-Id header is required" });

            return await ProcessLogsAsync(tenantId, req, queue, logger, ct);
        });

        endpoints.MapPost("/v1/metrics", () => Results.Bytes(Array.Empty<byte>(), "application/x-protobuf"));

        endpoints.MapPost("/{tenantId}/v1/traces", async (
            string tenantId,
            HttpRequest req,
            TelemetryIngestQueue queue,
            ILogger<OtlpHttpReceiverMarker> logger,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(tenantId, out var parsedTenantId))
                return Results.BadRequest(new { error = "Invalid tenant ID format" });

            return await ProcessTracesAsync(parsedTenantId, req, queue, logger, ct);
        });

        endpoints.MapPost("/{tenantId}/v1/logs", async (
            string tenantId,
            HttpRequest req,
            TelemetryIngestQueue queue,
            ILogger<OtlpHttpReceiverMarker> logger,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(tenantId, out var parsedTenantId))
                return Results.BadRequest(new { error = "Invalid tenant ID format" });

            return await ProcessLogsAsync(parsedTenantId, req, queue, logger, ct);
        });

        endpoints.MapPost("/{tenantId}/v1/metrics", (string tenantId) =>
            Guid.TryParse(tenantId, out _)
                ? Results.Bytes(Array.Empty<byte>(), "application/x-protobuf")
                : Results.BadRequest(new { error = "Invalid tenant ID format" }));

        if (includeHealthEndpoint)
            endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        return endpoints;
    }

    private static async Task<IResult> ProcessTracesAsync(
        Guid? tenantId,
        HttpRequest req,
        TelemetryIngestQueue queue,
        ILogger logger,
        CancellationToken ct)
    {
        if (!IsProtobuf(req.ContentType))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var payload = await ReadBodyAsync(req, ct);
        var exportReq = OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest.Parser.ParseFrom(payload);

        logger.LogDebug(
            "[OTLP HTTP] Received {ResourceSpansCount} ResourceSpans for tenant {TenantId}",
            exportReq.ResourceSpans.Count,
            tenantId);

        var receivedUtc = DateTimeOffset.UtcNow;
        var totalSpans = 0;

        foreach (var resourceSpans in exportReq.ResourceSpans)
        {
            var resourceJson = OtlpJson.ToJsonResource(resourceSpans.Resource);
            var serviceName = resourceSpans.Resource?.Attributes
                .FirstOrDefault(a => a.Key == "service.name")
                ?.Value?.StringValue ?? "unknown-service";

            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                var scopeJson = OtlpJson.ToJsonScope(scopeSpans.Scope);
                var scopeName = scopeSpans.Scope?.Name ?? "unknown";
                totalSpans += scopeSpans.Spans.Count;

                logger.LogDebug(
                    "[OTLP HTTP] Processing {SpansCount} spans — service='{ServiceName}' scope='{ScopeName}'",
                    scopeSpans.Spans.Count,
                    serviceName,
                    scopeName);

                foreach (var span in scopeSpans.Spans)
                {
                    var attrsJson = OtlpJson.ToJsonAttributes(span.Attributes);
                    var eventsJson = OtlpJson.ToJsonEvents(span.Events);
                    var statusCode = span.Status?.Code is not null ? (int)span.Status.Code : 0;
                    var statusMessage = span.Status?.Message;

                    var spanName = span.Name;
                    if (string.IsNullOrWhiteSpace(spanName))
                    {
                        var httpMethod = span.Attributes
                            .FirstOrDefault(a => a.Key is "http.request.method" or "http.method")
                            ?.Value?.StringValue;

                        if (!string.IsNullOrEmpty(httpMethod))
                        {
                            var host = span.Attributes
                                .FirstOrDefault(a => a.Key is "http.host" or "server.address")
                                ?.Value?.StringValue;
                            spanName = host is not null ? $"{httpMethod} {host}" : $"HTTP {httpMethod}";
                        }
                        else
                        {
                            spanName = !string.IsNullOrWhiteSpace(scopeName) ? scopeName : $"span-{span.Kind}";
                        }
                    }

                    var startUnixNs = (long)span.StartTimeUnixNano;
                    var endUnixNs = (long)span.EndTimeUnixNano;

                    if (endUnixNs == 0 && startUnixNs > 0)
                    {
                        logger.LogWarning(
                            "[OTLP HTTP] Span '{SpanName}' [Service: {ServiceName}] has EndTimeUnixNano=0 (incomplete). Client likely uses BatchExportProcessor — switch to SimpleExportProcessor.",
                            spanName,
                            serviceName);
                        endUnixNs = startUnixNs;
                    }
                    else if (endUnixNs < startUnixNs)
                    {
                        logger.LogWarning(
                            "[OTLP HTTP] Span '{SpanName}' [Service: {ServiceName}] has End < Start (end={End}, start={Start}). Correcting.",
                            spanName,
                            serviceName,
                            endUnixNs,
                            startUnixNs);
                        endUnixNs = startUnixNs;
                    }

                    await queue.EnqueueAsync(new SpanRow(
                        TenantId: tenantId,
                        ReceivedUtc: receivedUtc,
                        TraceId: span.TraceId.ToByteArray(),
                        SpanId: span.SpanId.ToByteArray(),
                        ParentSpanId: span.ParentSpanId?.Length > 0 ? span.ParentSpanId.ToByteArray() : null,
                        Name: spanName,
                        Kind: (int)span.Kind,
                        StartUnixNs: startUnixNs,
                        EndUnixNs: endUnixNs,
                        StatusCode: statusCode,
                        StatusMessage: statusMessage,
                        AttributesJson: attrsJson,
                        EventsJson: eventsJson,
                        ResourceJson: resourceJson,
                        ScopeJson: scopeJson,
                        ServiceName: serviceName),
                        ct);

                    var durationMs = (endUnixNs - startUnixNs) / 1_000_000.0;
                    logger.LogDebug(
                        "[OTLP HTTP] Span '{SpanName}' service='{ServiceName}' scope='{ScopeName}' kind={Kind} duration={Duration}ms status={StatusCode} attrs={AttrCount} events={EventCount}",
                        spanName,
                        serviceName,
                        scopeName,
                        (int)span.Kind,
                        durationMs,
                        statusCode,
                        span.Attributes.Count,
                        span.Events.Count);
                }
            }
        }

        logger.LogDebug("[OTLP HTTP] Enqueued {TotalSpans} spans total for tenant {TenantId}", totalSpans, tenantId);
        return Results.Bytes(Array.Empty<byte>(), "application/x-protobuf");
    }

    private static async Task<IResult> ProcessLogsAsync(
        Guid? tenantId,
        HttpRequest req,
        TelemetryIngestQueue queue,
        ILogger logger,
        CancellationToken ct)
    {
        if (!IsProtobuf(req.ContentType))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var payload = await ReadBodyAsync(req, ct);
        var exportReq = OpenTelemetry.Proto.Collector.Logs.V1.ExportLogsServiceRequest.Parser.ParseFrom(payload);

        logger.LogDebug(
            "[OTLP HTTP] Received {ResourceLogsCount} ResourceLogs for tenant {TenantId}",
            exportReq.ResourceLogs.Count,
            tenantId);

        var receivedUtc = DateTimeOffset.UtcNow;
        var totalLogs = 0;

        foreach (var resourceLogs in exportReq.ResourceLogs)
        {
            var resourceJson = OtlpJson.ToJsonResource(resourceLogs.Resource);
            var serviceName = resourceLogs.Resource?.Attributes
                .FirstOrDefault(a => a.Key == "service.name")
                ?.Value?.StringValue ?? "unknown-service";

            foreach (var scopeLogs in resourceLogs.ScopeLogs)
            {
                var scopeJson = OtlpJson.ToJsonScope(scopeLogs.Scope);
                var scopeName = scopeLogs.Scope?.Name ?? "unknown";
                totalLogs += scopeLogs.LogRecords.Count;

                logger.LogDebug(
                    "[OTLP HTTP] Processing {LogsCount} logs — service='{ServiceName}' scope='{ScopeName}'",
                    scopeLogs.LogRecords.Count,
                    serviceName,
                    scopeName);

                foreach (var logRecord in scopeLogs.LogRecords)
                {
                    var attrsJson = OtlpJson.ToJsonAttributes(logRecord.Attributes);
                    var body = logRecord.Body?.StringValue ?? logRecord.Body?.ToString() ?? string.Empty;
                    var bodyPreview = body.Length > 80 ? body[..80] + "…" : body;

                    await queue.EnqueueAsync(new LogRow(
                        TenantId: tenantId,
                        ReceivedUtc: receivedUtc,
                        TraceId: logRecord.TraceId?.Length > 0 ? logRecord.TraceId.ToByteArray() : null,
                        SpanId: logRecord.SpanId?.Length > 0 ? logRecord.SpanId.ToByteArray() : null,
                        SeverityNumber: (int)logRecord.SeverityNumber,
                        SeverityText: logRecord.SeverityText,
                        Body: body,
                        AttributesJson: attrsJson,
                        ResourceJson: resourceJson,
                        ScopeJson: scopeJson,
                        ServiceName: serviceName),
                        ct);

                    logger.LogDebug(
                        "[OTLP HTTP] Log service='{ServiceName}' scope='{ScopeName}' severity={SeverityText}({SeverityNumber}) attrs={AttrCount} body='{BodyPreview}'",
                        serviceName,
                        scopeName,
                        logRecord.SeverityText,
                        (int)logRecord.SeverityNumber,
                        logRecord.Attributes.Count,
                        bodyPreview);
                }
            }
        }

        logger.LogDebug("[OTLP HTTP] Enqueued {TotalLogs} logs total for tenant {TenantId}", totalLogs, tenantId);
        return Results.Bytes(Array.Empty<byte>(), "application/x-protobuf");
    }

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
            return null;
        }

        if (!Guid.TryParse(headerValue, out var tenantId))
        {
            logger.LogWarning("[OTLP HTTP] Invalid X-Tenant-Id format: {Value}", headerValue);
            return null;
        }

        return tenantId;
    }

    private static bool IsProtobuf(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && contentType.StartsWith("application/x-protobuf", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        Stream body = req.Body;

        if (req.Headers.TryGetValue("Content-Encoding", out var enc)
            && enc.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            body = new GZipStream(body, CompressionMode.Decompress, leaveOpen: false);
        }

        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}

internal sealed class OtlpHttpReceiverMarker;

