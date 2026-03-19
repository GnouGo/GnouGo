using System.Text.Json;
using Microsoft.Extensions.Options;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Web;

public static class TenantApi
{
    private static readonly JsonSerializerOptions SseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static void MapTenantApi(this WebApplication app)
    {
        // ── SSE real-time stream ──────────────────────────────────────────
        app.MapGet("/api/tenants/traces/stream", async (
            Guid? tenantId,
            int? limit,
            string? serviceName,
            DateTimeOffset? startUtc,
            DateTimeOffset? endUtc,
            string? traceIdFilter,
            string? attributeContains,
            TelemetryEventBus eventBus,
            IServiceScopeFactory scopeFactory,
            IOptions<DevModeOptions> devOpts,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsync("{\"error\":\"tenantId query parameter is required\"}", ct);
                return;
            }

            var l = Math.Clamp(limit ?? 50, 1, 500);

            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";

            var writer = httpContext.Response.BodyWriter;
            var channel = eventBus.Subscribe();

            try
            {
                // Send initial snapshot
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                    var traces = await store.GetRecentTracesAsync(tenantId, l, serviceName, startUtc, endUtc, traceIdFilter, attributeContains);
                    var json = JsonSerializer.Serialize(traces, SseJson);
                    await httpContext.Response.WriteAsync($"event: init\ndata: {json}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }

                // Wait for flush events and send updates
                await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                    var traces = await store.GetRecentTracesAsync(tenantId, l, serviceName, startUtc, endUtc, traceIdFilter, attributeContains);
                    var json = JsonSerializer.Serialize(traces, SseJson);
                    await httpContext.Response.WriteAsync($"event: update\ndata: {json}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal
            }
            finally
            {
                eventBus.Unsubscribe(channel);
            }
        });

        app.MapPost("/api/admin/tenants", async (CreateTenantRequest req, EfTelemetryStore store) =>
        {
            var tenantId = Guid.CreateVersion7();
            
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });
            
            if (req.RetentionMinutes <= 0)
                return Results.BadRequest(new { error = "retentionMinutes must be > 0" });

            await store.CreateTenantAsync(tenantId, req.Name.Trim(), req.RetentionMinutes);

            return Results.Ok(new TenantCreatedResponse(tenantId, req.Name.Trim(), req.RetentionMinutes));
        });

