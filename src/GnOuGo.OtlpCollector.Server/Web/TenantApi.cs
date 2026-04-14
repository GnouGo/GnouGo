using Microsoft.Extensions.Options;
using OtlpTenantCollector.Models;
using OtlpTenantCollector.Services;
using OtlpTenantCollector.Services.Options;

namespace OtlpTenantCollector.Web;

public static class TenantApi
{
    public static IEndpointRouteBuilder MapTenantApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // ── SSE real-time stream ──────────────────────────────────────────
        endpoints.MapGet("/api/tenants/traces/stream", async (
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
                await OtlpApiResponses.WriteJsonResponseAsync(
                    httpContext.Response,
                    new ApiErrorResponse("tenantId query parameter is required"),
                    OtlpApiJsonContext.Default.ApiErrorResponse,
                    StatusCodes.Status400BadRequest,
                    ct);
                return;
            }

            var l = Math.Clamp(limit ?? 50, 1, 500);

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
                    var traces = await store.GetRecentTracesAsync(tenantId, l, serviceName, startUtc, endUtc, traceIdFilter, attributeContains);
                    await OtlpApiResponses.WriteServerSentEventAsync(
                        httpContext,
                        "init",
                        traces,
                        OtlpApiJsonContext.Default.ListTraceSummaryDto,
                        ct);
                }

                // Wait for flush events and send updates
                await foreach (var _ in channel.Reader.ReadAllAsync(ct))
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                    var traces = await store.GetRecentTracesAsync(tenantId, l, serviceName, startUtc, endUtc, traceIdFilter, attributeContains);
                    await OtlpApiResponses.WriteServerSentEventAsync(
                        httpContext,
                        "update",
                        traces,
                        OtlpApiJsonContext.Default.ListTraceSummaryDto,
                        ct);
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

        endpoints.MapPost("/api/admin/tenants", async (CreateTenantRequest req, EfTelemetryStore store) =>
        {
            var tenantId = Guid.CreateVersion7();
            
            if (string.IsNullOrWhiteSpace(req.Name))
                return OtlpApiResponses.BadRequest("name is required");
            
            if (req.RetentionMinutes <= 0)
                return OtlpApiResponses.BadRequest("retentionMinutes must be > 0");

            await store.CreateTenantAsync(tenantId, req.Name.Trim(), req.RetentionMinutes);

            return OtlpApiResponses.Json(
                new TenantAdminCreatedResponse(tenantId, req.Name.Trim(), req.RetentionMinutes),
                OtlpApiJsonContext.Default.TenantAdminCreatedResponse);
        });

        endpoints.MapGet("/api/admin/tenants", async (EfTelemetryStore store) =>
        {
            var tenants = await store.GetAllTenantsAsync();
            var payload = TelemetryApiMapper.ToTenantSummaryResponses(tenants);
            return OtlpApiResponses.Json(payload, OtlpApiJsonContext.Default.ListTenantSummaryResponse);
        });

        endpoints.MapDelete("/api/admin/tenants/{tenantId:guid}", async (Guid tenantId, EfTelemetryStore store) =>
        {
            var tenant = await store.GetTenantAsync(tenantId);
            if (tenant is null)
                return OtlpApiResponses.NotFound("tenant not found");

            await store.DeleteTenantAsync(tenantId);
            return OtlpApiResponses.OkMessage("tenant deleted successfully");
        });

