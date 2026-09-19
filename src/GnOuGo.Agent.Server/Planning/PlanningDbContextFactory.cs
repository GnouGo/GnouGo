using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Design-time model generation never starts the host or opens a user workspace.</summary>
public sealed class PlanningDbContextFactory : IDesignTimeDbContextFactory<PlanningDbContext>
{
    public PlanningDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<PlanningDbContext>().UseSqlite("Data Source=:memory:").Options);
}
