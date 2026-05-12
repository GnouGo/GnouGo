using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GnOuGo.Git.Mcp;

[McpServerToolType]
public sealed class GitTools
{
    private readonly GitPolicy _policy;
    private readonly GitRepositoryService _gitRepositoryService;
    private readonly ILogger<GitTools> _logger;

    public GitTools(GitPolicy policy, GitRepositoryService gitRepositoryService, ILogger<GitTools> logger)
    {
        _policy = policy;
        _gitRepositoryService = gitRepositoryService;
        _logger = logger;
    }

    [McpServerTool(Name = "git_get_policy"), Description("Returns the active Git MCP policy: allowed roots, mutation/network flags, limits, and auth source status.")]
    public GitPolicyInfo GetPolicy() => _policy.DescribePolicy();

    [McpServerTool(Name = "git_repository_info"), Description("Returns basic information about the Git repository containing the project root.")]
    public object GitRepositoryInfo([Description("Project root path or null for default.")] string? projectRoot = null)
        => Execute(() => _gitRepositoryService.GetRepositoryInfo(projectRoot));

    [McpServerTool(Name = "git_status"), Description("Returns Git working tree and index status for the repository containing the project root.")]
    public object GitStatus([Description("Project root path or null for default.")] string? projectRoot = null)
        => Execute(() => _gitRepositoryService.GetStatus(projectRoot));

    [McpServerTool(Name = "git_diff"), Description("Returns a truncated Git patch for unstaged or staged changes, optionally limited to one relative path.")]
    public object GitDiff(
        [Description("Project root path or null for default.")] string? projectRoot = null,
        [Description("Optional relative path to diff.")] string? relativePath = null,
        [Description("When true, returns staged/index changes instead of unstaged working tree changes.")] bool staged = false)
        => Execute(() => _gitRepositoryService.GetDiff(projectRoot, relativePath, staged));

    [McpServerTool(Name = "git_log"), Description("Returns recent commits for the current repository.")]
    public object GitLog(
        [Description("Project root path or null for default.")] string? projectRoot = null,
        [Description("Maximum number of commits to return.")] int maxCount = 20)
        => Execute(() => _gitRepositoryService.GetLog(projectRoot, maxCount));

    [McpServerTool(Name = "git_branches"), Description("Lists local and remote Git branches and marks the current HEAD branch.")]
    public object GitBranches([Description("Project root path or null for default.")] string? projectRoot = null)
        => Execute(() => _gitRepositoryService.ListBranches(projectRoot));

    [McpServerTool(Name = "git_create_branch"), Description("Creates a local Git branch, optionally from a start point and optionally checks it out. Requires Git:AllowMutations=true.")]
    public object GitCreateBranch(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Name of the branch to create.")] string branchName,
        [Description("Optional start point commit, branch, or tag.")] string? startPoint = null,
        [Description("Whether to checkout the new branch immediately.")] bool checkout = false)
        => Execute(() => _gitRepositoryService.CreateBranch(projectRoot, branchName, startPoint, checkout));

    [McpServerTool(Name = "git_checkout"), Description("Checks out an existing branch/commit, or creates a branch from a start point. Requires Git:AllowMutations=true.")]
    public object GitCheckout(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Branch name, tag, or commit SHA to checkout.")] string branchOrCommit,
        [Description("Create a new branch from branchOrCommit instead of checking it out directly.")] bool createBranch = false,
        [Description("New branch name when createBranch=true. If omitted, branchOrCommit is used.")] string? newBranchName = null)
        => Execute(() => _gitRepositoryService.Checkout(projectRoot, branchOrCommit, createBranch, newBranchName));

