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

        await provider.WriteFileForTestAsync("src/NewFile.cs", "class NewFile { }\n", TestContext.Current.CancellationToken);

        Assert.Equal("class NewFile { }\n", File.ReadAllText(Path.Combine(_root, "src", "NewFile.cs")));
        Assert.Contains("src" + Path.DirectorySeparatorChar + "NewFile.cs", provider.ModifiedFiles);
    }

    [Fact]
    public async Task ReadFileForTestAsync_ReadsAllowedFile()
    {
        var provider = CreateProvider(allowWrites: false);

        var content = await provider.ReadFileForTestAsync("src/Existing.cs", TestContext.Current.CancellationToken);

        Assert.Contains("class Existing", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Constructor_AllowsAbsoluteProjectRootInsideWorkspace()
    {
        var provider = CreateProvider(_root, allowWrites: false);

        var content = await provider.ReadFileForTestAsync("src/Existing.cs", TestContext.Current.CancellationToken);

        Assert.Contains("class Existing", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteFileForTestAsync_RejectsWhenWritesAreDisabled()
    {
        var provider = CreateProvider(allowWrites: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.WriteFileForTestAsync("src/NewFile.cs", "class NewFile { }\n", TestContext.Current.CancellationToken));

        Assert.Contains("disabled by policy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteFileForTestAsync_RejectsParentTraversal()
    {
        var provider = CreateProvider(allowWrites: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.WriteFileForTestAsync("../escape.cs", "class Escape { }\n", TestContext.Current.CancellationToken));

        Assert.Contains("parent traversal", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadFileForTestAsync_RejectsReservedGnOuGoPath()
    {
        var internalDirectory = Directory.CreateDirectory(Path.Combine(_root, ".GnOuGo"));
        File.WriteAllText(Path.Combine(internalDirectory.FullName, "Internal.cs"), "internal");
        var provider = CreateProvider(allowWrites: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ReadFileForTestAsync(".GnOuGo/Internal.cs", TestContext.Current.CancellationToken));

        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.cs")]
    [InlineData(".GnOuGo/internal.cs")]
    [InlineData(".git/config.cs")]
    [InlineData("src/*.cs")]
    [InlineData("src/file.exe")]
    public async Task AllMutations_RejectRestrictedPaths(string path)
    {
        await using var provider = CreateProvider(true);
        await Assert.ThrowsAnyAsync<Exception>(() => provider.WriteFileAsync(path, "x", null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.AppendFileAsync(path, "x", null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.RemoveAsync(path, true, true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.RenameAsync("src/Existing.cs", path, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(_root, "src", "Existing.cs")));
    }

    [Fact]
    public async Task RecursiveChanges_ValidateEveryDescendantBeforeMutating()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "mixed")).FullName;
        File.WriteAllText(Path.Combine(folder, "allowed.cs"), "keep");
        File.WriteAllText(Path.Combine(folder, "private.bin"), "keep");
        await using var provider = CreateProvider(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RemoveAsync("mixed", true, false, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RenameAsync("mixed", "moved", TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(folder, "allowed.cs")));
        Assert.False(Directory.Exists(Path.Combine(_root, "moved")));
        Assert.Empty(provider.ModifiedFiles);
    }

    [Fact]
    public async Task SymlinksAndRootMutation_AreRejected()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "Keep.cs"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
        await using var provider = CreateProvider(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.ReadFileAsync("link/Keep.cs", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.WriteFileAsync("link/New.cs", "new", null, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.RemoveAsync("link", true, false, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.RemoveAsync(".", true, false, TestContext.Current.CancellationToken));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "Keep.cs")));
        Assert.DoesNotContain(await provider.ReadDirectoryAsync(".", TestContext.Current.CancellationToken), e => e.Name == "link");
    }

    [Fact]
    public async Task SizePolicy_AppliesToMetadataAndRecursiveMutations()
    {
        var path = Path.Combine(_root, "src", "Oversized.cs");
        await File.WriteAllTextAsync(path, new string('x', 1024 * 1024 + 1), TestContext.Current.CancellationToken);
        await using var provider = CreateProvider(true);
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadFileAsync("src/Oversized.cs", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StatAsync("src/Oversized.cs", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExistsAsync("src/Oversized.cs", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RemoveAsync("src", true, false, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RenameAsync("src", "moved", ct));
        Assert.DoesNotContain(await provider.ReadDirectoryAsync("src", ct), e => e.Name == "Oversized.cs");
        Assert.True(File.Exists(path));
        Assert.Empty(provider.ModifiedFiles);
    }

    [Fact]
    public async Task DirectoryOperationsAndAppend_TrackActualChanges()
    {
        await using var provider = CreateProvider(true);
        var ct = TestContext.Current.CancellationToken;
        await provider.MakeDirectoryAsync("new/nested", true, null, ct);
        await provider.WriteFileAsync("new/nested/File.cs", "one", null, ct);
        await provider.AppendFileAsync("new/nested/File.cs", "two", null, ct);
        Assert.Equal("onetwo", await provider.ReadFileAsync("new/nested/File.cs", ct));
        Assert.Equal(6, (await provider.StatAsync("new/nested/File.cs", ct)).Size);
        await provider.RenameAsync("new", "moved", ct);
        Assert.True(await provider.ExistsAsync("moved/nested/File.cs", ct));
        await provider.RemoveAsync("moved", true, false, ct);
        Assert.Contains(Path.Combine("new", "nested", "File.cs"), provider.ModifiedFiles);
        Assert.Contains(Path.Combine("moved", "nested", "File.cs"), provider.ModifiedFiles);
    }

    [Fact]
    public async Task DisabledWritesAndOversizedAppend_LeaveFilesUnchanged()
    {
        await using var readOnly = CreateProvider(false);
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.MakeDirectoryAsync("new", true, null, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.RemoveAsync("src/Existing.cs", false, false, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.RenameAsync("src", "moved", ct));
        await using var writer = CreateProvider(true);
        var before = File.ReadAllText(Path.Combine(_root, "src", "Existing.cs"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AppendFileAsync("src/Existing.cs", new string('x', 24_000), null, ct));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "src", "Existing.cs")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private LocalProjectSessionFsProvider CreateProvider(bool allowWrites)
        => CreateProvider(".", allowWrites);

    private LocalProjectSessionFsProvider CreateProvider(string projectRoot, bool allowWrites)
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
            projectRoot,
            NullLogger<LocalProjectSessionFsProvider>.Instance);
    }
}
