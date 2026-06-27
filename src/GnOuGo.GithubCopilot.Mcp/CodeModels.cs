using System.ComponentModel;

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
    long ApproximateBytes,
    string? RootPathRelative = null)
{
    [Description("Workspace-relative existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RootPath.")]
    public string? ProjectRootRelative => RootPathRelative;
}

public sealed record CodeFileContent(
    string Path,
    string FullPath,
    string Content,
    long LengthBytes,
    string? RelativePath = null);

public sealed record CodeSearchResult(
    string Path,
    int Line,
    string Text);

public sealed record CodeSearchResults(IReadOnlyList<CodeSearchResult> Results, bool Truncated);

/// <summary>
/// Stable GnOuGo progress contract emitted by code tools and consumed by Flow/UI.
/// SDK-specific events, when available, must be mapped to this schema before leaving this MCP server.
/// </summary>
public sealed record CodeProgressEvent(
    string Kind,
    string Level,
    string Message,
    DateTimeOffset Timestamp,
    string? File = null);

internal sealed record CodeMcpProgressEnvelope(
    string Type,
    string? CorrelationId,
    string? RunId,
    string? StepId,
    string? StepType,
    string? Server,
    string? Method,
    string? Kind,
    CodeProgressEvent Event);

public sealed record CodeSuggestionResult(
    string Task,
    IReadOnlyList<string> Files,
    string Suggestion,
    string? Model,
    string? UsageJson,
    IReadOnlyList<CodeProgressEvent> ProgressEvents = null!);

public sealed record CodeAgentEditResult(
    string Task,
    IReadOnlyList<string> ContextFiles,
    IReadOnlyList<string> ModifiedFiles,
    string Summary,
    string? Model,
    string? UsageJson,
    IReadOnlyList<CodeProgressEvent> ProgressEvents = null!,
    string? Output = null,
    string? TraceId = null,
    string? CorrelationId = null,
    string? TraceParent = null);

public sealed record CodeWriteResult(
    string Path,
    string FullPath,
    long BytesWritten,
    bool CreatedDirectory,
    string? RelativePath = null);

public sealed record CodeErrorResult(string Code, string Message);

internal sealed record CodeUsageInfo(long? OutputTokens, string? RequestId, string? InteractionId);

