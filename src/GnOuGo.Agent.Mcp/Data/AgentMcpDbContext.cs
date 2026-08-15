using System.Diagnostics.CodeAnalysis;
using GnOuGo.Agent.Mcp.Models;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Mcp.Data;

/// <summary>
/// EF Core DbContext for GnOuGo Agent MCP persisted configuration entities.
/// Database-agnostic: the provider is configured externally via DbContextOptions.
/// </summary>
public sealed class AgentMcpDbContext : DbContext
{
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Agent MCP intentionally retains its EF Core SQLite context in the managed partial-trim profile and verifies it through repository and published-host tests.")]
    public AgentMcpDbContext(DbContextOptions<AgentMcpDbContext> options) : base(options) { }

    public DbSet<UserConfigRecord> UserConfigs => Set<UserConfigRecord>();

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Agent MCP deliberately retains EF Core under partial trimming; its model and persistence paths are covered by published-host smoke tests.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Agent MCP is not a Native AOT target; this annotation documents the EF model boundary for transitive analyzer consumers.")]
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

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

