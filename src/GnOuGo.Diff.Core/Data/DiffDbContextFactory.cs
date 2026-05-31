using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GnOuGo.Diff.Core.Data;

public sealed class DiffDbContextFactory : IDesignTimeDbContextFactory<DiffDbContext>
{
    public DiffDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DiffDbContext>()
            .UseDiffCoreSqlite("Data Source=gnougo-diff-design-time.db")
            .Options;

        return new DiffDbContext(options);
    }
}


