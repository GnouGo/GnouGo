using System.Text.Json;

namespace OtlpTenantCollector.Web;

public sealed record ApiErrorResponse(string Error);

public sealed record ApiMessageResponse(string Message);

public sealed record HealthStatusResponse(string Status);

public sealed record QueueStatusResponse(
    bool CanCount,
    bool CanPeek,
    bool ReaderCompleted,
    string Message);

public sealed record CollectorConfigResponse(
    string DatabasePath,
    int BatchSize,
    int FlushSeconds,
    int ChannelCapacity,
    int RetentionSweepSeconds,
    bool DevModeEnabled);

public sealed record CreateTenantRequest(string Name, int RetentionMinutes);

public sealed record TenantAdminCreatedResponse(Guid Id, string Name, int RetentionMinutes);

public sealed record TenantSummaryResponse(
    Guid Id,
    string Name,
    int RetentionMinutes,
    DateTimeOffset CreatedAt);

public sealed record TelemetryLogResponse(
    Guid? TenantId,
    DateTimeOffset ReceivedUtc,
    string? TraceId,
    string? SpanId,
    int SeverityNumber,
    string? SeverityText,
    string? Body,
    string? ServiceName,
    JsonElement Attributes,
    JsonElement Resource,
    JsonElement Scope);

