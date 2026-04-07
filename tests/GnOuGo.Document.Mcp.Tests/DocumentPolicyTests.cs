using GnOuGo.Document.Mcp;
using Xunit;

namespace GnOuGo.Document.Mcp.Tests;

public class DocumentPolicyTests
{
    [Fact]
    public void ResolveFilePath_AcceptsPathInsideWorkingRoot()
    {
        var root = CreateTempDir();
        var file = Path.Combine(root, "test.docx");
        File.WriteAllText(file, "");
        var policy = CreatePolicy(root);

        var resolved = policy.ResolveFilePath("test.docx");

        Assert.Equal(file, resolved);
    }

    [Fact]
    public void ResolveFilePath_RejectsPathOutsideAllowedRoots()
    {
        var root = CreateTempDir();
        var outside = CreateTempDir();
        var policy = CreatePolicy(root);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            policy.ResolveFilePath(Path.Combine(outside, "test.docx")));

        Assert.Contains("outside allowed roots", ex.Message);
    }

    [Theory]
    [InlineData(".pdf", true)]
    [InlineData(".docx", true)]
    [InlineData(".xlsx", true)]
    [InlineData(".pptx", true)]
    [InlineData(".txt", true)]
    [InlineData(".exe", false)]
    [InlineData(".dll", false)]
    [InlineData(".zip", false)]
    public void IsExtensionAllowed_ReturnsExpected(string ext, bool expected)
    {
        var root = CreateTempDir();
        var policy = CreatePolicy(root);

        Assert.Equal(expected, policy.IsExtensionAllowed($"file{ext}"));
    }

    [Fact]
    public void DescribePolicy_ContainsDefaultWorkingDirectory()
    {
        var root = CreateTempDir();
        var policy = CreatePolicy(root);

        var info = policy.DescribePolicy();

        Assert.Contains(root, info.AllowedRoots, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(root, info.DefaultWorkingDirectory);
    }

    [Fact]
    public void DiscoverWorkspaceRoot_FindsSolutionFile()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "Test.sln"), "");
        var child = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;

        var result = DocumentPolicy.DiscoverWorkspaceRoot(child);

        Assert.Equal(root, result);
    }

    [Fact]
    public void IsPathWithinRoot_ReturnsTrueForChildPaths()
    {
        var root = "/tmp/testroot";
        Assert.True(DocumentPolicy.IsPathWithinRoot("/tmp/testroot/sub/file.txt", root));
        Assert.False(DocumentPolicy.IsPathWithinRoot("/tmp/other/file.txt", root));
    }

    private static DocumentPolicy CreatePolicy(string root)
        => new(new DocumentServerSettings
        {
            DefaultWorkingDirectory = root,
            AllowedWorkingRoots = [root]
        }, root);

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Document.Mcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

