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

    [McpServerTool(Name = "git_get_policy"), Description("Returns the active Git MCP policy: allowed roots, mutation/network flags, limits, and auth source status. Call this first to discover the default workspace.")]
    public GitPolicyInfo GetPolicy() => _policy.DescribePolicy();

    [McpServerTool(Name = "git_repository_info"), Description("Returns basic information about an existing Git repository project root. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitRepositoryInfo([Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot = null)
        => Execute(() => _gitRepositoryService.GetRepositoryInfo(projectRoot));

    [McpServerTool(Name = "git_status"), Description("Returns Git working tree and index status for an existing repository project root. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitStatus([Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot = null)
        => Execute(() => _gitRepositoryService.GetStatus(projectRoot));

    [McpServerTool(Name = "git_diff"), Description("Returns a truncated Git patch for an existing repository project root, optionally limited to one relative path. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitDiff(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot = null,
        [Description("Optional relative path only, from the workspace root, to diff.")] string? relativePath = null,
        [Description("When true, returns staged/index changes instead of unstaged working tree changes.")] bool staged = false)
        => Execute(() => _gitRepositoryService.GetDiff(projectRoot, relativePath, staged));

    [McpServerTool(Name = "git_log"), Description("Returns recent commits for an existing repository project root. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitLog(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot = null,
        [Description("Maximum number of commits to return.")] int maxCount = 20)
        => Execute(() => _gitRepositoryService.GetLog(projectRoot, maxCount));

    [McpServerTool(Name = "git_branches"), Description("Lists local and remote Git branches for an existing repository project root. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitBranches([Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot = null)
        => Execute(() => _gitRepositoryService.ListBranches(projectRoot));

    [McpServerTool(Name = "git_create_branch"), Description("Creates a local Git branch in an existing repository project root, optionally from a start point and optionally checks it out. Requires Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitCreateBranch(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Name of the branch to create.")] string branchName,
        [Description("Optional start point commit, branch, or tag.")] string? startPoint = null,
        [Description("Whether to checkout the new branch immediately.")] bool checkout = false)
        => Execute(() => _gitRepositoryService.CreateBranch(projectRoot, branchName, startPoint, checkout));

    [McpServerTool(Name = "git_delete_branch"), Description("Deletes a local Git branch in an existing repository project root. Requires Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitDeleteBranch(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Name of the local branch to delete.")] string branchName)
        => Execute(() => _gitRepositoryService.DeleteBranch(projectRoot, branchName));

    [McpServerTool(Name = "git_checkout"), Description("Checks out an existing branch/commit in an existing repository project root, or creates a branch from a start point. Requires Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitCheckout(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Branch name, tag, or commit SHA to checkout.")] string branchOrCommit,
        [Description("Create a new branch from branchOrCommit instead of checking it out directly.")] bool createBranch = false,
        [Description("New branch name when createBranch=true. If omitted, branchOrCommit is used.")] string? newBranchName = null)
        => Execute(() => _gitRepositoryService.Checkout(projectRoot, branchOrCommit, createBranch, newBranchName));

    [McpServerTool(Name = "git_switch_branch"), Description("Switches an existing repository project root to a local branch with git switch -C semantics. Requires Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitSwitchBranch(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Local branch name to create/reset and switch to, equivalent to git switch -C <branchName>.")] string branchName,
        [Description("Optional start point commit, branch, tag, or remote-tracking branch. Defaults to <remoteName>/<branchName>.")] string? startPoint = null,
        [Description("Remote name used for the default start point and upstream tracking. Defaults to Git:DefaultRemoteName.")] string? remoteName = null)
        => Execute(() => _gitRepositoryService.SwitchBranch(projectRoot, branchName, startPoint, remoteName));

    [McpServerTool(Name = "git_stage"), Description("Stages pathspecs in an existing repository project root. pathsJson is a JSON array of relative paths only; empty/null stages all changes. Requires Git:AllowMutations=true.")]
    public object GitStage(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Optional JSON array of relative paths only, for example [\"src/App.cs\"]. Empty/null means all changes.")] string? pathsJson = null)
        => Execute(() => _gitRepositoryService.Stage(projectRoot, ParseGitPaths(pathsJson)));

    [McpServerTool(Name = "git_unstage"), Description("Unstages pathspecs in an existing repository project root. pathsJson is a JSON array of relative paths only; empty/null unstages all changes. Requires Git:AllowMutations=true.")]
    public object GitUnstage(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Optional JSON array of relative paths only. Empty/null means all changes.")] string? pathsJson = null)
        => Execute(() => _gitRepositoryService.Unstage(projectRoot, ParseGitPaths(pathsJson)));

    [McpServerTool(Name = "git_commit"), Description("Creates a Git commit from staged changes in an existing repository project root. Requires Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitCommit(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Commit message.")] string message,
        [Description("Optional author name.")] string? authorName = null,
        [Description("Optional author email.")] string? authorEmail = null)
        => Execute(() => _gitRepositoryService.Commit(projectRoot, message, authorName, authorEmail));

    [McpServerTool(Name = "git_merge"), Description("Merges another branch into the current branch of an existing repository project root and reports conflicts. Requires Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitMerge(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Branch to merge into the current branch.")] string branchName,
        [Description("Optional merge author name.")] string? authorName = null,
        [Description("Optional merge author email.")] string? authorEmail = null)
        => Execute(() => _gitRepositoryService.Merge(projectRoot, branchName, authorName, authorEmail));

    [McpServerTool(Name = "git_conflicts"), Description("Lists current Git merge conflicts in an existing repository project root, if any. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitConflicts([Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot = null)
        => Execute(() => _gitRepositoryService.GetConflicts(projectRoot));

    [McpServerTool(Name = "git_resolve_conflict"), Description("Marks a conflict as resolved in an existing repository project root using 'ours', 'theirs', or 'stage_existing' after manual editing. Requires Git:AllowMutations=true.")]
    public object GitResolveConflict(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Relative path only, from the workspace root, of the conflicted file.")] string relativePath,
        [Description("Resolution strategy: ours, theirs, or stage_existing.")] string strategy)
        => Execute(() => _gitRepositoryService.ResolveConflict(projectRoot, relativePath, strategy));

    [McpServerTool(Name = "git_clone"), Description("Clones a Git repository into a new workspace target directory. Requires Git:AllowNetworkOperations=true and Git:AllowMutations=true. targetDirectory is a creation target, not an existing projectRoot before clone. After success, pass response.projectRootRelative to Git/Code projectRoot inputs.")]
    public object GitClone(
        [Description("Remote Git URL to clone.")] string remoteUrl,
        [Description("Clone target directory relative to the workspace root only. Must be empty or non-existing. After clone succeeds, use response.projectRootRelative as the existing projectRoot for later tools.")] string targetDirectory,
        [Description("Optional branch to checkout during clone.")] string? branch = null)
        => Execute(() => _gitRepositoryService.Clone(remoteUrl, targetDirectory, branch));

    [McpServerTool(Name = "git_fetch"), Description("Fetches from a remote for an existing repository project root. Requires Git:AllowNetworkOperations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitFetch(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Remote name. Defaults to Git:DefaultRemoteName.")] string? remoteName = null,
        [Description("Optional fetch refspec, for example refs/heads/my-branch:refs/remotes/origin/my-branch.")] string? refSpec = null)
        => Execute(() => _gitRepositoryService.Fetch(projectRoot, remoteName, refSpec));

    [McpServerTool(Name = "git_pull"), Description("Pulls the current branch of an existing repository project root from its configured remote. Requires Git:AllowNetworkOperations=true and Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitPull(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Remote name. Defaults to Git:DefaultRemoteName.")] string? remoteName = null,
        [Description("Optional merge author name.")] string? authorName = null,
        [Description("Optional merge author email.")] string? authorEmail = null)
        => Execute(() => _gitRepositoryService.Pull(projectRoot, remoteName, authorName, authorEmail));

    [McpServerTool(Name = "git_push"), Description("Pushes a local branch from an existing repository project root and optionally sets upstream. Requires Git:AllowNetworkOperations=true and Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitPush(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Remote name. Defaults to Git:DefaultRemoteName.")] string? remoteName = null,
        [Description("Branch name. Defaults to the current branch.")] string? branchName = null,
        [Description("Whether to configure upstream after push.")] bool setUpstream = true)
        => Execute(() => _gitRepositoryService.Push(projectRoot, remoteName, branchName, setUpstream));

    [McpServerTool(Name = "git_delete_remote_branch"), Description("Deletes a branch on a remote from an existing repository project root. Requires Git:AllowNetworkOperations=true and Git:AllowMutations=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GitDeleteRemoteBranch(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Remote name. Defaults to Git:DefaultRemoteName.")] string? remoteName,
        [Description("Name of the remote branch to delete.")] string branchName)
        => Execute(() => _gitRepositoryService.DeleteRemoteBranch(projectRoot, remoteName, branchName));

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
        var values = JsonSerializer.Deserialize(pathsJson, GitMcpJsonContext.Default.ListString);
        return values?.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    }
}

