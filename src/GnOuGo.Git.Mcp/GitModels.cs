using System.ComponentModel;

namespace GnOuGo.Git.Mcp;

public sealed record GitPolicyInfo(
    string DefaultWorkingDirectory,
    IReadOnlyList<string> AllowedWorkingRoots,
    bool AllowMutations,
    bool AllowNetworkOperations,
    bool RequireCleanWorkingTreeForMerge,
    int MaxDiffCharacters,
    int MaxLogCount,
    string DefaultRemoteName,
    bool HasConfiguredToken,
    IReadOnlyList<string> TokenEnvironmentVariables);

public sealed record GitErrorResult(string Code, string Message);

public sealed record GitRepositoryInfo(
    string RootPath,
    string WorkingDirectory,
    bool IsBare,
    string? Output = null,
    string? RootPathRelative = null,
    string? WorkingDirectoryRelative = null)
{
    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RootPath.")]
    public string? ProjectRootRelative => RootPathRelative;
}

public sealed record GitStatusEntry(string Path, string State);

public sealed record GitStatusResult(
    string RepositoryRoot,
    string? HeadBranch,
    bool IsDetachedHead,
    bool IsDirty,
    IReadOnlyList<GitStatusEntry> Entries,
    string? Output = null,
    string? RepositoryRootRelative = null)
{
    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RepositoryRoot.")]
    public string? ProjectRootRelative => RepositoryRootRelative;
}

public sealed record GitDiffResult(
    string RepositoryRoot,
    string? Path,
    bool Staged,
    string Patch,
    bool Truncated,
    string? Output = null,
    string? RepositoryRootRelative = null)
{
    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RepositoryRoot.")]
    public string? ProjectRootRelative => RepositoryRootRelative;
}

public sealed record GitCommitInfo(string Sha, string ShortSha, string MessageShort, string AuthorName, string AuthorEmail, DateTimeOffset When, string? Output = null);

public sealed record GitLogResult(
    string RepositoryRoot,
    IReadOnlyList<GitCommitInfo> Commits,
    bool Truncated,
    string? Output = null,
    string? RepositoryRootRelative = null)
{
    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RepositoryRoot.")]
    public string? ProjectRootRelative => RepositoryRootRelative;
}

public sealed record GitBranchInfo(string Name, string FriendlyName, string? UpstreamBranch, bool IsCurrentRepositoryHead, bool IsRemote, string? TipSha);

public sealed record GitBranchesResult(
    string RepositoryRoot,
    string? HeadBranch,
    IReadOnlyList<GitBranchInfo> Branches,
    string? Output = null,
    string? RepositoryRootRelative = null)
{
    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RepositoryRoot.")]
    public string? ProjectRootRelative => RepositoryRootRelative;
}

public sealed record GitOperationResult(
    string RepositoryRoot,
    string Operation,
    string Message,
    bool Success,
    string? Output = null,
    string? RepositoryRootRelative = null)
{
    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RepositoryRoot.")]
    public string? ProjectRootRelative => RepositoryRootRelative;
}

public sealed record GitCloneResult(
    string RepositoryRoot,
    string RemoteUrl,
    string? Branch,
    [property: Description("Required workspace-relative path to an existing project root created by git_clone. Pass this value to MCP projectRoot inputs after clone succeeds; do not reuse targetDirectory before clone.")]
    string ProjectRootRelative,
    string? Output = null,
    string? RepositoryRootRelative = null);

public sealed record GitMergeResult(
    string RepositoryRoot,
    string Branch,
    string Status,
    string? CommitSha,
    IReadOnlyList<GitConflictInfo> Conflicts,
    string? Output = null,
    string? RepositoryRootRelative = null)
{
    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RepositoryRoot.")]
    public string? ProjectRootRelative => RepositoryRootRelative;
}

public sealed record GitConflictInfo(string Path, string? AncestorPath, string? OursPath, string? TheirsPath);

public sealed record GitPushResult(
    string RepositoryRoot,
    string RemoteName,
    string BranchName,
    bool SetUpstream,
    string Message,
    string? Output = null,
    string? RepositoryRootRelative = null)
{
    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RepositoryRoot.")]
    public string? ProjectRootRelative => RepositoryRootRelative;
}
