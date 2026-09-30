using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Only non-content indexes and references are persisted here. Payloads belong to KeyVault.</summary>
public sealed class PlanningDbContext : DbContext
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "EF Core 10.0.12 compiled planning model; host registration and published encrypted persistence smoke use that model explicitly.")]
    public PlanningDbContext(DbContextOptions<PlanningDbContext> options) : base(options) { }
    public DbSet<PlanningSessionIndex> Sessions => Set<PlanningSessionIndex>();
    public DbSet<PlanningCallIndex> Calls => Set<PlanningCallIndex>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var sessions = modelBuilder.Entity<PlanningSessionIndex>();
        sessions.ToTable("PlanningSessions");
        sessions.HasKey(nameof(PlanningSessionIndex.TenantId), nameof(PlanningSessionIndex.SessionId));
        sessions.Property(s => s.Revision).IsConcurrencyToken();
        sessions.HasIndex(nameof(PlanningSessionIndex.TenantId), nameof(PlanningSessionIndex.UpdatedAtTicks));
        var calls = modelBuilder.Entity<PlanningCallIndex>();
        calls.ToTable("PlanningCalls");
        calls.HasKey(nameof(PlanningCallIndex.TenantId), nameof(PlanningCallIndex.SessionId), nameof(PlanningCallIndex.RequestHash));
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
