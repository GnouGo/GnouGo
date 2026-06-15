using GnOuGo.Workspace;
using Microsoft.Extensions.Options;

namespace GnOuGo.Files.Server.Options;

public sealed class FilesStoragePaths
{
    public const string DefaultDatabasePath = ".GnOuGo/data/gnougo-files.db";

    public FilesStoragePaths(IOptions<FilesServerOptions> options)
    {
        var value = options.Value;
        var workspaceRoot = GnOuGoWorkspace.ResolveDefaultWorkingDirectorySafe(contentRootPath: AppContext.BaseDirectory);
        StorageRootPath = ResolvePath(value.StorageRootPath, Path.Combine(workspaceRoot, GnOuGoWorkspace.WorkspaceDataSubfolder, "Files"));

        DatabasePath = GnOuGoWorkspace.ResolveDatabasePath(
            value.DatabasePath,
            AppContext.BaseDirectory,
            DefaultDatabasePath);
    }

    public string StorageRootPath { get; }

    public string DatabasePath { get; }

    private static string ResolvePath(string? configuredPath, string defaultPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? defaultPath : configuredPath;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }
}
