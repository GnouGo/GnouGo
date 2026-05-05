using System.Text.Json;

namespace OtlpTenantCollector.Models;

public interface ITelemetryRow
{
    Guid? TenantId { get; }
    DateTimeOffset ReceivedUtc { get; }
}

public sealed record SpanRow(
    Guid? TenantId,
    DateTimeOffset ReceivedUtc,
    byte[] TraceId,         // 16 bytes
    byte[] SpanId,          // 8 bytes
    byte[]? ParentSpanId,   // 8 bytes or null
    string Name,
    int Kind,
    long StartUnixNs,
    long EndUnixNs,
    int StatusCode,
    string? StatusMessage,
    string? AttributesJson,
    string? EventsJson,
    string? ResourceJson,
    string? ScopeJson,
    string? ServiceName     // Nom du service OpenTelemetry
) : ITelemetryRow;

public sealed record LogRow(
    Guid? TenantId,
    DateTimeOffset ReceivedUtc,
    byte[]? TraceId,       // 16 bytes or null
    byte[]? SpanId,        // 8 bytes or null
    int SeverityNumber,
    string? SeverityText,
    string? Body,
    string? AttributesJson,
    string? ResourceJson,
    string? ScopeJson,
    string? ServiceName     // Nom du service OpenTelemetry
) : ITelemetryRow;

// API DTOs
public sealed record TenantCreatedResponse(string TenantId, string ApiKey, int RetentionMinutes);

public sealed record TraceSummaryDto(
    string TraceId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int SpanCount,
    string? RootSpanName,
    string? ServicesCsv,
    string? ServiceName     // Service principal de la trace
);

public sealed record SpanEventDto(
    string Name,
    DateTimeOffset TimeUtc,
    JsonElement Attributes
);

public sealed record SpanDto(
    string SpanId,
    string? ParentSpanId,
    string Name,
    int Kind,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DurationMs,
    int StatusCode,
    string? StatusMessage,
    JsonElement Attributes,
    List<SpanEventDto> Events,
    JsonElement Resource,
    JsonElement Scope
);

public sealed record TraceDto(string TraceId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, List<SpanDto> Spans);
