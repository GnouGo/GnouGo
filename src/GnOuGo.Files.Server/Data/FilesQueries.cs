using GnOuGo.Files.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Files.Server.Data;

/// <summary>
/// Precompiled EF Core queries for optimal performance and AOT friendliness.
/// </summary>
internal static class FilesQueries
{
    public static readonly Func<FilesDbContext, string, Task<FileRecord?>> GetFileById =
        EF.CompileAsyncQuery(
            (FilesDbContext db, string id) =>
                db.Files.AsNoTracking().FirstOrDefault(f => f.Id == id));
}

