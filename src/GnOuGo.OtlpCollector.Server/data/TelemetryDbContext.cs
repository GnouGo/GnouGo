using Microsoft.EntityFrameworkCore;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Data;

public sealed class TelemetryDbContext : DbContext
{
    public TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : base(options) { }

    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<SpanRecordEntity> SpanRecords => Set<SpanRecordEntity>();
    public DbSet<LogRecordEntity> LogRecords => Set<LogRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantEntity>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id");
            e.Property(t => t.Name).HasColumnName("name").IsRequired();
            e.Property(t => t.RetentionMinutes).HasColumnName("retention_minutes");
            e.Property(t => t.CreatedUtc).HasColumnName("created_utc");
        });

        modelBuilder.Entity<SpanRecordEntity>(e =>
        {
            e.ToTable("span_records");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id");
            e.Property(s => s.TenantId).HasColumnName("tenant_id");
            e.Property(s => s.ReceivedUtc).HasColumnName("received_utc");
            e.Property(s => s.TraceId).HasColumnName("trace_id").IsRequired();
            e.Property(s => s.SpanId).HasColumnName("span_id").IsRequired();
            e.Property(s => s.ParentSpanId).HasColumnName("parent_span_id");
            e.Property(s => s.Name).HasColumnName("name").IsRequired();
            e.Property(s => s.Kind).HasColumnName("kind");
            e.Property(s => s.StartUnixNs).HasColumnName("start_unix_ns");
            e.Property(s => s.EndUnixNs).HasColumnName("end_unix_ns");
            e.Property(s => s.StatusCode).HasColumnName("status_code");
            e.Property(s => s.StatusMessage).HasColumnName("status_message");
            e.Property(s => s.AttributesJson).HasColumnName("attributes_json");
            e.Property(s => s.EventsJson).HasColumnName("events_json");
            e.Property(s => s.ResourceJson).HasColumnName("resource_json");
            e.Property(s => s.ScopeJson).HasColumnName("scope_json");
            e.Property(s => s.ServiceName).HasColumnName("service_name");

            e.HasIndex(s => new { s.TenantId, s.TraceId });
            e.HasIndex(s => new { s.TenantId, s.ReceivedUtc });
        });

        modelBuilder.Entity<LogRecordEntity>(e =>
        {
            e.ToTable("log_records");
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasColumnName("id");
            e.Property(l => l.TenantId).HasColumnName("tenant_id");
            e.Property(l => l.ReceivedUtc).HasColumnName("received_utc");
            e.Property(l => l.TraceId).HasColumnName("trace_id");
            e.Property(l => l.SpanId).HasColumnName("span_id");
            e.Property(l => l.SeverityNumber).HasColumnName("severity_number");
            e.Property(l => l.SeverityText).HasColumnName("severity_text");
            e.Property(l => l.Body).HasColumnName("body");
            e.Property(l => l.AttributesJson).HasColumnName("attributes_json");
            e.Property(l => l.ResourceJson).HasColumnName("resource_json");
            e.Property(l => l.ScopeJson).HasColumnName("scope_json");
            e.Property(l => l.ServiceName).HasColumnName("service_name");

            e.HasIndex(l => new { l.TenantId, l.ReceivedUtc });
            e.HasIndex(l => new { l.TenantId, l.TraceId });
        });
    }
}
