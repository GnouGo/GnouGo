using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Services;

/// <summary>
/// Helpers pour convertir entre les records (DTOs) et les entités EF Core
/// </summary>
public static class TelemetryMapper
{
    /// <summary>
    /// Convertit un SpanRow (record immutable) vers une SpanRecordEntity (entité EF Core)
    /// </summary>
    public static SpanRecordEntity ToEntity(this SpanRow row) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = row.TenantId,
        ReceivedUtc = row.ReceivedUtc,
        TraceId = row.TraceId,
        SpanId = row.SpanId,
        ParentSpanId = row.ParentSpanId,
        Name = row.Name,
        Kind = row.Kind,
        StartUnixNs = row.StartUnixNs,
        EndUnixNs = row.EndUnixNs,
        StatusCode = row.StatusCode,
        StatusMessage = row.StatusMessage,
        AttributesJson = row.AttributesJson,
        EventsJson = row.EventsJson,
        ResourceJson = row.ResourceJson,
        ScopeJson = row.ScopeJson,
        ServiceName = row.ServiceName
    };

    /// <summary>
    /// Convertit un LogRow (record immutable) vers une LogRecordEntity (entité EF Core)
    /// </summary>
    public static LogRecordEntity ToEntity(this LogRow row) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = row.TenantId,
        ReceivedUtc = row.ReceivedUtc,
        TraceId = row.TraceId,
        SpanId = row.SpanId,
        SeverityNumber = row.SeverityNumber,
        SeverityText = row.SeverityText,
        Body = row.Body,
        AttributesJson = row.AttributesJson,
        ResourceJson = row.ResourceJson,
        ScopeJson = row.ScopeJson,
        ServiceName = row.ServiceName
    };


    /// <summary>
    /// Convertit une SpanRecordEntity vers un SpanRow (si nécessaire)
    /// </summary>
    public static SpanRow ToRecord(this SpanRecordEntity entity) => new(
        TenantId: entity.TenantId,
        ReceivedUtc: entity.ReceivedUtc,
        TraceId: entity.TraceId,
        SpanId: entity.SpanId,
        ParentSpanId: entity.ParentSpanId,
        Name: entity.Name,
        Kind: entity.Kind,
        StartUnixNs: entity.StartUnixNs,
        EndUnixNs: entity.EndUnixNs,
        StatusCode: entity.StatusCode,
        StatusMessage: entity.StatusMessage,
        AttributesJson: entity.AttributesJson,
        EventsJson: entity.EventsJson,
        ResourceJson: entity.ResourceJson,
        ScopeJson: entity.ScopeJson,
        ServiceName: entity.ServiceName
    );

    /// <summary>
    /// Convertit une TenantEntity vers un record Tenant (pour compatibilité avec l'API existante)
    /// </summary>
    public static Tenant ToRecord(this TenantEntity entity) => new(
        Id: entity.Id,
        Name: entity.Name,
        RetentionMinutes: entity.RetentionMinutes,
        CreatedUtc: entity.CreatedUtc
    );

    /// <summary>
    /// Convertit une SpanRecordEntity vers un SpanRow (si nécessaire)
    /// </summary>
    public static LogRow ToRecord(this LogRecordEntity entity) => new(
        TenantId: entity.TenantId,
        ReceivedUtc: entity.ReceivedUtc,
        TraceId: entity.TraceId,
        SpanId: entity.SpanId,
        SeverityNumber: entity.SeverityNumber,
        SeverityText: entity.SeverityText,
        Body: entity.Body,
        AttributesJson: entity.AttributesJson,
        ResourceJson: entity.ResourceJson,
        ScopeJson: entity.ScopeJson,
        ServiceName: entity.ServiceName
    );

    /// <summary>
    /// Convertit une collection de SpanRow vers des entités
    /// </summary>
    public static List<SpanRecordEntity> ToEntities(this IEnumerable<SpanRow> rows)
        => rows.Select(r => r.ToEntity()).ToList();

    /// <summary>
    /// Convertit une collection de LogRow vers des entités
    /// </summary>
    public static List<LogRecordEntity> ToEntities(this IEnumerable<LogRow> rows)
        => rows.Select(r => r.ToEntity()).ToList();
}
