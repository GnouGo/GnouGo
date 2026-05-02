namespace GnOuGo.Code.Mcp;

public sealed record CodePolicyInfo(
    string DefaultWorkingDirectory,
    IReadOnlyList<string> AllowedWorkingRoots,
    IReadOnlyList<string> AllowedExtensions,
    long MaxFileSizeBytes,
    int MaxSearchResults,
    int MaxPromptCharacters,
    bool AllowWrites,
    string CopilotProvider,
    string CopilotModel,
    bool HasConfiguredToken,
    IReadOnlyList<string> TokenEnvironmentVariables,
    CodeGitPolicyInfo Git);

public sealed record CodeGitPolicyInfo(
    bool AllowMutations,
    bool AllowNetworkOperations,
    bool RequireCleanWorkingTreeForMerge,
    int MaxDiffCharacters,
    int MaxLogCount,
    string DefaultRemoteName,
    bool HasConfiguredToken,
    IReadOnlyList<string> TokenEnvironmentVariables);

public sealed record CodeProjectSummary(
    string RootPath,
    IReadOnlyList<string> SolutionFiles,
    IReadOnlyList<string> ProjectFiles,
    IReadOnlyList<string> TopLevelDirectories,
    int CodeFileCount,
    long ApproximateBytes);

public sealed record CodeFileContent(
    string Path,
    string FullPath,
    string Content,
    long LengthBytes);

public sealed record CodeSearchResult(
    string Path,
    int Line,
    string Text);

public sealed record CodeSearchResults(IReadOnlyList<CodeSearchResult> Results, bool Truncated);

public sealed record CodeSuggestionResult(
    string Task,
    IReadOnlyList<string> Files,
    string Suggestion,
    string? Model,
    string? UsageJson);

public sealed record CodeWriteResult(string Path, string FullPath, long BytesWritten, bool CreatedDirectory);

public sealed record CodeErrorResult(string Code, string Message);

public sealed record GitRepositoryInfo(string RootPath, string WorkingDirectory, bool IsBare);

public sealed record GitStatusEntry(string Path, string State);

public sealed record GitStatusResult(string RepositoryRoot, string? HeadBranch, bool IsDetachedHead, bool IsDirty, IReadOnlyList<GitStatusEntry> Entries);

public sealed record GitDiffResult(string RepositoryRoot, string? Path, bool Staged, string Patch, bool Truncated);

public sealed record GitCommitInfo(string Sha, string ShortSha, string MessageShort, string AuthorName, string AuthorEmail, DateTimeOffset When);

public sealed record GitLogResult(string RepositoryRoot, IReadOnlyList<GitCommitInfo> Commits, bool Truncated);

public sealed record GitBranchInfo(string Name, string FriendlyName, string? UpstreamBranch, bool IsCurrentRepositoryHead, bool IsRemote, string? TipSha);

public sealed record GitBranchesResult(string RepositoryRoot, string? HeadBranch, IReadOnlyList<GitBranchInfo> Branches);

public sealed record GitOperationResult(string RepositoryRoot, string Operation, string Message, bool Success);

public sealed record GitCloneResult(string RepositoryRoot, string RemoteUrl, string? Branch);

public sealed record GitMergeResult(string RepositoryRoot, string Branch, string Status, string? CommitSha, IReadOnlyList<GitConflictInfo> Conflicts);

public sealed record GitConflictInfo(string Path, string? AncestorPath, string? OursPath, string? TheirsPath);

public sealed record GitPushResult(string RepositoryRoot, string RemoteName, string BranchName, bool SetUpstream, string Message);


