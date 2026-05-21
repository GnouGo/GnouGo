using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Microsoft.Extensions.Options;

namespace GnOuGo.Git.Mcp;

public sealed class GitRepositoryService
{
    private readonly GitPolicy _policy;
    private readonly GitServerSettings _settings;

    public GitRepositoryService(GitPolicy policy, IOptions<GitServerSettings> settings)
    {
        _policy = policy;
        _settings = settings.Value;
    }

    public GitRepositoryInfo GetRepositoryInfo(string? projectRoot)
    {
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        return new GitRepositoryInfo(repositoryRoot, repository.Info.WorkingDirectory, repository.Info.IsBare);
    }

    public GitStatusResult GetStatus(string? projectRoot)
    {
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var entries = repository.RetrieveStatus(new StatusOptions())
            .Select(static entry => new GitStatusEntry(entry.FilePath, entry.State.ToString()))
            .OrderBy(static entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GitStatusResult(
            repositoryRoot,
            repository.Head.FriendlyName,
            repository.Info.IsHeadDetached,
            entries.Length > 0,
            entries);
    }

    public GitDiffResult GetDiff(string? projectRoot, string? relativePath = null, bool staged = false)
    {
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var paths = BuildOptionalPathFilter(relativePath);
        var patch = staged
            ? repository.Diff.Compare<Patch>(repository.Head.Tip?.Tree, DiffTargets.Index, paths)
            : repository.Diff.Compare<Patch>(paths);
        var text = patch.Content ?? string.Empty;
        var max = Math.Max(1, _settings.MaxDiffCharacters);
        var truncated = text.Length > max;
        if (truncated)
            text = text[..max] + "\n...diff truncated by Git:MaxDiffCharacters...";

        return new GitDiffResult(repositoryRoot, relativePath, staged, text, truncated);
    }

    public GitLogResult GetLog(string? projectRoot, int maxCount = 20)
    {
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var limit = Math.Clamp(maxCount, 1, Math.Max(1, _settings.MaxLogCount));
        var commits = repository.Commits
            .Take(limit + 1)
            .Select(static commit => new GitCommitInfo(
                commit.Sha,
                commit.Sha.Length > 12 ? commit.Sha[..12] : commit.Sha,
                commit.MessageShort,
                commit.Author.Name,
                commit.Author.Email,
                commit.Author.When))
            .ToArray();

        return new GitLogResult(repositoryRoot, commits.Take(limit).ToArray(), commits.Length > limit);
    }

    public GitBranchesResult ListBranches(string? projectRoot)
    {
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var branches = repository.Branches
            .Select(branch => new GitBranchInfo(
                branch.FriendlyName,
                branch.FriendlyName,
                branch.UpstreamBranchCanonicalName,
                branch.IsCurrentRepositoryHead,
                branch.IsRemote,
                branch.Tip?.Sha))
            .OrderBy(static branch => branch.IsRemote)
            .ThenBy(static branch => branch.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GitBranchesResult(repositoryRoot, repository.Head.FriendlyName, branches);
    }

    public GitOperationResult CreateBranch(string? projectRoot, string branchName, string? startPoint = null, bool checkout = false)
    {
        _policy.EnsureGitMutationsAllowed("create_branch");
        EnsureSafeBranchName(branchName);
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var branch = startPoint is null
            ? repository.CreateBranch(branchName)
            : repository.CreateBranch(branchName, ResolveCommitish(repository, startPoint));
        if (checkout)
            Commands.Checkout(repository, branch);

        return new GitOperationResult(repositoryRoot, "create_branch", $"Branch '{branch.FriendlyName}' created{(checkout ? " and checked out" : string.Empty)}.", true);
    }

    public GitOperationResult Checkout(string? projectRoot, string branchOrCommit, bool createBranch = false, string? newBranchName = null)
    {
        _policy.EnsureGitMutationsAllowed("checkout");
        if (string.IsNullOrWhiteSpace(branchOrCommit))
            throw new InvalidOperationException("branchOrCommit must not be empty.");

        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        if (createBranch)
        {
            var branchName = string.IsNullOrWhiteSpace(newBranchName) ? branchOrCommit : newBranchName;
            EnsureSafeBranchName(branchName);
            var branch = repository.CreateBranch(branchName, ResolveCommitish(repository, branchOrCommit));
            Commands.Checkout(repository, branch);
            return new GitOperationResult(repositoryRoot, "checkout", $"Created and checked out branch '{branch.FriendlyName}'.", true);
        }

        var existingBranch = repository.Branches[branchOrCommit];
        if (existingBranch is not null)
        {
            Commands.Checkout(repository, existingBranch);
            return new GitOperationResult(repositoryRoot, "checkout", $"Checked out branch '{existingBranch.FriendlyName}'.", true);
        }

        Commands.Checkout(repository, ResolveCommitish(repository, branchOrCommit));
        return new GitOperationResult(repositoryRoot, "checkout", $"Checked out commitish '{branchOrCommit}'.", true);
    }

    public GitOperationResult Stage(string? projectRoot, IReadOnlyList<string> paths)
    {
        _policy.EnsureGitMutationsAllowed("stage");
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var pathspecs = NormalizePathspecs(paths);
        Commands.Stage(repository, pathspecs);
        return new GitOperationResult(repositoryRoot, "stage", $"Staged {pathspecs.Count} pathspec(s).", true);
    }

    public GitOperationResult Unstage(string? projectRoot, IReadOnlyList<string> paths)
    {
        _policy.EnsureGitMutationsAllowed("unstage");
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var pathspecs = NormalizePathspecs(paths);
        Commands.Unstage(repository, pathspecs);
        return new GitOperationResult(repositoryRoot, "unstage", $"Unstaged {pathspecs.Count} pathspec(s).", true);
    }

    public GitCommitInfo Commit(string? projectRoot, string message, string? authorName = null, string? authorEmail = null)
    {
        _policy.EnsureGitMutationsAllowed("commit");
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("message must not be empty.");

        using var repository = OpenRepository(projectRoot, out _);
        var signature = CreateSignature(authorName, authorEmail);
        var commit = repository.Commit(message, signature, signature);
        return new GitCommitInfo(commit.Sha, commit.Sha.Length > 12 ? commit.Sha[..12] : commit.Sha, commit.MessageShort, commit.Author.Name, commit.Author.Email, commit.Author.When);
    }

    public GitMergeResult Merge(string? projectRoot, string branchName, string? authorName = null, string? authorEmail = null)
    {
        _policy.EnsureGitMutationsAllowed("merge");
        if (string.IsNullOrWhiteSpace(branchName))
            throw new InvalidOperationException("branchName must not be empty.");

        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        if (_settings.RequireCleanWorkingTreeForMerge && repository.RetrieveStatus().IsDirty)
            throw new InvalidOperationException("Working tree must be clean before merge by policy. Set Git:RequireCleanWorkingTreeForMerge=false to allow dirty merges.");

        var branch = repository.Branches[branchName] ?? throw new InvalidOperationException($"Branch '{branchName}' was not found.");
        var result = repository.Merge(branch, CreateSignature(authorName, authorEmail));
        return new GitMergeResult(
            repositoryRoot,
            branch.FriendlyName,
            result.Status.ToString(),
            result.Commit?.Sha,
            ListConflicts(repository));
    }

    public IReadOnlyList<GitConflictInfo> GetConflicts(string? projectRoot)
    {
        using var repository = OpenRepository(projectRoot, out _);
        return ListConflicts(repository);
    }

    public GitOperationResult ResolveConflict(string? projectRoot, string relativePath, string strategy)
    {
        _policy.EnsureGitMutationsAllowed("resolve_conflict");
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("relativePath must not be empty.");
        if (string.IsNullOrWhiteSpace(strategy))
            throw new InvalidOperationException("strategy must not be empty.");

        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var normalizedPath = NormalizeRelativePath(relativePath);
        var selectedStrategy = strategy.Trim().ToLowerInvariant();
        switch (selectedStrategy)
        {
            case "ours":
                Commands.Checkout(repository, repository.Head.Tip.Tree, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force }, normalizedPath);
                Commands.Stage(repository, normalizedPath);
                break;
            case "theirs":
                var mergeHead = repository.Refs["MERGE_HEAD"]?.TargetIdentifier;
                if (string.IsNullOrWhiteSpace(mergeHead))
                    throw new InvalidOperationException("Cannot resolve with 'theirs' because MERGE_HEAD is not available.");
                Commands.Checkout(repository, repository.Lookup<Commit>(mergeHead).Tree, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force }, normalizedPath);
                Commands.Stage(repository, normalizedPath);
                break;
            case "stage_existing":
            case "manual":
                Commands.Stage(repository, normalizedPath);
                break;
            default:
                throw new InvalidOperationException("strategy must be one of: ours, theirs, stage_existing, manual.");
        }

        return new GitOperationResult(repositoryRoot, "resolve_conflict", $"Resolved '{normalizedPath}' with strategy '{selectedStrategy}'.", true);
    }

