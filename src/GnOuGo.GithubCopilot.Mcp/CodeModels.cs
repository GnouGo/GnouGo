namespace GnOuGo.GithubCopilot.Mcp;

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
    string CopilotMode,
    bool CopilotForwardTraceContext,
    bool CopilotTelemetryEnabled,
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

public sealed record CodeAgentEditResult(
    string Task,
    IReadOnlyList<string> ContextFiles,
    IReadOnlyList<string> ModifiedFiles,
    string Summary,
    string? Model,
    string? UsageJson);

public sealed record CodeWriteResult(string Path, string FullPath, long BytesWritten, bool CreatedDirectory);

public sealed record CodeErrorResult(string Code, string Message);



