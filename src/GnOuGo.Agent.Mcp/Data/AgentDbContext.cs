using Microsoft.EntityFrameworkCore;
using GnOuGo.Agent.Mcp.Models;

namespace GnOuGo.Agent.Mcp.Data;

public sealed class AgentDbContext : DbContext
{
    public DbSet<AgentDefinition> Agents => Set<AgentDefinition>();
    public DbSet<UserConfigRecord> UserConfigs => Set<UserConfigRecord>();

    public AgentDbContext(DbContextOptions<AgentDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<AgentDefinition>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Name).HasMaxLength(256).IsRequired();
            e.Property(a => a.Workflow).IsRequired();
            e.Property(a => a.SchedulesJson).HasColumnName("SchedulesJson").HasDefaultValue("[]");
            e.Property(a => a.TenantId);
            e.Ignore(a => a.CreatedAt);
            e.Ignore(a => a.UpdatedAt);
            e.HasIndex(a => a.TenantId);
            e.HasIndex(a => new { a.Name, a.TenantId }).IsUnique();
        });

        m.Entity<UserConfigRecord>(e =>
        {
            e.HasKey(config => config.Id);
            e.Property(config => config.TenantId);
            e.Property(config => config.TenantScopeKey).HasMaxLength(64).IsRequired();
            e.Property(config => config.DefaultLlmProvider).HasMaxLength(256);
            e.Property(config => config.DefaultLlmModel).HasMaxLength(256);
            e.Property(config => config.DefaultEmbeddingConfig).HasMaxLength(256);
            e.Property(config => config.DefaultAgent).HasMaxLength(256);
            e.Property(config => config.ModelOverridesJson);
            e.Ignore(config => config.UpdatedAt);
            e.HasIndex(config => config.TenantId);
            e.HasIndex(config => config.TenantScopeKey).IsUnique();
        });
    }
}