internal static class GitMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, GitMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(GitPolicyInfo))]
[JsonSerializable(typeof(GitErrorResult))]
[JsonSerializable(typeof(GitRepositoryInfo))]
[JsonSerializable(typeof(GitStatusEntry))]
[JsonSerializable(typeof(IReadOnlyList<GitStatusEntry>))]
[JsonSerializable(typeof(GitStatusResult))]
[JsonSerializable(typeof(GitDiffResult))]
[JsonSerializable(typeof(GitCommitInfo))]
[JsonSerializable(typeof(IReadOnlyList<GitCommitInfo>))]
[JsonSerializable(typeof(GitLogResult))]
[JsonSerializable(typeof(GitBranchInfo))]
[JsonSerializable(typeof(IReadOnlyList<GitBranchInfo>))]
[JsonSerializable(typeof(GitBranchesResult))]
[JsonSerializable(typeof(GitOperationResult))]
[JsonSerializable(typeof(GitCloneResult))]
[JsonSerializable(typeof(GitConflictInfo))]
[JsonSerializable(typeof(IReadOnlyList<GitConflictInfo>))]
[JsonSerializable(typeof(GitMergeResult))]
[JsonSerializable(typeof(GitPushResult))]
internal sealed partial class GitMcpJsonContext : JsonSerializerContext;
