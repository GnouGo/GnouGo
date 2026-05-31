using System.Diagnostics.CodeAnalysis;
using GnOuGo.Agent.Mcp.Models;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Mcp.Data;

/// <summary>
/// EF Core DbContext for GnOuGo Agent MCP entities (Agents, UserConfigs).
/// Database-agnostic: the provider is configured externally via DbContextOptions.
/// </summary>
public sealed class AgentMcpDbContext : DbContext
{
    [UnconditionalSuppressMessage("AOT", "IL2026",
        Justification = "Agent MCP uses EF Core with SQLite. TrimMode=partial preserves EF Core assemblies.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Agent MCP uses EF Core with SQLite. TrimMode=partial preserves EF Core assemblies.")]
    public AgentMcpDbContext(DbContextOptions<AgentMcpDbContext> options) : base(options) { }

    public DbSet<AgentDefinition> Agents => Set<AgentDefinition>();
    public DbSet<UserConfigRecord> UserConfigs => Set<UserConfigRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentDefinition>(entity =>
        {
            entity.ToTable("Agents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("Id").ValueGeneratedNever();
            entity.Property(e => e.TenantId).HasColumnName("TenantId");
            entity.Property(e => e.Name).HasColumnName("Name").IsRequired();
            entity.Property(e => e.Workflow).HasColumnName("Workflow").IsRequired();
            entity.Property(e => e.OriginalPrompt).HasColumnName("OriginalPrompt");
            entity.Property(e => e.ScheduleDescription).HasColumnName("ScheduleDescription");
            entity.Property(e => e.SchedulesJson).HasColumnName("SchedulesJson").IsRequired().HasDefaultValue("[]");
            entity.Property(e => e.CreatedAtTicks).HasColumnName("CreatedAtTicks");
            entity.Property(e => e.UpdatedAtTicks).HasColumnName("UpdatedAtTicks");

            entity.Ignore(e => e.CreatedAt);
            entity.Ignore(e => e.UpdatedAt);

            entity.HasIndex(e => e.TenantId).HasDatabaseName("IX_Agents_TenantId");
            entity.HasIndex(e => new { e.Name, e.TenantId }).IsUnique().HasDatabaseName("IX_Agents_Name_TenantId");
        });

        modelBuilder.Entity<UserConfigRecord>(entity =>
        {
            entity.ToTable("UserConfigs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("Id").ValueGeneratedNever();
            entity.Property(e => e.TenantId).HasColumnName("TenantId");
            entity.Property(e => e.TenantScopeKey).HasColumnName("TenantScopeKey").IsRequired();
            entity.Property(e => e.DefaultLlmProvider).HasColumnName("DefaultLlmProvider");
            entity.Property(e => e.DefaultLlmModel).HasColumnName("DefaultLlmModel");
            entity.Property(e => e.DefaultEmbeddingConfig).HasColumnName("DefaultEmbeddingConfig");
            entity.Property(e => e.DefaultAgent).HasColumnName("DefaultAgent");
            entity.Property(e => e.ModelOverridesJson).HasColumnName("ModelOverridesJson");
            entity.Property(e => e.UpdatedAtTicks).HasColumnName("UpdatedAtTicks");

            entity.Ignore(e => e.UpdatedAt);

            entity.HasIndex(e => e.TenantScopeKey).IsUnique().HasDatabaseName("IX_UserConfigs_TenantScopeKey");
            entity.HasIndex(e => e.TenantId).HasDatabaseName("IX_UserConfigs_TenantId");
        });
    }
}

