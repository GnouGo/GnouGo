using Microsoft.Extensions.Options;

namespace GnOuGo.Files.Server.Options;

public sealed class FilesStoragePaths
{
    public FilesStoragePaths(IOptions<FilesServerOptions> options)
    {
        var value = options.Value;
        StorageRootPath = ResolvePath(value.StorageRootPath, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "GnOuGo",
            "data"));

        DatabasePath = ResolvePath(value.DatabasePath, Path.Combine(StorageRootPath, "gnougo-files.db"));
    }

    public string StorageRootPath { get; }

    public string DatabasePath { get; }

    private static string ResolvePath(string? configuredPath, string defaultPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? defaultPath : configuredPath;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }
}

