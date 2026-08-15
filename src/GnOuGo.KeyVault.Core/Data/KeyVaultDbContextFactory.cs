using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GnOuGo.KeyVault.Core.Data;

/// <summary>Creates the KeyVault context for EF Core compiled-model generation.</summary>
public sealed class KeyVaultDbContextFactory : IDesignTimeDbContextFactory<KeyVaultDbContext>
{
    /// <summary>Creates a design-time context without touching a workspace database.</summary>
    /// <param name="args">Arguments supplied by the EF Core tooling.</param>
    /// <returns>A context configured against an in-memory SQLite connection string.</returns>
    public KeyVaultDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<KeyVaultDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);
}