        app.MapGet("/api/admin/tenants", async (EfTelemetryStore store) =>
        {
            var tenants = await store.GetAllTenantsAsync();
            return Results.Ok(tenants.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                retentionMinutes = t.RetentionMinutes,
                createdAt = t.CreatedUtc
            }));
        });

        app.MapDelete("/api/admin/tenants/{tenantId:guid}", async (Guid tenantId, EfTelemetryStore store) =>
        {
            var tenant = await store.GetTenantAsync(tenantId);
            if (tenant is null)
                return Results.NotFound(new { error = "tenant not found" });

            await store.DeleteTenantAsync(tenantId);
            return Results.Ok(new { message = "tenant deleted successfully" });
        });

        // Purge all telemetry data for a tenant (keeps the tenant)
        // tenantId is optional query param — null means "no tenant" (DevMode)
        app.MapDelete("/api/tenants/data", async (Guid? tenantId, EfTelemetryStore store, IOptions<DevModeOptions> devOpts) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "tenantId query parameter is required" });

            if (tenantId is not null)
            {
                var tenant = await store.GetTenantAsync(tenantId.Value);
                if (tenant is null)
                    return Results.NotFound(new { error = "tenant not found" });
            }

            var deleted = await store.PurgeTenantDataAsync(tenantId);
            return Results.Ok(new { message = $"Purged {deleted} records for tenant {tenantId?.ToString() ?? "(null)"}" });
        });

        // Traces endpoints — tenantId is optional query param
        app.MapGet("/api/tenants/traces/recent", async (
            Guid? tenantId, 
            int? limit, 
            string? serviceName,
            DateTimeOffset? startUtc,
            DateTimeOffset? endUtc,
            string? traceIdFilter,
            string? attributeContains,
            EfTelemetryStore store,
            IOptions<DevModeOptions> devOpts) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "tenantId query parameter is required" });

            var l = Math.Clamp(limit ?? 50, 1, 500);
            return Results.Ok(await store.GetRecentTracesAsync(tenantId, l, serviceName, startUtc, endUtc, traceIdFilter, attributeContains));
        });

        app.MapGet("/api/tenants/traces/{traceId}", async (
            string traceId,
            Guid? tenantId,
            EfTelemetryStore store,
            IOptions<DevModeOptions> devOpts) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "tenantId query parameter is required" });

            var traceIdBytes = Convert.FromHexString(traceId);
            var spans = await store.GetTraceSpansAsync(tenantId, traceIdBytes);
            
            if (spans.Count == 0) return Results.NotFound();

            var spanDtos = spans.Select(s => OtlpJson.SpanRecordToDto(s)).ToList();
            var trace = new TraceDto(
                TraceId: traceId.ToLowerInvariant(),
                StartUtc: spanDtos.Min(s => s.StartUtc),
                EndUtc: spanDtos.Max(s => s.EndUtc),
                Spans: spanDtos
            );

            return Results.Ok(trace);
        });

        // Logs endpoints — tenantId is optional query param
        app.MapGet("/api/tenants/logs/recent", async (
            Guid? tenantId, 
            int? limit, 
            string? serviceName,
            DateTimeOffset? startUtc,
            DateTimeOffset? endUtc,
            [Microsoft.AspNetCore.Mvc.FromQuery] int[]? severityLevels,
            string? traceIdFilter,
            string? attributeContains,
            EfTelemetryStore store,
            IOptions<DevModeOptions> devOpts) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "tenantId query parameter is required" });

            var l = Math.Clamp(limit ?? 100, 1, 1000);
            var logs = await store.GetRecentLogsAsync(tenantId, l, serviceName, startUtc, endUtc, severityLevels, traceIdFilter, attributeContains);
            
            var logDtos = logs.Select(log => new
            {
                tenantId = log.TenantId,
                receivedUtc = log.ReceivedUtc,
                traceId = log.TraceId != null ? Convert.ToHexString(log.TraceId) : null,
                spanId = log.SpanId != null ? Convert.ToHexString(log.SpanId) : null,
                severityNumber = log.SeverityNumber,
                severityText = log.SeverityText,
                body = log.Body,
                serviceName = log.ServiceName,
                attributes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.AttributesJson ?? "{}"),
                resource = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.ResourceJson ?? "{}"),
                scope = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.ScopeJson ?? "{}")
            }).ToList();

            return Results.Ok(logDtos);
        });

        // ── SSE real-time stream for logs ──────────────────────────────────
        app.MapGet("/api/tenants/logs/stream", async (
            Guid? tenantId,
            int? limit,
            string? serviceName,
            TelemetryEventBus eventBus,
            IServiceScopeFactory scopeFactory,
            IOptions<DevModeOptions> devOpts,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsync("{\"error\":\"tenantId query parameter is required\"}", ct);
                return;
            }

            var l = Math.Clamp(limit ?? 100, 1, 1000);

            httpContext.Response.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";

            var channel = eventBus.Subscribe();

            try
            {
                // Send initial snapshot
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                    var logs = await store.GetRecentLogsAsync(tenantId, l, serviceName);
                    var logDtos2 = logs.Select(log => new
                    {
                        tenantId = log.TenantId,
                        receivedUtc = log.ReceivedUtc,
                        traceId = log.TraceId != null ? Convert.ToHexString(log.TraceId) : null,
                        spanId = log.SpanId != null ? Convert.ToHexString(log.SpanId) : null,
                        severityNumber = log.SeverityNumber,
                        severityText = log.SeverityText,
                        body = log.Body,
                        serviceName = log.ServiceName,
                        attributes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.AttributesJson ?? "{}"),
                        resource = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.ResourceJson ?? "{}"),
                        scope = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.ScopeJson ?? "{}")
                    }).ToList();
                    var json = JsonSerializer.Serialize(logDtos2, SseJson);
                    await httpContext.Response.WriteAsync($"event: init\ndata: {json}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }

                await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                    var logs = await store.GetRecentLogsAsync(tenantId, l, serviceName);
                    var logDtos2 = logs.Select(log => new
                    {
                        tenantId = log.TenantId,
                        receivedUtc = log.ReceivedUtc,
                        traceId = log.TraceId != null ? Convert.ToHexString(log.TraceId) : null,
                        spanId = log.SpanId != null ? Convert.ToHexString(log.SpanId) : null,
                        severityNumber = log.SeverityNumber,
                        severityText = log.SeverityText,
                        body = log.Body,
                        serviceName = log.ServiceName,
                        attributes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.AttributesJson ?? "{}"),
                        resource = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.ResourceJson ?? "{}"),
                        scope = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.ScopeJson ?? "{}")
                    }).ToList();
                    var json = JsonSerializer.Serialize(logDtos2, SseJson);
                    await httpContext.Response.WriteAsync($"event: update\ndata: {json}\n\n", ct);
                    await httpContext.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* Client disconnected */ }
            finally
            {
                eventBus.Unsubscribe(channel);
            }
        });

        app.MapGet("/api/tenants/traces/{traceId}/logs", async (
            string traceId,
            Guid? tenantId,
            EfTelemetryStore store,
            IOptions<DevModeOptions> devOpts) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
                return Results.BadRequest(new { error = "tenantId query parameter is required" });

            var traceIdBytes = Convert.FromHexString(traceId);
            var logs = await store.GetLogsForTraceAsync(tenantId, traceIdBytes);
            
            var logDtos = logs.Select(log => new
            {
                tenantId = log.TenantId,
                receivedUtc = log.ReceivedUtc,
                traceId = log.TraceId != null ? Convert.ToHexString(log.TraceId) : null,
                spanId = log.SpanId != null ? Convert.ToHexString(log.SpanId) : null,
                severityNumber = log.SeverityNumber,
                severityText = log.SeverityText,
                body = log.Body,
                attributes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.AttributesJson ?? "{}"),
                resource = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.ResourceJson ?? "{}"),
                scope = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(log.ScopeJson ?? "{}")
            }).ToList();

            return Results.Ok(logDtos);
        });
    }

    public sealed record CreateTenantRequest(string Name, int RetentionMinutes);
    public sealed record TenantCreatedResponse(Guid Id, string Name, int RetentionMinutes);
}