    public GitCloneResult Clone(string remoteUrl, string targetDirectory, string? branch = null)
    {
        _policy.EnsureGitNetworkAllowed("clone");
        _policy.EnsureGitMutationsAllowed("clone");
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new InvalidOperationException("remoteUrl must not be empty.");

        var target = _policy.ResolveCloneTargetDirectory(targetDirectory);
        var options = new CloneOptions();
        if (!string.IsNullOrWhiteSpace(branch))
            options.BranchName = branch;
        ApplyCredentials(options.FetchOptions);

        var repositoryPath = Repository.Clone(remoteUrl, target, options);
        var workingDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repositoryPath)) ?? target;
        return new GitCloneResult(workingDirectory, remoteUrl, branch);
    }

    public GitOperationResult Fetch(string? projectRoot, string? remoteName = null)
    {
        _policy.EnsureGitNetworkAllowed("fetch");
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var remote = ResolveRemote(repository, remoteName);
        var options = new FetchOptions();
        ApplyCredentials(options);
        Commands.Fetch(repository, remote.Name, remote.FetchRefSpecs.Select(static spec => spec.Specification), options, "fetch from Git MCP");
        return new GitOperationResult(repositoryRoot, "fetch", $"Fetched remote '{remote.Name}'.", true);
    }

    public GitMergeResult Pull(string? projectRoot, string? remoteName = null, string? authorName = null, string? authorEmail = null)
    {
        _policy.EnsureGitNetworkAllowed("pull");
        _policy.EnsureGitMutationsAllowed("pull");
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        if (_settings.RequireCleanWorkingTreeForMerge && repository.RetrieveStatus().IsDirty)
            throw new InvalidOperationException("Working tree must be clean before pull by policy. Set Git:RequireCleanWorkingTreeForMerge=false to allow dirty pulls.");

        _ = ResolveRemote(repository, remoteName);
        var options = new PullOptions { FetchOptions = new FetchOptions() };
        ApplyCredentials(options.FetchOptions);
        var result = Commands.Pull(repository, CreateSignature(authorName, authorEmail), options);
        return new GitMergeResult(repositoryRoot, repository.Head.FriendlyName, result.Status.ToString(), result.Commit?.Sha, ListConflicts(repository));
    }

    public GitPushResult Push(string? projectRoot, string? remoteName = null, string? branchName = null, bool setUpstream = true)
    {
        _policy.EnsureGitNetworkAllowed("push");
        _policy.EnsureGitMutationsAllowed("push");
        using var repository = OpenRepository(projectRoot, out var repositoryRoot);
        var remote = ResolveRemote(repository, remoteName);
        var branch = string.IsNullOrWhiteSpace(branchName) ? repository.Head : repository.Branches[branchName];
        if (branch is null)
            throw new InvalidOperationException($"Branch '{branchName}' was not found.");
        if (branch.IsRemote)
            throw new InvalidOperationException("Cannot push a remote-tracking branch directly.");

        var options = new PushOptions();
        ApplyCredentials(options);
        var localRef = branch.CanonicalName;
        var remoteRef = $"refs/heads/{branch.FriendlyName}";
        repository.Network.Push(remote, $"{localRef}:{remoteRef}", options);

        if (setUpstream)
        {
            repository.Branches.Update(branch, updater =>
            {
                updater.Remote = remote.Name;
                updater.UpstreamBranch = remoteRef;
            });
        }

        return new GitPushResult(repositoryRoot, remote.Name, branch.FriendlyName, setUpstream, $"Pushed '{branch.FriendlyName}' to '{remote.Name}'{(setUpstream ? " and set upstream" : string.Empty)}.");
    }

    private Repository OpenRepository(string? projectRoot, out string repositoryRoot)
    {
        var root = _policy.ResolveProjectRoot(projectRoot);
        var discovered = Repository.Discover(root);
        if (string.IsNullOrWhiteSpace(discovered) || !Repository.IsValid(discovered))
            throw new InvalidOperationException($"'{root}' is not inside a Git repository.");

        repositoryRoot = Path.GetFullPath(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(discovered)) ?? root);
        try
        {
            return new Repository(discovered);
        }
        catch (LibGit2SharpException ex)
        {
            throw new InvalidOperationException($"'{root}' is not inside a Git repository or the repository cannot be opened: {ex.Message}", ex);
        }
    }

    private Remote ResolveRemote(Repository repository, string? remoteName)
    {
        var name = string.IsNullOrWhiteSpace(remoteName) ? _settings.DefaultRemoteName : remoteName.Trim();
        var remote = repository.Network.Remotes[name];
        return remote ?? throw new InvalidOperationException($"Remote '{name}' was not found.");
    }

    private Signature CreateSignature(string? authorName, string? authorEmail)
    {
        var name = string.IsNullOrWhiteSpace(authorName) ? _settings.DefaultAuthorName : authorName.Trim();
        var email = string.IsNullOrWhiteSpace(authorEmail) ? _settings.DefaultAuthorEmail : authorEmail.Trim();
        return new Signature(name, email, DateTimeOffset.Now);
    }

    private void ApplyCredentials(FetchOptions options)
    {
        options.CredentialsProvider = CreateCredentialsProvider();
    }

    private void ApplyCredentials(PushOptions options)
    {
        options.CredentialsProvider = CreateCredentialsProvider();
    }

    private CredentialsHandler? CreateCredentialsProvider()
    {
        var token = _policy.ResolveGitToken();
        if (string.IsNullOrWhiteSpace(token))
            return null;

        return (_, _, _) => new UsernamePasswordCredentials
        {
            Username = string.IsNullOrWhiteSpace(_settings.Username) ? "x-access-token" : _settings.Username,
            Password = token
        };
    }

    private static IReadOnlyList<string> BuildOptionalPathFilter(string? relativePath)
        => string.IsNullOrWhiteSpace(relativePath) ? [] : [NormalizeRelativePath(relativePath)];

    private static IReadOnlyList<string> NormalizePathspecs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return ["*"];
        return paths.Select(NormalizeRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("Git path must not be empty.");
        if (Path.IsPathRooted(relativePath) || ContainsParentTraversalSegment(relativePath) || relativePath.IndexOfAny(['*', '?']) >= 0)
            throw new InvalidOperationException("Git paths must be relative and must not contain traversal or wildcard characters.");
        return relativePath.Replace('\\', '/').Trim('/');
    }

    private static void EnsureSafeBranchName(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            throw new InvalidOperationException("branchName must not be empty.");
        if (branchName.Contains("..", StringComparison.Ordinal) || branchName.StartsWith('/') || branchName.EndsWith('/') || branchName.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) >= 0)
            throw new InvalidOperationException("branchName contains characters that are not allowed by the Git MCP policy.");
    }

    private static Commit ResolveCommitish(Repository repository, string commitish)
    {
        var commit = repository.Lookup<Commit>(commitish)
            ?? repository.Branches[commitish]?.Tip
            ?? repository.Branches[$"origin/{commitish}"]?.Tip
            ?? ResolveTagTarget(repository, commitish);
        return commit ?? throw new InvalidOperationException($"Commitish '{commitish}' was not found.");
    }

    private static bool ContainsParentTraversalSegment(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal));

    private static Commit? ResolveTagTarget(Repository repository, string tagName)
    {
        var tag = repository.Tags[tagName];
        if (tag?.Target is Commit commit)
            return commit;

        if (tag?.Target is TagAnnotation annotation && annotation.Target is Commit annotatedCommit)
            return annotatedCommit;

        return null;
    }

    private static IReadOnlyList<GitConflictInfo> ListConflicts(Repository repository)
        => repository.Index.Conflicts?
            .Select(static conflict => new GitConflictInfo(
                conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path ?? string.Empty,
                conflict.Ancestor?.Path,
                conflict.Ours?.Path,
                conflict.Theirs?.Path))
            .Where(static conflict => !string.IsNullOrWhiteSpace(conflict.Path))
            .OrderBy(static conflict => conflict.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
}




