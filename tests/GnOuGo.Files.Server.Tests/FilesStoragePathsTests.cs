using GnOuGo.Files.Server.Options;
using GnOuGo.Workspace;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Files.Server.Tests;

public sealed class FilesStoragePathsTests
{
    [Fact]
    public void DatabasePath_WhenDefault_UsesWorkspaceGnOuGoDataDirectory()
    {
        var paths = new FilesStoragePaths(Microsoft.Extensions.Options.Options.Create(new FilesServerOptions()));
        var expected = Path.Combine(
            GnOuGoWorkspace.ResolveDefaultWorkingDirectory(),
            ".GnOuGo",
            "data",
            "gnougo-files.db");

        Assert.Equal(expected, paths.DatabasePath);
    }

    [Fact]
    public void DatabasePath_WhenAbsoluteOverride_PreservesOverride()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"gnougo-files-{Guid.NewGuid():N}.db");
        var paths = new FilesStoragePaths(Microsoft.Extensions.Options.Options.Create(new FilesServerOptions
        {
            DatabasePath = databasePath
        }));

        Assert.Equal(databasePath, paths.DatabasePath);
    }
}
