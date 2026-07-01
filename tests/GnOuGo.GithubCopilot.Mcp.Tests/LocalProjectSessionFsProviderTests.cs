using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

public sealed class LocalProjectSessionFsProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-sessionfs-tests-" + Guid.NewGuid().ToString("N"));

    public LocalProjectSessionFsProviderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Existing.cs"), "class Existing { }\n");
    }

    [Fact]
    public async Task WriteFileForTestAsync_WritesAllowedFileAndTracksModification()
    {
        var provider = CreateProvider(allowWrites: true);

        await provider.WriteFileForTestAsync("src/NewFile.cs", "class NewFile { }\n");

        Assert.Equal("class NewFile { }\n", File.ReadAllText(Path.Combine(_root, "src", "NewFile.cs")));
        Assert.Contains("src" + Path.DirectorySeparatorChar + "NewFile.cs", provider.ModifiedFiles);
    }

    [Fact]
    public async Task ReadFileForTestAsync_ReadsAllowedFile()
    {
        var provider = CreateProvider(allowWrites: false);

        var content = await provider.ReadFileForTestAsync("src/Existing.cs");

        Assert.Contains("class Existing", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteFileForTestAsync_RejectsWhenWritesAreDisabled()
    {
        var provider = CreateProvider(allowWrites: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.WriteFileForTestAsync("src/NewFile.cs", "class NewFile { }\n"));

        Assert.Contains("disabled by policy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteFileForTestAsync_RejectsParentTraversal()
    {
        var provider = CreateProvider(allowWrites: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.WriteFileForTestAsync("../escape.cs", "class Escape { }\n"));

        Assert.Contains("parent traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private LocalProjectSessionFsProvider CreateProvider(bool allowWrites)
    {
        var settings = new CodeServerSettings
        {
            DefaultWorkingDirectory = _root,
            AllowedWorkingRoots = [_root],
            AllowedExtensions = [".cs", ".md"],
            MaxFileSizeBytes = 1024 * 1024,
            MaxPromptCharacters = 24_000,
            AllowWrites = allowWrites
        };
        return new LocalProjectSessionFsProvider(
            new CodePolicy(settings, _root),
            settings,
            ".",
            NullLogger<LocalProjectSessionFsProvider>.Instance);
    }
}