    [McpServerTool(Name = "git_stage"), Description("Stages pathspecs for commit. pathsJson is a JSON array of relative paths; empty/null stages all changes. Requires Git:AllowMutations=true.")]
    public object GitStage(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Optional JSON array of relative paths, for example [\"src/App.cs\"]. Empty/null means all changes.")] string? pathsJson = null)
        => Execute(() => _gitRepositoryService.Stage(projectRoot, ParseGitPaths(pathsJson)));

    [McpServerTool(Name = "git_unstage"), Description("Unstages pathspecs. pathsJson is a JSON array of relative paths; empty/null unstages all changes. Requires Git:AllowMutations=true.")]
    public object GitUnstage(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Optional JSON array of relative paths. Empty/null means all changes.")] string? pathsJson = null)
        => Execute(() => _gitRepositoryService.Unstage(projectRoot, ParseGitPaths(pathsJson)));

    [McpServerTool(Name = "git_commit"), Description("Creates a Git commit from staged changes. Requires Git:AllowMutations=true.")]
    public object GitCommit(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Commit message.")] string message,
        [Description("Optional author name.")] string? authorName = null,
        [Description("Optional author email.")] string? authorEmail = null)
        => Execute(() => _gitRepositoryService.Commit(projectRoot, message, authorName, authorEmail));

    [McpServerTool(Name = "git_merge"), Description("Merges another branch into the current branch and reports conflicts. Requires Git:AllowMutations=true.")]
    public object GitMerge(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Branch to merge into the current branch.")] string branchName,
        [Description("Optional merge author name.")] string? authorName = null,
        [Description("Optional merge author email.")] string? authorEmail = null)
        => Execute(() => _gitRepositoryService.Merge(projectRoot, branchName, authorName, authorEmail));

    [McpServerTool(Name = "git_conflicts"), Description("Lists current Git merge conflicts, if any.")]
    public object GitConflicts([Description("Project root path or null for default.")] string? projectRoot = null)
        => Execute(() => _gitRepositoryService.GetConflicts(projectRoot));

    [McpServerTool(Name = "git_resolve_conflict"), Description("Marks a conflict as resolved using 'ours', 'theirs', or 'stage_existing' after manual editing. Requires Git:AllowMutations=true.")]
    public object GitResolveConflict(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Relative path of the conflicted file.")] string relativePath,
        [Description("Resolution strategy: ours, theirs, or stage_existing.")] string strategy)
        => Execute(() => _gitRepositoryService.ResolveConflict(projectRoot, relativePath, strategy));

    [McpServerTool(Name = "git_clone"), Description("Clones a Git repository into an allowed target directory. Requires Git:AllowNetworkOperations=true and Git:AllowMutations=true.")]
    public object GitClone(
        [Description("Remote Git URL to clone.")] string remoteUrl,
        [Description("Target directory path. It must be inside the allowed roots and empty/non-existing.")] string targetDirectory,
        [Description("Optional branch to checkout during clone.")] string? branch = null)
        => Execute(() => _gitRepositoryService.Clone(remoteUrl, targetDirectory, branch));

    [McpServerTool(Name = "git_fetch"), Description("Fetches from a remote. Requires Git:AllowNetworkOperations=true.")]
    public object GitFetch(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Remote name. Defaults to Git:DefaultRemoteName.")] string? remoteName = null)
        => Execute(() => _gitRepositoryService.Fetch(projectRoot, remoteName));

    [McpServerTool(Name = "git_pull"), Description("Pulls the current branch from its configured remote. Requires Git:AllowNetworkOperations=true and Git:AllowMutations=true.")]
    public object GitPull(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Remote name. Defaults to Git:DefaultRemoteName.")] string? remoteName = null,
        [Description("Optional merge author name.")] string? authorName = null,
        [Description("Optional merge author email.")] string? authorEmail = null)
        => Execute(() => _gitRepositoryService.Pull(projectRoot, remoteName, authorName, authorEmail));

    [McpServerTool(Name = "git_push"), Description("Pushes a local branch and optionally sets upstream. Requires Git:AllowNetworkOperations=true and Git:AllowMutations=true.")]
    public object GitPush(
        [Description("Project root path or null for default.")] string? projectRoot,
        [Description("Remote name. Defaults to Git:DefaultRemoteName.")] string? remoteName = null,
        [Description("Branch name. Defaults to the current branch.")] string? branchName = null,
        [Description("Whether to configure upstream after push.")] bool setUpstream = true)
        => Execute(() => _gitRepositoryService.Push(projectRoot, remoteName, branchName, setUpstream));

    private object Execute<T>(Func<T> action)
    {
        try
        {
            return action()!;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or UnauthorizedAccessException or IOException or JsonException or LibGit2SharpException)
        {
            _logger.LogWarning(ex, "Git MCP tool policy/input error");
            return new GitErrorResult("POLICY_OR_INPUT_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git MCP tool unexpected error");
            return new GitErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static IReadOnlyList<string> ParseGitPaths(string? pathsJson)
    {
        if (string.IsNullOrWhiteSpace(pathsJson))
            return [];
        var values = JsonSerializer.Deserialize(pathsJson, GitToolsJsonContext.Default.ListString);
        return values?.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    }
}

[JsonSerializable(typeof(List<string>))]
internal sealed partial class GitToolsJsonContext : JsonSerializerContext;



