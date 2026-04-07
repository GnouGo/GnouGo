using GnOuGo.Document.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GnOuGo.Document.Mcp.Tests;

public class DocumentOperationHostTests
{
    [Fact]
    public void Read_NonExistentFile_ReturnsFileNotFound()
    {
        var host = CreateHost();

        var result = host.Read("nonexistent.txt", null);

        Assert.False(result.Success);
        Assert.Equal("FILE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Read_DisallowedExtension_ReturnsExtensionNotAllowed()
    {
        var root = CreateTempDir();
        var file = Path.Combine(root, "test.exe");
        File.WriteAllText(file, "data");
        var host = CreateHost(root);

        var result = host.Read("test.exe", null);

        Assert.False(result.Success);
        Assert.Equal("EXTENSION_NOT_ALLOWED", result.ErrorCode);
    }

    [Fact]
    public void Read_PlainTextFile_ReturnsContent()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "hello.txt"), "Hello, World!");
        var host = CreateHost(root);

        var result = host.Read("hello.txt", null);

        Assert.True(result.Success);
        Assert.Single(result.Sections);
        Assert.Contains("Hello, World!", result.Sections[0].Content);
    }

    [Fact]
    public void Read_MarkdownFile_ReturnsContent()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "readme.md"), "# Title\n\nSome content.");
        var host = CreateHost(root);

        var result = host.Read("readme.md", "plain");

        Assert.True(result.Success);
        Assert.Single(result.Sections);
        Assert.Contains("# Title", result.Sections[0].Content);
    }

    [Fact]
    public void Read_CsvFile_ReturnsContent()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "data.csv"), "name,age\nAlice,30\nBob,25");
        var host = CreateHost(root);

        var result = host.Read("data.csv", null);

        Assert.True(result.Success);
        Assert.Contains("Alice", result.Sections[0].Content);
    }

    [Fact]
    public void Write_PlainTextFile_CreatesFileAndReturnsSuccess()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var result = host.Write("output.txt", "Hello from MCP!", null);

        Assert.True(result.Success);
        Assert.NotNull(result.FilePath);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal("Hello from MCP!", File.ReadAllText(result.FilePath));
    }

    [Fact]
    public void Write_DisallowedExtension_ReturnsError()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var result = host.Write("output.exe", "data", null);

        Assert.False(result.Success);
        Assert.Equal("EXTENSION_NOT_ALLOWED", result.ErrorCode);
    }

    [Fact]
    public void Write_DocxFile_CreatesValidDocx()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("test.docx", "Line 1\nLine 2\nLine 3", null);

        Assert.True(writeResult.Success);
        Assert.True(File.Exists(writeResult.FilePath));

        // Read it back
        var readResult = host.Read("test.docx", "plain");
        Assert.True(readResult.Success);
        Assert.Contains("Line 1", readResult.Sections[0].Content);
        Assert.Contains("Line 2", readResult.Sections[0].Content);
    }

    [Fact]
    public void Write_XlsxFile_CreatesValidXlsx()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var writeResult = host.Write("test.xlsx", "Name\tAge\nAlice\t30\nBob\t25", null);

        Assert.True(writeResult.Success);
        Assert.True(File.Exists(writeResult.FilePath));

        // Read it back
        var readResult = host.Read("test.xlsx", "markdown");
        Assert.True(readResult.Success);
        Assert.NotEmpty(readResult.Sections);
        Assert.Contains("Alice", readResult.Sections[0].Content);
    }

    [Fact]
    public void Write_CreatesSubdirectories()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var result = host.Write("sub/dir/file.txt", "Nested!", null);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(root, "sub", "dir", "file.txt")));
    }

    [Fact]
    public void ListFiles_ReturnsAllowedFilesOnly()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "doc.txt"), "text");
        File.WriteAllText(Path.Combine(root, "data.csv"), "a,b");
        File.WriteAllText(Path.Combine(root, "binary.exe"), "nope");
        var host = CreateHost(root);

        var result = host.ListFiles(null, false);

        Assert.True(result.Success);
        Assert.Equal(2, result.Files.Count);
        Assert.All(result.Files, f => Assert.True(f.Extension is ".txt" or ".csv"));
    }

    [Fact]
    public void ListFiles_Recursive_FindsNestedFiles()
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "top.md"), "# Top");
        File.WriteAllText(Path.Combine(root, "sub", "nested.json"), "{}");
        var host = CreateHost(root);

        var result = host.ListFiles(null, true);

        Assert.True(result.Success);
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public void ListFiles_NonExistentDirectory_ReturnsError()
    {
        var root = CreateTempDir();
        var host = CreateHost(root);

        var result = host.ListFiles("nonexistent", false);

        Assert.False(result.Success);
        Assert.Equal("DIRECTORY_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public void Read_FileTooLarge_ReturnsError()
    {
        var root = CreateTempDir();
        var file = Path.Combine(root, "big.txt");
        File.WriteAllText(file, new string('x', 200));
        var host = CreateHost(root, maxFileSize: 100);

        var result = host.Read("big.txt", null);

        Assert.False(result.Success);
        Assert.Equal("FILE_TOO_LARGE", result.ErrorCode);
    }

    [Fact]
    public void GetPolicy_ReturnsValidInfo()
    {
        var host = CreateHost();
        var info = host.GetPolicy();

        Assert.NotNull(info.DefaultWorkingDirectory);
        Assert.NotEmpty(info.AllowedExtensions);
        Assert.True(info.MaxFileSizeBytes > 0);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static DocumentOperationHost CreateHost(string? root = null, long maxFileSize = 50 * 1024 * 1024)
    {
        root ??= CreateTempDir();
        var settings = new DocumentServerSettings
        {
            DefaultWorkingDirectory = root,
            AllowedWorkingRoots = [root],
            MaxFileSizeBytes = maxFileSize
        };
        var policy = new DocumentPolicy(settings, root);
        return new DocumentOperationHost(policy, NullLogger<DocumentOperationHost>.Instance);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "GnOuGo.Document.Mcp.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

