using Microsoft.EntityFrameworkCore;
using GnOuGo.Agent.Mcp.Models;

namespace GnOuGo.Agent.Mcp.Data;

public sealed class AgentDbContext : DbContext
{
    public DbSet<AgentDefinition> Agents => Set<AgentDefinition>();

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
    }
}

