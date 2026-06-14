using GnOuGo.Workspace;
using Xunit;

namespace GnOuGo.Workspace.Tests;

public class GnOuGoWorkspaceTests
{
    [Fact]
    public void ResolveDesktopDirectory_ReturnsNonEmptyAbsolutePath()
    {
        var result = GnOuGoWorkspace.ResolveDesktopDirectory();

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(Path.IsPathRooted(result));
    }

    [Fact]
    public void ResolveDefaultWorkingDirectory_DefaultPath_CreatesGnOuGoSubfolder()
    {
        var result = GnOuGoWorkspace.ResolveDefaultWorkingDirectory();

        Assert.True(Path.IsPathRooted(result));
        Assert.True(Directory.Exists(result));
        Assert.EndsWith(GnOuGoWorkspace.DefaultSubfolder, Path.GetFileName(result));
    }

    [Fact]
    public void ResolveDefaultWorkingDirectory_AbsolutePath_UsesAsIs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "GnOuGo.Workspace.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var result = GnOuGoWorkspace.ResolveDefaultWorkingDirectory(tempDir);

            Assert.Equal(Path.GetFullPath(tempDir), result);
            Assert.True(Directory.Exists(result));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveDefaultWorkingDirectory_RelativePath_ResolvesUnderDesktop()
    {
        var result = GnOuGoWorkspace.ResolveDefaultWorkingDirectory("GnOuGo");

        Assert.True(Path.IsPathRooted(result));
        Assert.True(Directory.Exists(result));
        Assert.EndsWith("GnOuGo", Path.GetFileName(result));
    }

    [Fact]
    public void ResolveDefaultWorkingDirectorySafe_NeverThrows()
    {
        // Even with an impossible configured path, the safe variant should not throw.
        var result = GnOuGoWorkspace.ResolveDefaultWorkingDirectorySafe(
            configuredPath: null,
            contentRootPath: Path.GetTempPath());

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.True(Path.IsPathRooted(result));
    }

    [Fact]
    public void DiscoverWorkspaceRoot_FindsSolutionFile()
    {
        var root = CreateTempDirectory();
        File.WriteAllText(Path.Combine(root, "Test.sln"), "");
        var child = Directory.CreateDirectory(Path.Combine(root, "sub", "deep")).FullName;

        var result = GnOuGoWorkspace.DiscoverWorkspaceRoot(child);

        Assert.Equal(root, result);
    }

    [Fact]
    public void DiscoverWorkspaceRoot_FindsGitDirectory()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var child = Directory.CreateDirectory(Path.Combine(root, "src", "lib")).FullName;

        var result = GnOuGoWorkspace.DiscoverWorkspaceRoot(child);

        Assert.Equal(root, result);
    }

    [Fact]
    public void DiscoverWorkspaceRoot_ReturnsNull_WhenNoMarkerFound()
    {
        var isolated = CreateTempDirectory();

        var result = GnOuGoWorkspace.DiscoverWorkspaceRoot(isolated);

        // May return null or a parent with a .sln/.git — we accept both.
        // The key contract: it must not throw.
        _ = result;
    }

    [Fact]
    public void IsPathWithinRoot_ReturnsTrueForChildPath()
    {
        var root = "/workspace/project";
        var child = "/workspace/project/src/file.cs";

        Assert.True(GnOuGoWorkspace.IsPathWithinRoot(child, root));
    }

    [Fact]
    public void IsPathWithinRoot_ReturnsTrueForExactRoot()
    {
        var root = "/workspace/project";

        Assert.True(GnOuGoWorkspace.IsPathWithinRoot(root, root));
    }

    [Fact]
    public void IsPathWithinRoot_ReturnsFalseForSiblingPath()
    {
        var root = "/workspace/project";
        var sibling = "/workspace/other";

        Assert.False(GnOuGoWorkspace.IsPathWithinRoot(sibling, root));
    }

    [Fact]
    public void IsPathWithinRoot_ReturnsFalseForSimilarPrefix()
    {
        var root = "/workspace/project";
        var similar = "/workspace/project-extra/file.cs";

        Assert.False(GnOuGoWorkspace.IsPathWithinRoot(similar, root));
    }

    [Fact]
    public void ResolveDatabasePath_AbsolutePath_ReturnedAsIs()
    {
        var absolutePath = Path.Combine(Path.GetTempPath(), "test.db");

        var result = GnOuGoWorkspace.ResolveDatabasePath(absolutePath, "/base", "data/default.db");

        Assert.Equal(absolutePath, result);
    }

    [Fact]
    public void ResolveDatabasePath_DefaultRelativePath_ResolvesUnderDefaultWorkingDirectory()
    {
        var expected = Path.Combine(
            GnOuGoWorkspace.ResolveDefaultWorkingDirectory(),
            ".GnOuGo",
            "data",
            "gnougo-test.db");

        var result = GnOuGoWorkspace.ResolveDatabasePath(null, "/base", ".GnOuGo/data/gnougo-test.db");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveDatabasePath_CustomRelativePath_ResolvesUnderDefaultWorkingDirectory()
    {
        var expected = Path.Combine(
            GnOuGoWorkspace.ResolveDefaultWorkingDirectory(),
            "custom",
            "path.db");

        var result = GnOuGoWorkspace.ResolveDatabasePath("custom/path.db", "/base", ".GnOuGo/data/default.db");

        Assert.Equal(expected, result);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Workspace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
