using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Only non-content indexes and references are persisted here. Payloads belong to KeyVault.</summary>
public sealed class PlanningDbContext(DbContextOptions<PlanningDbContext> options) : DbContext(options)
{
    public DbSet<PlanningSessionIndex> Sessions => Set<PlanningSessionIndex>();
    public DbSet<PlanningCallIndex> Calls => Set<PlanningCallIndex>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var sessions = modelBuilder.Entity<PlanningSessionIndex>();
        sessions.ToTable("PlanningSessions");
        sessions.HasKey(s => new { s.TenantId, s.SessionId });
        sessions.Property(s => s.Revision).IsConcurrencyToken();
        sessions.HasIndex(s => new { s.TenantId, s.UpdatedAtTicks });
        var calls = modelBuilder.Entity<PlanningCallIndex>();
        calls.ToTable("PlanningCalls");
        calls.HasKey(c => new { c.TenantId, c.SessionId, c.RequestHash });
    }
}

public sealed class PlanningSessionIndex
{
    public string TenantId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long Revision { get; set; }
    public string Status { get; set; } = "";
    public long UpdatedAtTicks { get; set; }
    public string PayloadKey { get; set; } = "";
}

public sealed class PlanningCallIndex
{
    public string TenantId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Status { get; set; } = "reserved";
    public string PayloadKey { get; set; } = "";
}
