using LibGit2Sharp;
using Microsoft.Extensions.Options;
using Xunit;

namespace GnOuGo.Git.Mcp.Tests;

public sealed class GitRepositoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnougo-git-mcp-tests-" + Guid.NewGuid().ToString("N"));

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

        var status = service.GetStatus(".");

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

        service.Stage(".", ["README.md"]);
        var commit = service.Commit(".", "Initial commit", "Test User", "test@example.local");
        var status = service.GetStatus(".");
        var log = service.GetLog(".", 10);

        Assert.False(status.IsDirty);
        Assert.Contains("Repository is clean", status.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(commit.Sha));
        Assert.Contains("Created commit", commit.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Initial commit", Assert.Single(log.Commits).MessageShort);
        Assert.Contains("Returned 1 commit", log.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StageAll_ExcludesGnOuGoWorkingDirectoryByDefault()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        var copilotWorkingDirectory = Path.Combine(_root, ".GnOuGo");
        Directory.CreateDirectory(copilotWorkingDirectory);
        File.WriteAllText(Path.Combine(copilotWorkingDirectory, "temp.json"), "{}\n");
        var service = CreateService();

        var result = service.Stage(".", []);
        var status = service.GetStatus(".");

        Assert.True(result.Success);
        Assert.Contains("Staged 1 pathspec", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(status.Entries, entry => entry.Path.Replace('\\', '/') == "README.md" && entry.State.Contains("NewInIndex", StringComparison.Ordinal));
        Assert.Contains(status.Entries, entry => entry.Path.Replace('\\', '/') == ".GnOuGo/temp.json" && entry.State.Contains("NewInWorkdir", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Entries, entry => entry.Path.Replace('\\', '/') == ".GnOuGo/temp.json" && entry.State.Contains("NewInIndex", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateBranchCheckoutAndMerge_ReportsConflicts()
    {
        var service = CreateService();
        WriteCommit(service, "app.txt", "base\n", "base");

        service.CreateBranch(".", "feature/conflict", checkout: true);
        WriteCommit(service, "app.txt", "feature\n", "feature change");
        service.Checkout(".", "master");
        WriteCommit(service, "app.txt", "master\n", "master change");

        var merge = service.Merge(".", "feature/conflict");
        var conflicts = service.GetConflicts(".");

        Assert.Equal("Conflicts", merge.Status);
        Assert.Contains(conflicts, conflict => conflict.Path.Replace('\\', '/') == "app.txt");
    }

    [Fact]
    public void CreateBranch_AllowsTagAsStartPoint()
    {
        var service = CreateService();
        var initialCommit = WriteCommit(service, "README.md", "base\n", "base");

        using (var repository = new Repository(_root))
        {
            repository.ApplyTag("v1.0.0", initialCommit.Sha);
        }

        var result = service.CreateBranch(".", "release/from-tag", startPoint: "v1.0.0");

        Assert.True(result.Success);

        using var verificationRepository = new Repository(_root);
        var branch = verificationRepository.Branches["release/from-tag"];
        Assert.NotNull(branch);
        Assert.Equal(initialCommit.Sha, branch.Tip?.Sha);
    }

    [Fact]
    public void DeleteBranch_RemovesLocalBranch()
    {
        var service = CreateService();
        WriteCommit(service, "README.md", "base\n", "base");
        service.CreateBranch(".", "feature/delete-me", checkout: true);
        WriteCommit(service, "feature.txt", "unmerged\n", "unmerged feature");
        service.Checkout(".", "master");

        var result = service.DeleteBranch(".", "feature/delete-me");

        Assert.True(result.Success);
        Assert.Contains("Deleted local branch", result.Output, StringComparison.OrdinalIgnoreCase);

        using var repository = new Repository(_root);
        Assert.Null(repository.Branches["feature/delete-me"]);
    }

    [Fact]
    public void DeleteBranch_RejectsCurrentBranch()
    {
        var service = CreateService();
        WriteCommit(service, "README.md", "base\n", "base");
        service.CreateBranch(".", "feature/current", checkout: true);

        var ex = Assert.Throws<InvalidOperationException>(() => service.DeleteBranch(".", "feature/current"));

        Assert.Contains("currently checked-out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteRemoteBranch_RemovesBranchFromRemote()
    {
        var remoteRoot = Path.Combine(_root, "remote.git");
        Repository.Init(remoteRoot, isBare: true);
        var service = CreateService(allowNetwork: true);
        WriteCommit(service, "README.md", "base\n", "base");
        service.CreateBranch(".", "feature/remote-delete");

        using (var repository = new Repository(_root))
        {
            repository.Network.Remotes.Add("origin", remoteRoot);
        }

        service.Push(".", "origin", "feature/remote-delete", setUpstream: false);

        using (var remoteRepository = new Repository(remoteRoot))
        {
            Assert.NotNull(remoteRepository.Branches["feature/remote-delete"]);
        }

        var result = service.DeleteRemoteBranch(".", "origin", "feature/remote-delete");

        Assert.True(result.Success);
        Assert.Contains("Deleted remote branch", result.Output, StringComparison.OrdinalIgnoreCase);

        using (var remoteRepository = new Repository(remoteRoot))
        {
            Assert.Null(remoteRepository.Branches["feature/remote-delete"]);
        }
    }

    [Fact]
    public void FetchRefSpecAndSwitchBranch_MatchesGitFetchAndSwitchC()
    {
        var remoteRoot = Path.Combine(_root, "remote.git");
        var sourceRoot = Path.Combine(_root, "source");
        var clientRoot = Path.Combine(_root, "client");
        Repository.Init(remoteRoot, isBare: true);
        Directory.CreateDirectory(sourceRoot);
        Repository.Init(sourceRoot);

        var sourceService = CreateService(sourceRoot, allowNetwork: true);
        WriteCommit(sourceService, "README.md", "base\n", "base", sourceRoot);
        using (var sourceRepository = new Repository(sourceRoot))
        {
            sourceRepository.Network.Remotes.Add("origin", remoteRoot);
        }

        sourceService.Push(".", "origin", "master", setUpstream: false);
        sourceService.CreateBranch(".", "feature/pr-branch", checkout: true);
        var featureCommit = WriteCommit(sourceService, "feature.txt", "feature\n", "feature", sourceRoot);
        sourceService.Push(".", "origin", "feature/pr-branch", setUpstream: false);

        Directory.CreateDirectory(clientRoot);
        Repository.Init(clientRoot);
        using (var clientRepository = new Repository(clientRoot))
        {
            clientRepository.Network.Remotes.Add("origin", remoteRoot);
        }

        var clientService = CreateService(clientRoot, allowNetwork: true);
        var fetch = clientService.Fetch(
            ".",
            "origin",
            "refs/heads/feature/pr-branch:refs/remotes/origin/feature/pr-branch");
        var switchResult = clientService.SwitchBranch(".", "feature/pr-branch", "origin/feature/pr-branch");

        Assert.True(fetch.Success);
        Assert.True(switchResult.Success);
        Assert.Contains("Fetched", fetch.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Switched to branch", switchResult.Output, StringComparison.OrdinalIgnoreCase);

        using var verificationRepository = new Repository(clientRoot);
        Assert.Equal("feature/pr-branch", verificationRepository.Head.FriendlyName);
        Assert.Equal(featureCommit.Sha, verificationRepository.Head.Tip.Sha);
        Assert.NotNull(verificationRepository.Branches["origin/feature/pr-branch"]);
        Assert.True(File.Exists(Path.Combine(clientRoot, "feature.txt")));
    }

    [Fact]
    public void SwitchBranch_ResetsExistingLocalBranchToStartPoint()
    {
        var service = CreateService();
        WriteCommit(service, "README.md", "base\n", "base");
        service.CreateBranch(".", "feature/reset", checkout: true);
        var oldCommit = WriteCommit(service, "feature.txt", "old\n", "old feature");
        service.Checkout(".", "master");
        var newCommit = WriteCommit(service, "README.md", "base\nnew\n", "new master");

        var result = service.SwitchBranch(".", "feature/reset", "master");

        Assert.True(result.Success);
        using var repository = new Repository(_root);
        Assert.Equal("feature/reset", repository.Head.FriendlyName);
        Assert.Equal(newCommit.Sha, repository.Head.Tip.Sha);
        Assert.NotEqual(oldCommit.Sha, repository.Head.Tip.Sha);
    }

    [Fact]
    public void Stage_AllowsRelativeFileNamesContainingDoubleDotsWithoutTraversal()
    {
        File.WriteAllText(Path.Combine(_root, "a..b.txt"), "hello\n");
        var service = CreateService();

        var result = service.Stage(".", ["a..b.txt"]);
        var status = service.GetStatus(".");

        Assert.True(result.Success);
        Assert.Contains("Staged 1 pathspec", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(status.Entries, entry => entry.Path.Replace('\\', '/') == "a..b.txt" && entry.State.Contains("NewInIndex", StringComparison.Ordinal));
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

        var clone = service.Clone(source, "target");

        Assert.Equal(NormalizePath(target), NormalizePath(clone.RepositoryRoot));
        Assert.Equal("target", clone.RepositoryRootRelative);
        Assert.Equal("target", clone.ProjectRootRelative);
        Assert.Contains("Cloned", clone.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target", clone.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(Repository.IsValid(target));
        Assert.True(File.Exists(Path.Combine(target, "README.md")));
    }

    [Fact]
    public void MutationOperation_IsRejectedWhenDisabled()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        var service = CreateService(allowMutations: false);

        var ex = Assert.Throws<InvalidOperationException>(() => service.Stage(".", ["README.md"]));

        Assert.Contains("disabled by policy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private GitCommitInfo WriteCommit(GitRepositoryService service, string relativePath, string content, string message, string? root = null)
    {
        var repositoryRoot = root ?? _root;
        var path = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(repositoryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        service.Stage(".", [Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')]);
        return service.Commit(".", message, "Test User", "test@example.local");
    }

    private GitRepositoryService CreateService(string? defaultWorkingDirectory = null, bool allowMutations = true, bool allowNetwork = false)
    {
        var settings = new GitServerSettings
        {
            DefaultWorkingDirectory = defaultWorkingDirectory ?? _root,
            AllowedWorkingRoots = [_root],
            AllowMutations = allowMutations,
            AllowNetworkOperations = allowNetwork,
            RequireCleanWorkingTreeForMerge = true,
            DefaultAuthorName = "Test User",
            DefaultAuthorEmail = "test@example.local"
        };
        var policy = new GitPolicy(settings, _root);
        return new GitRepositoryService(policy, Options.Create(settings));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Resolves symlinks so that /var/folders and /private/var/folders compare equal on macOS.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsMacOS() && full.StartsWith("/var/", StringComparison.Ordinal))
            full = "/private" + full;
        return full;
    }
}
