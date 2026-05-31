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

        endpoints.MapMethods("/api/tenants/traces/stream", [HttpMethods.Get], TraceStreamAsync);
        endpoints.MapMethods("/api/admin/tenants", [HttpMethods.Post], CreateTenantAsync);
        endpoints.MapMethods("/api/admin/tenants", [HttpMethods.Get], GetTenantsAsync);
        endpoints.MapMethods("/api/admin/tenants/{tenantId:guid}", [HttpMethods.Delete], DeleteTenantAsync);
        endpoints.MapMethods("/api/tenants/data", [HttpMethods.Delete], PurgeTenantDataAsync);
        endpoints.MapMethods("/api/tenants/traces/recent", [HttpMethods.Get], GetRecentTracesAsync);
        endpoints.MapMethods("/api/tenants/traces/{traceId}", [HttpMethods.Get], GetTraceAsync);
        endpoints.MapMethods("/api/tenants/logs/recent", [HttpMethods.Get], GetRecentLogsAsync);
        endpoints.MapMethods("/api/tenants/logs/stream", [HttpMethods.Get], LogStreamAsync);
        endpoints.MapMethods("/api/tenants/traces/{traceId}/logs", [HttpMethods.Get], GetTraceLogsAsync);

        return endpoints;
    }

    private static async Task TraceStreamAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var tenantId = GetGuidQuery(httpContext, "tenantId");
        var eventBus = httpContext.RequestServices.GetRequiredService<TelemetryEventBus>();
        var scopeFactory = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var devOpts = httpContext.RequestServices.GetRequiredService<IOptions<DevModeOptions>>();
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TenantApi).FullName ?? nameof(TenantApi));

        if (!await EnsureTenantAllowedAsync(httpContext, tenantId, devOpts.Value.Enabled, ct))
            return;

        var limit = Math.Clamp(GetIntQuery(httpContext, "limit") ?? 50, 1, 500);
        var serviceName = GetStringQuery(httpContext, "serviceName");
        var startUtc = GetDateTimeOffsetQuery(httpContext, "startUtc");
        var endUtc = GetDateTimeOffsetQuery(httpContext, "endUtc");
        var traceIdFilter = GetStringQuery(httpContext, "traceIdFilter");
        var attributeContains = GetStringQuery(httpContext, "attributeContains");

        PrepareSseResponse(httpContext);
        var channel = eventBus.Subscribe();
        try
        {
            await WriteTraceSnapshotAsync(httpContext, scopeFactory, tenantId, limit, serviceName, startUtc, endUtc, traceIdFilter, attributeContains, "init", ct);
            await foreach (var _ in channel.Reader.ReadAllAsync(ct))
                await WriteTraceSnapshotAsync(httpContext, scopeFactory, tenantId, limit, serviceName, startUtc, endUtc, traceIdFilter, attributeContains, "update", ct);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(ex, "Tenant trace SSE stream was cancelled, likely because the client disconnected.");
        }
        finally
        {
            eventBus.Unsubscribe(channel);
        }
    }

    private static async Task CreateTenantAsync(HttpContext httpContext)
    {
        var req = await System.Text.Json.JsonSerializer.DeserializeAsync(
            httpContext.Request.Body,
            OtlpApiJsonContext.Default.CreateTenantRequest,
            httpContext.RequestAborted);

        if (req is null || string.IsNullOrWhiteSpace(req.Name))
        {
            await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.BadRequest("name is required"));
            return;
        }

        if (req.RetentionMinutes <= 0)
        {
            await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.BadRequest("retentionMinutes must be > 0"));
            return;
        }

        var tenantId = Guid.CreateVersion7();
        var name = req.Name.Trim();
        var store = httpContext.RequestServices.GetRequiredService<EfTelemetryStore>();
        await store.CreateTenantAsync(tenantId, name, req.RetentionMinutes);
        await OtlpApiResponses.ExecuteAsync(
            httpContext,
            OtlpApiResponses.Json(
                new TenantAdminCreatedResponse(tenantId, name, req.RetentionMinutes),
                OtlpApiJsonContext.Default.TenantAdminCreatedResponse));
    }

    private static async Task GetTenantsAsync(HttpContext httpContext)
    {
        var store = httpContext.RequestServices.GetRequiredService<EfTelemetryStore>();
        var tenants = await store.GetAllTenantsAsync();
        var payload = TelemetryApiMapper.ToTenantSummaryResponses(tenants);
        await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.Json(payload, OtlpApiJsonContext.Default.ListTenantSummaryResponse));
    }

    private static async Task DeleteTenantAsync(HttpContext httpContext)
    {
        var tenantId = GetGuidRoute(httpContext, "tenantId");
        if (tenantId is null)
        {
            await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.BadRequest("Invalid tenant ID format"));
            return;
        }

        var store = httpContext.RequestServices.GetRequiredService<EfTelemetryStore>();
        var tenant = await store.GetTenantAsync(tenantId.Value);
        if (tenant is null)
        {
            await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.NotFound("tenant not found"));
            return;
        }

        await store.DeleteTenantAsync(tenantId.Value);
        await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.OkMessage("tenant deleted successfully"));
    }

    private static async Task PurgeTenantDataAsync(HttpContext httpContext)
    {
        var tenantId = GetGuidQuery(httpContext, "tenantId");
        var store = httpContext.RequestServices.GetRequiredService<EfTelemetryStore>();
        var devOpts = httpContext.RequestServices.GetRequiredService<IOptions<DevModeOptions>>();

        if (!await EnsureTenantAllowedAsync(httpContext, tenantId, devOpts.Value.Enabled, httpContext.RequestAborted))
            return;

        if (tenantId is not null && await store.GetTenantAsync(tenantId.Value) is null)
        {
            await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.NotFound("tenant not found"));
            return;
        }

        var deleted = await store.PurgeTenantDataAsync(tenantId);
        await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.OkMessage($"Purged {deleted} records for tenant {tenantId?.ToString() ?? "(null)"}"));
    }

    private static async Task GetRecentTracesAsync(HttpContext httpContext)
    {
        var tenantId = GetGuidQuery(httpContext, "tenantId");
        var devOpts = httpContext.RequestServices.GetRequiredService<IOptions<DevModeOptions>>();
        if (!await EnsureTenantAllowedAsync(httpContext, tenantId, devOpts.Value.Enabled, httpContext.RequestAborted))
            return;

        var store = httpContext.RequestServices.GetRequiredService<EfTelemetryStore>();
        var traces = await store.GetRecentTracesAsync(
            tenantId,
            Math.Clamp(GetIntQuery(httpContext, "limit") ?? 50, 1, 500),
            GetStringQuery(httpContext, "serviceName"),
            GetDateTimeOffsetQuery(httpContext, "startUtc"),
            GetDateTimeOffsetQuery(httpContext, "endUtc"),
            GetStringQuery(httpContext, "traceIdFilter"),
            GetStringQuery(httpContext, "attributeContains"));
        await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.Json(traces, OtlpApiJsonContext.Default.ListTraceSummaryDto));
    }

    private static async Task GetTraceAsync(HttpContext httpContext)
    {
        var tenantId = GetGuidQuery(httpContext, "tenantId");
        var devOpts = httpContext.RequestServices.GetRequiredService<IOptions<DevModeOptions>>();
        if (!await EnsureTenantAllowedAsync(httpContext, tenantId, devOpts.Value.Enabled, httpContext.RequestAborted))
            return;

        var traceId = GetStringRoute(httpContext, "traceId");
        if (!TryParseHex(traceId, out var traceIdBytes))
        {
            await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.BadRequest("Invalid trace ID format"));
            return;
        }

        var store = httpContext.RequestServices.GetRequiredService<EfTelemetryStore>();
        var spans = await store.GetTraceSpansAsync(tenantId, traceIdBytes);
        if (spans.Count == 0)
        {
            await OtlpApiResponses.ExecuteAsync(httpContext, Results.NotFound());
            return;
        }

        var spanDtos = spans.Select(OtlpJson.SpanRecordToDto).ToList();
        var trace = new TraceDto(traceId!.ToLowerInvariant(), spanDtos.Min(s => s.StartUtc), spanDtos.Max(s => s.EndUtc), spanDtos);
        await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.Json(trace, OtlpApiJsonContext.Default.TraceDto));
    }

    private static async Task GetRecentLogsAsync(HttpContext httpContext)
    {
        var tenantId = GetGuidQuery(httpContext, "tenantId");
        var devOpts = httpContext.RequestServices.GetRequiredService<IOptions<DevModeOptions>>();
        if (!await EnsureTenantAllowedAsync(httpContext, tenantId, devOpts.Value.Enabled, httpContext.RequestAborted))
            return;

        var store = httpContext.RequestServices.GetRequiredService<EfTelemetryStore>();
        var logs = await store.GetRecentLogsAsync(
            tenantId,
            Math.Clamp(GetIntQuery(httpContext, "limit") ?? 100, 1, 1000),
            GetStringQuery(httpContext, "serviceName"),
            GetDateTimeOffsetQuery(httpContext, "startUtc"),
            GetDateTimeOffsetQuery(httpContext, "endUtc"),
            GetIntArrayQuery(httpContext, "severityLevels"),
            GetStringQuery(httpContext, "traceIdFilter"),
            GetStringQuery(httpContext, "attributeContains"));
        var payload = TelemetryApiMapper.ToLogResponses(logs);
        await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.Json(payload, OtlpApiJsonContext.Default.ListTelemetryLogResponse));
    }

    private static async Task LogStreamAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        var tenantId = GetGuidQuery(httpContext, "tenantId");
        var eventBus = httpContext.RequestServices.GetRequiredService<TelemetryEventBus>();
        var scopeFactory = httpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var devOpts = httpContext.RequestServices.GetRequiredService<IOptions<DevModeOptions>>();
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(TenantApi).FullName ?? nameof(TenantApi));

        if (!await EnsureTenantAllowedAsync(httpContext, tenantId, devOpts.Value.Enabled, ct))
            return;

        var limit = Math.Clamp(GetIntQuery(httpContext, "limit") ?? 100, 1, 1000);
        var serviceName = GetStringQuery(httpContext, "serviceName");
        PrepareSseResponse(httpContext);
        var channel = eventBus.Subscribe();
        try
        {
            await WriteLogSnapshotAsync(httpContext, scopeFactory, tenantId, limit, serviceName, "init", ct);
            await foreach (var _ in channel.Reader.ReadAllAsync(ct))
                await WriteLogSnapshotAsync(httpContext, scopeFactory, tenantId, limit, serviceName, "update", ct);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(ex, "Tenant log SSE stream was cancelled, likely because the client disconnected.");
        }
        finally
        {
            eventBus.Unsubscribe(channel);
        }
    }

    private static async Task GetTraceLogsAsync(HttpContext httpContext)
    {
        var tenantId = GetGuidQuery(httpContext, "tenantId");
        var devOpts = httpContext.RequestServices.GetRequiredService<IOptions<DevModeOptions>>();
        if (!await EnsureTenantAllowedAsync(httpContext, tenantId, devOpts.Value.Enabled, httpContext.RequestAborted))
            return;

        if (!TryParseHex(GetStringRoute(httpContext, "traceId"), out var traceIdBytes))
        {
            await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.BadRequest("Invalid trace ID format"));
            return;
        }

        var store = httpContext.RequestServices.GetRequiredService<EfTelemetryStore>();
        var logs = await store.GetLogsForTraceAsync(tenantId, traceIdBytes);
        var payload = TelemetryApiMapper.ToLogResponses(logs, includeServiceName: false);
        await OtlpApiResponses.ExecuteAsync(httpContext, OtlpApiResponses.Json(payload, OtlpApiJsonContext.Default.ListTelemetryLogResponse));
    }

    private static async Task WriteTraceSnapshotAsync(
        HttpContext httpContext,
        IServiceScopeFactory scopeFactory,
        Guid? tenantId,
        int limit,
        string? serviceName,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        string? traceIdFilter,
        string? attributeContains,
        string eventName,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
        var traces = await store.GetRecentTracesAsync(tenantId, limit, serviceName, startUtc, endUtc, traceIdFilter, attributeContains);
        await OtlpApiResponses.WriteServerSentEventAsync(httpContext, eventName, traces, OtlpApiJsonContext.Default.ListTraceSummaryDto, ct);
    }

    private static async Task WriteLogSnapshotAsync(HttpContext httpContext, IServiceScopeFactory scopeFactory, Guid? tenantId, int limit, string? serviceName, string eventName, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<EfTelemetryStore>();
        var logs = await store.GetRecentLogsAsync(tenantId, limit, serviceName);
        var payload = TelemetryApiMapper.ToLogResponses(logs);
        await OtlpApiResponses.WriteServerSentEventAsync(httpContext, eventName, payload, OtlpApiJsonContext.Default.ListTelemetryLogResponse, ct);
    }

    private static async Task<bool> EnsureTenantAllowedAsync(HttpContext httpContext, Guid? tenantId, bool devMode, CancellationToken ct)
    {
        if (tenantId is not null || devMode)
            return true;

        await OtlpApiResponses.WriteJsonResponseAsync(
            httpContext.Response,
            new ApiErrorResponse("tenantId query parameter is required"),
            OtlpApiJsonContext.Default.ApiErrorResponse,
            StatusCodes.Status400BadRequest,
            ct);
        return false;
    }

    private static void PrepareSseResponse(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static string? GetStringQuery(HttpContext httpContext, string name) =>
        httpContext.Request.Query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    private static string? GetStringRoute(HttpContext httpContext, string name) =>
        httpContext.Request.RouteValues[name]?.ToString();

    private static Guid? GetGuidQuery(HttpContext httpContext, string name) =>
        Guid.TryParse(GetStringQuery(httpContext, name), out var value) ? value : null;

    private static Guid? GetGuidRoute(HttpContext httpContext, string name) =>
        Guid.TryParse(GetStringRoute(httpContext, name), out var value) ? value : null;

    private static int? GetIntQuery(HttpContext httpContext, string name) =>
        int.TryParse(GetStringQuery(httpContext, name), out var value) ? value : null;

    private static DateTimeOffset? GetDateTimeOffsetQuery(HttpContext httpContext, string name) =>
        DateTimeOffset.TryParse(GetStringQuery(httpContext, name), out var value) ? value : null;

    private static int[]? GetIntArrayQuery(HttpContext httpContext, string name)
    {
        if (!httpContext.Request.Query.TryGetValue(name, out var values) || values.Count == 0)
            return null;

        var parsed = new List<int>(values.Count);
        foreach (var value in values)
        {
            if (int.TryParse(value, out var item))
                parsed.Add(item);
        }

        return parsed.Count == 0 ? null : [.. parsed];
    }

    private static bool TryParseHex(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
