namespace GnOuGo.Agent.Server.Telemetry;

public sealed record TraceDebugAvailability(bool Enabled, string Message);

public sealed record TraceDebugSnapshot(
    TraceDebugAvailability Availability,
    string? CorrelationId,
    string? TraceId,
    TraceGroupDto? Trace,
    List<TraceLogDto> Logs,
    bool Pending,
    string Message);

public sealed record TraceLogDto(
    DateTimeOffset ReceivedUtc,
    string? TraceId,
    string? SpanId,
    int SeverityNumber,
    string? SeverityText,
    string? Body,
    string? ServiceName,
    Dictionary<string, object?> Attributes,
    Dictionary<string, object?> Resource,
    Dictionary<string, object?> Scope);

public sealed record TraceSummaryDto(
    string TraceId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int SpanCount,
    string? RootSpanName,
    string? ServicesCsv,
    string? ServiceName);

public sealed record TraceEventDto(
    string Name,
    DateTimeOffset TimeUtc,
    Dictionary<string, object?> Attributes);

public sealed record TraceSpanDto(
    string SpanId,
    string? ParentSpanId,
    string Name,
    int Kind,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DurationMs,
    int StatusCode,
    string? StatusMessage,
    Dictionary<string, object?> Attributes,
    List<TraceEventDto> Events,
    Dictionary<string, object?> Resource,
    Dictionary<string, object?> Scope);

public sealed record TraceGroupDto(
    string TraceId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    List<TraceSpanDto> Spans);

