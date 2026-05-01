using LibGit2Sharp;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Code.Mcp.Tests;

public sealed class GitRepositoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-code-git-tests-" + Guid.NewGuid().ToString("N"));

    public GitRepositoryServiceTests()
    {
        Directory.CreateDirectory(_root);
        Repository.Init(_root);
    }

    [Fact]
    public void GetStatus_ReturnsUntrackedFiles()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        var service = CreateService();

        var status = service.GetStatus(_root);

        Assert.True(status.IsDirty);
        var entry = Assert.Single(status.Entries);
        Assert.Equal("README.md", entry.Path.Replace('\\', '/'));
        Assert.Contains("NewInWorkdir", entry.State, StringComparison.Ordinal);
    }

    [Fact]
    public void StageAndCommit_CreatesCommitAndClearsStatus()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        var service = CreateService();

        service.Stage(_root, ["README.md"]);
        var commit = service.Commit(_root, "Initial commit", "Test User", "test@example.local");
        var status = service.GetStatus(_root);
        var log = service.GetLog(_root, 10);

        Assert.False(status.IsDirty);
        Assert.False(string.IsNullOrWhiteSpace(commit.Sha));
        Assert.Equal("Initial commit", Assert.Single(log.Commits).MessageShort);
    }

    [Fact]
    public void CreateBranchCheckoutAndMerge_ReportsConflicts()
    {
        var service = CreateService();
        WriteCommit(service, "app.txt", "base\n", "base");

        service.CreateBranch(_root, "feature/conflict", checkout: true);
        WriteCommit(service, "app.txt", "feature\n", "feature change");
        service.Checkout(_root, "master");
        WriteCommit(service, "app.txt", "master\n", "master change");

        var merge = service.Merge(_root, "feature/conflict");
        var conflicts = service.GetConflicts(_root);

        Assert.Equal("Conflicts", merge.Status);
        Assert.Contains(conflicts, conflict => conflict.Path.Replace('\\', '/') == "app.txt");
    }

    [Fact]
    public void Clone_CopiesLocalRepositoryIntoAllowedEmptyDirectory()
    {
        var source = Path.Combine(_root, "source");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(source);
        Repository.Init(source);
        var sourceService = CreateService(source, allowNetwork: true);
        WriteCommit(sourceService, Path.Combine(source, "README.md"), "source\n", "source initial", root: source);
        var service = CreateService(allowNetwork: true);

        var clone = service.Clone(source, target);

        Assert.Equal(Path.GetFullPath(target), clone.RepositoryRoot);
        Assert.True(Repository.IsValid(target));
        Assert.True(File.Exists(Path.Combine(target, "README.md")));
    }

    [Fact]
    public void MutationOperation_IsRejectedWhenDisabled()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        var service = CreateService(allowMutations: false);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Stage(_root, ["README.md"]));

        Assert.Contains("disabled by policy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteCommit(GitRepositoryService service, string relativePath, string content, string message, string? root = null)
    {
        var repositoryRoot = root ?? _root;
        var path = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        service.Stage(repositoryRoot, [Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')]);
        service.Commit(repositoryRoot, message, "Test User", "test@example.local");
    }

    private GitRepositoryService CreateService(string? defaultWorkingDirectory = null, bool allowMutations = true, bool allowNetwork = false)
    {
        var settings = new CodeServerSettings
        {
            DefaultWorkingDirectory = defaultWorkingDirectory ?? _root,
            AllowedWorkingRoots = [_root],
            Git = new CodeGitSettings
            {
                AllowMutations = allowMutations,
                AllowNetworkOperations = allowNetwork,
                RequireCleanWorkingTreeForMerge = true,
                DefaultAuthorName = "Test User",
                DefaultAuthorEmail = "test@example.local"
            }
        };
        var policy = new CodePolicy(settings, _root);
        return new GitRepositoryService(policy, Options.Create(settings));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}