        // Purge all telemetry data for a tenant (keeps the tenant)
        // tenantId is optional query param — null means "no tenant" (DevMode)
        endpoints.MapDelete("/api/tenants/data", async (Guid? tenantId, EfTelemetryStore store, IOptions<DevModeOptions> devOpts) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
                return OtlpApiResponses.BadRequest("tenantId query parameter is required");

            if (tenantId is not null)
            {
                var tenant = await store.GetTenantAsync(tenantId.Value);
                if (tenant is null)
                    return OtlpApiResponses.NotFound("tenant not found");
            }

            var deleted = await store.PurgeTenantDataAsync(tenantId);
            return OtlpApiResponses.OkMessage($"Purged {deleted} records for tenant {tenantId?.ToString() ?? "(null)"}");
        });

        // Traces endpoints — tenantId is optional query param
        endpoints.MapGet("/api/tenants/traces/recent", async (
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
                return OtlpApiResponses.BadRequest("tenantId query parameter is required");

            var l = Math.Clamp(limit ?? 50, 1, 500);
            var traces = await store.GetRecentTracesAsync(tenantId, l, serviceName, startUtc, endUtc, traceIdFilter, attributeContains);
            return OtlpApiResponses.Json(traces, OtlpApiJsonContext.Default.ListTraceSummaryDto);
        });

        endpoints.MapGet("/api/tenants/traces/{traceId}", async (
            string traceId,
            Guid? tenantId,
            EfTelemetryStore store,
            IOptions<DevModeOptions> devOpts) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
                return OtlpApiResponses.BadRequest("tenantId query parameter is required");

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

            return OtlpApiResponses.Json(trace, OtlpApiJsonContext.Default.TraceDto);
        });

        // Logs endpoints — tenantId is optional query param
        endpoints.MapGet("/api/tenants/logs/recent", async (
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
                return OtlpApiResponses.BadRequest("tenantId query parameter is required");

            var l = Math.Clamp(limit ?? 100, 1, 1000);
            var logs = await store.GetRecentLogsAsync(tenantId, l, serviceName, startUtc, endUtc, severityLevels, traceIdFilter, attributeContains);
            var payload = TelemetryApiMapper.ToLogResponses(logs);
            return OtlpApiResponses.Json(payload, OtlpApiJsonContext.Default.ListTelemetryLogResponse);
        });

        // ── SSE real-time stream for logs ──────────────────────────────────
        endpoints.MapGet("/api/tenants/logs/stream", async (
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
                await OtlpApiResponses.WriteJsonResponseAsync(
                    httpContext.Response,
                    new ApiErrorResponse("tenantId query parameter is required"),
                    OtlpApiJsonContext.Default.ApiErrorResponse,
                    StatusCodes.Status400BadRequest,
                    ct);
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
                    var payload = TelemetryApiMapper.ToLogResponses(logs);
                    await OtlpApiResponses.WriteServerSentEventAsync(
                        httpContext,
                        "init",
                        payload,
                        OtlpApiJsonContext.Default.ListTelemetryLogResponse,
                        ct);
                }

                await foreach (var _ in channel.Reader.ReadAllAsync(ct))
                {
                    using var scope = scopeFactory.CreateScope();
                    var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
                    var logs = await store.GetRecentLogsAsync(tenantId, l, serviceName);
                    var payload = TelemetryApiMapper.ToLogResponses(logs);
                    await OtlpApiResponses.WriteServerSentEventAsync(
                        httpContext,
                        "update",
                        payload,
                        OtlpApiJsonContext.Default.ListTelemetryLogResponse,
                        ct);
                }
            }
            catch (OperationCanceledException) { /* Client disconnected */ }
            finally
            {
                eventBus.Unsubscribe(channel);
            }
        });

        endpoints.MapGet("/api/tenants/traces/{traceId}/logs", async (
            string traceId,
            Guid? tenantId,
            EfTelemetryStore store,
            IOptions<DevModeOptions> devOpts) =>
        {
            if (tenantId is null && !devOpts.Value.Enabled)
                return OtlpApiResponses.BadRequest("tenantId query parameter is required");

            var traceIdBytes = Convert.FromHexString(traceId);
            var logs = await store.GetLogsForTraceAsync(tenantId, traceIdBytes);
            var payload = TelemetryApiMapper.ToLogResponses(logs, includeServiceName: false);
            return OtlpApiResponses.Json(payload, OtlpApiJsonContext.Default.ListTelemetryLogResponse);
        });

        return endpoints;
    }
}
