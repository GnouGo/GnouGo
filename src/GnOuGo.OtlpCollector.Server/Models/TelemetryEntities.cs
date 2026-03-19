using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OtlpTenantCollector.Models;

// Entité Tenant pour EF Core
[Table("tenants")]
public class TenantEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Required]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("retention_minutes")]
    public int RetentionMinutes { get; set; }

    [Column("created_utc")]
    public DateTimeOffset CreatedUtc { get; set; }

    // Pas de navigation properties : pas de relation FK stricte avec spans/logs (design volontaire)
}

// Entité Span pour EF Core
[Table("span_records")]
public class SpanRecordEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid? TenantId { get; set; }

    [Column("received_utc")]
    public DateTimeOffset ReceivedUtc { get; set; }

    [Required]
    [Column("trace_id")]
    public byte[] TraceId { get; set; } = Array.Empty<byte>();

    [Required]
    [Column("span_id")]
    public byte[] SpanId { get; set; } = Array.Empty<byte>();

    [Column("parent_span_id")]
    public byte[]? ParentSpanId { get; set; }

    [Required]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("kind")]
    public int Kind { get; set; }

    [Column("start_unix_ns")]
    public long StartUnixNs { get; set; }

    [Column("end_unix_ns")]
    public long EndUnixNs { get; set; }

    [Column("status_code")]
    public int StatusCode { get; set; }

    [Column("status_message")]
    public string? StatusMessage { get; set; }

    [Column("attributes_json")]
    public string? AttributesJson { get; set; }

    [Column("events_json")]
    public string? EventsJson { get; set; }

    [Column("resource_json")]
    public string? ResourceJson { get; set; }

    [Column("scope_json")]
    public string? ScopeJson { get; set; }

    [Column("service_name")]
    public string? ServiceName { get; set; }

    // Pas de navigation property Tenant - pas de relation FK stricte pour éviter les contraintes
}

// Entité Log pour EF Core
[Table("log_records")]
public class LogRecordEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("tenant_id")]
    public Guid? TenantId { get; set; }

    [Column("received_utc")]
    public DateTimeOffset ReceivedUtc { get; set; }

    [Column("trace_id")]
    public byte[]? TraceId { get; set; }

    [Column("span_id")]
    public byte[]? SpanId { get; set; }

    [Column("severity_number")]
    public int SeverityNumber { get; set; }

    [Column("severity_text")]
    public string? SeverityText { get; set; }

    [Column("body")]
    public string? Body { get; set; }

    [Column("attributes_json")]
    public string? AttributesJson { get; set; }

    [Column("resource_json")]
    public string? ResourceJson { get; set; }

    [Column("scope_json")]
    public string? ScopeJson { get; set; }

    [Column("service_name")]
    public string? ServiceName { get; set; }

    // Pas de navigation property Tenant - pas de relation FK stricte pour éviter les contraintes
}

