namespace OtlpTenantCollector.Models;

public class TenantEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int RetentionMinutes { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public class SpanRecordEntity
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public byte[] TraceId { get; set; } = Array.Empty<byte>();
    public byte[] SpanId { get; set; } = Array.Empty<byte>();
    public byte[]? ParentSpanId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Kind { get; set; }
    public long StartUnixNs { get; set; }
    public long EndUnixNs { get; set; }
    public int StatusCode { get; set; }
    public string? StatusMessage { get; set; }
    public string? AttributesJson { get; set; }
    public string? EventsJson { get; set; }
    public string? ResourceJson { get; set; }
    public string? ScopeJson { get; set; }
    public string? ServiceName { get; set; }
}

public class LogRecordEntity
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public DateTimeOffset ReceivedUtc { get; set; }
    public byte[]? TraceId { get; set; }
    public byte[]? SpanId { get; set; }
    public int SeverityNumber { get; set; }
    public string? SeverityText { get; set; }
    public string? Body { get; set; }
    public string? AttributesJson { get; set; }
    public string? ResourceJson { get; set; }
    public string? ScopeJson { get; set; }
    public string? ServiceName { get; set; }
}

