using Microsoft.EntityFrameworkCore;
using OtlpTenantCollector.Models;

namespace OtlpTenantCollector.Data;

public class TelemetryDbContext : DbContext
{
    public TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : base(options)
    {
    }

    public DbSet<TenantEntity> Tenants { get; set; } = null!;
    public DbSet<SpanRecordEntity> Spans { get; set; } = null!;
    public DbSet<LogRecordEntity> Logs { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TenantEntity>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.RetentionMinutes).HasColumnName("retention_minutes");
            entity.Property(e => e.CreatedUtc).HasColumnName("created_utc");
        });

        modelBuilder.Entity<SpanRecordEntity>(entity =>
        {
            entity.ToTable("span_records");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired(false);
            entity.Property(e => e.ReceivedUtc).HasColumnName("received_utc");
            entity.Property(e => e.TraceId).HasColumnName("trace_id").IsRequired();
            entity.Property(e => e.SpanId).HasColumnName("span_id").IsRequired();
            entity.Property(e => e.ParentSpanId).HasColumnName("parent_span_id");
            entity.Property(e => e.Name).HasColumnName("name").IsRequired();
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.StartUnixNs).HasColumnName("start_unix_ns");
            entity.Property(e => e.EndUnixNs).HasColumnName("end_unix_ns");
            entity.Property(e => e.StatusCode).HasColumnName("status_code");
            entity.Property(e => e.StatusMessage).HasColumnName("status_message");
            entity.Property(e => e.AttributesJson).HasColumnName("attributes_json");
            entity.Property(e => e.EventsJson).HasColumnName("events_json");
            entity.Property(e => e.ResourceJson).HasColumnName("resource_json");
            entity.Property(e => e.ScopeJson).HasColumnName("scope_json");
            entity.Property(e => e.ServiceName).HasColumnName("service_name");

            // TenantId FK optionnelle — en DevMode le tenant peut être null
            entity.HasOne<TenantEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.ClientNoAction)
                  .HasConstraintName(null);

            entity.HasIndex(e => new { e.TenantId, e.TraceId });
            entity.HasIndex(e => new { e.TenantId, e.ReceivedUtc });
        });

        modelBuilder.Entity<LogRecordEntity>(entity =>
        {
            entity.ToTable("log_records");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").IsRequired();
            entity.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired(false);
            entity.Property(e => e.ReceivedUtc).HasColumnName("received_utc");
            entity.Property(e => e.TraceId).HasColumnName("trace_id");
            entity.Property(e => e.SpanId).HasColumnName("span_id");
            entity.Property(e => e.SeverityNumber).HasColumnName("severity_number");
            entity.Property(e => e.SeverityText).HasColumnName("severity_text");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.AttributesJson).HasColumnName("attributes_json");
            entity.Property(e => e.ResourceJson).HasColumnName("resource_json");
            entity.Property(e => e.ScopeJson).HasColumnName("scope_json");
            entity.Property(e => e.ServiceName).HasColumnName("service_name");

            // TenantId FK optionnelle — en DevMode le tenant peut être null
            entity.HasOne<TenantEntity>()
                  .WithMany()
                  .HasForeignKey(e => e.TenantId)
                  .IsRequired(false)
                  .OnDelete(DeleteBehavior.ClientNoAction)
                  .HasConstraintName(null);

            entity.HasIndex(e => new { e.TenantId, e.ReceivedUtc });
            entity.HasIndex(e => new { e.TenantId, e.TraceId });
        });
    }
}
