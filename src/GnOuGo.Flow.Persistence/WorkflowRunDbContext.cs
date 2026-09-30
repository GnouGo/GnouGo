using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Design;

namespace GnOuGo.Flow.Persistence;

/// <summary>Contains only rebuildable metadata. Workflow inputs, outputs, observations and source belong to KeyVault.</summary>
public sealed class WorkflowRunDbContext : DbContext
{
    // EF Core 10.0.12 annotates the base constructor even when a compiled model and precompiled queries are used.
    // This package always supplies its generated model; published persistence smoke tests exercise the boundary.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "EF Core 10.0.12 compiled WorkflowRunDbContext model; no runtime model discovery.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "EF Core 10.0.12 compiled model and publish-precompiled queries; published persistence smoke tested.")]
    public WorkflowRunDbContext(DbContextOptions<WorkflowRunDbContext> options) : base(options) { }
    public DbSet<WorkflowRunIndex> Runs => Set<WorkflowRunIndex>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var runs = modelBuilder.Entity<WorkflowRunIndex>();
        runs.ToTable("WorkflowRuns");
        runs.HasKey(nameof(WorkflowRunIndex.TenantId), nameof(WorkflowRunIndex.RunId));
        runs.HasIndex(nameof(WorkflowRunIndex.TenantId), nameof(WorkflowRunIndex.UpdatedAtTicks));
    }
}

// EF Core 10.0.12 generated materializers test IInjectableService; the persisted entity must remain unsealed.
public class WorkflowRunIndex
{
    public string TenantId { get; set; } = "";
    public string RunId { get; set; } = "";
    public long Revision { get; set; }
    public string Status { get; set; } = "";
    public long UpdatedAtTicks { get; set; }
}

public sealed class WorkflowRunDbContextFactory : IDesignTimeDbContextFactory<WorkflowRunDbContext>
{
    public WorkflowRunDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<WorkflowRunDbContext>().UseSqlite("Data Source=:memory:").Options);
}
