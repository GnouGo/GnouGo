using System.Text.Json;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Web;

public static class TelemetryApiMapper
{
    public static TenantSummaryResponse ToTenantSummaryResponse(TenantEntity tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return new TenantSummaryResponse(tenant.Id, tenant.Name, tenant.RetentionMinutes, tenant.CreatedUtc);
    }

    public static List<TenantSummaryResponse> ToTenantSummaryResponses(IEnumerable<TenantEntity> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        return [.. tenants.Select(ToTenantSummaryResponse)];
    }

    public static TelemetryLogResponse ToLogResponse(LogRecordEntity log, bool includeServiceName = true)
    {
        ArgumentNullException.ThrowIfNull(log);

        return new TelemetryLogResponse(
            TenantId: log.TenantId,
            ReceivedUtc: log.ReceivedUtc,
            TraceId: log.TraceId is null ? null : Convert.ToHexString(log.TraceId),
            SpanId: log.SpanId is null ? null : Convert.ToHexString(log.SpanId),
            SeverityNumber: log.SeverityNumber,
            SeverityText: log.SeverityText,
            Body: log.Body,
            ServiceName: includeServiceName ? log.ServiceName : null,
            Attributes: ParseJsonObject(log.AttributesJson),
            Resource: ParseJsonObject(log.ResourceJson),
            Scope: ParseJsonObject(log.ScopeJson));
    }

    public static List<TelemetryLogResponse> ToLogResponses(IEnumerable<LogRecordEntity> logs, bool includeServiceName = true)
    {
        ArgumentNullException.ThrowIfNull(logs);
        return [.. logs.Select(log => ToLogResponse(log, includeServiceName))];
    }

    public static JsonElement ParseJsonObject(string? json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return document.RootElement.Clone();
    }
}

