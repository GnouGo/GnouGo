using GnOuGo.KeyVault.Core.Data.CompiledModels;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.KeyVault.Core.Data;

/// <summary>Configures the EF Core SQLite persistence owned by KeyVault.</summary>
public static class KeyVaultDbContextOptionsBuilderExtensions
{
    /// <summary>Uses SQLite with the trimming-safe compiled KeyVault model.</summary>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <returns>The supplied builder.</returns>
    public static DbContextOptionsBuilder UseKeyVaultSqlite(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString)
        => optionsBuilder
            .UseSqlite(connectionString)
            .UseModel(KeyVaultDbContextModel.Instance);
}
