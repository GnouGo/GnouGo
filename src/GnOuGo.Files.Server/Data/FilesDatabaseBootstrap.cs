using GnOuGo.Files.Server.Options;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Files.Server.Data;

public static class FilesDatabaseBootstrap
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var paths = scope.ServiceProvider.GetRequiredService<FilesStoragePaths>();
        Directory.CreateDirectory(paths.StorageRootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);

        var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }
}
