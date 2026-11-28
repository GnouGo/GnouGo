using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.Files.Server.Options;

public sealed class FilesStoragePaths
{
    public const string DefaultDatabasePath = ".GnOuGo/data/gnougo-files.db";

    public FilesStoragePaths(IOptions<FilesServerOptions> options)
    {
        var value = options.Value;
        StorageRootPath = GnOuGo.Files.Core.DurableFileStore.ResolveRoot(value.StorageRootPath, AppContext.BaseDirectory);

        DatabasePath = GnOuGoWorkspace.ResolveDatabasePath(
            value.DatabasePath,
            AppContext.BaseDirectory,
            DefaultDatabasePath);
    }

    public string StorageRootPath { get; }

    public string DatabasePath { get; }

}
