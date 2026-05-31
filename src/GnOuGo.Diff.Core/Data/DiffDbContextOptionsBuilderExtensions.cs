using GnOuGo.Diff.Core.Data.CompiledModels;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Diff.Core.Data;

public static class DiffDbContextOptionsBuilderExtensions
{
    public static DbContextOptionsBuilder UseDiffCoreSqlite(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString)
        => optionsBuilder
            .UseSqlite(connectionString)
            .UseModel(DiffDbContextModel.Instance);

    public static DbContextOptionsBuilder<TContext> UseDiffCoreSqlite<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString)
        where TContext : DiffDbContext
        => (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder)
            .UseDiffCoreSqlite(connectionString);
}

