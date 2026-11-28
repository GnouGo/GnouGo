using System.ComponentModel;
using System.Text.Json;
using GnOuGo.GithubCopilot.Core;
using System.Text.Json.Serialization;

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
    string? RootPathRelative = null,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;

    [Description("Workspace-relative path to an existing project root. Pass this value to MCP projectRoot inputs; do not use absolute RootPath.")]
    public string? ProjectRootRelative => RootPathRelative;
}

public sealed record CodeFileContent(
    string Path,
    string FullPath,
    string Content,
    long LengthBytes,
    string? RelativePath = null,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
}

public sealed record CodeSearchResult(
    string Path,
    int Line,
    string Text);

public sealed record CodeSearchResults(
    IReadOnlyList<CodeSearchResult> Results,
    bool Truncated,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
}

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
    IReadOnlyList<CodeProgressEvent> ProgressEvents = null!,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
}

public sealed record CodeAgentEditResult(
    [property: Description("Original input instructions echoed for traceability.")]
    string Task,
    IReadOnlyList<string> ContextFiles,
    IReadOnlyList<string> ModifiedFiles,
    [property: Description("Copilot's returned text. Read success and error_code for completion, and commandResults for observed commands and exit codes.")]
    string Summary,
    string? Model,
    string? UsageJson,
    [property: Description("Progress stream for diagnostics. Use summary and toolExecutions for downstream results.")]
    IReadOnlyList<CodeProgressEvent> ProgressEvents = null!,
    string? Output = null,
    string? TraceId = null,
    string? CorrelationId = null,
    string? TraceParent = null,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    [Description("SDK tool observations including original arguments and terminal exit codes when observed. A missing exit code remains unknown.")]
    public IReadOnlyList<CopilotToolExecutionObservation> ToolExecutions { get; init; } = [];
    [Description("Ordered commands, working directories and verified exit codes for reporting. Bind this field with summary when raw terminal output is unnecessary. A null exitCode means unverified completion. Older receipts may omit commandResults.")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CodeCommandResult>? CommandResults { get; init; }
    public bool Ok => Success;
}

public sealed record CodeCommandResult(string ToolCallId, string? Command, string? WorkingDirectory, long? ExitCode, string? SourceToolCallId)
{
    internal static IReadOnlyList<CodeCommandResult> FromObservations(IReadOnlyList<CopilotToolExecutionObservation> observations)
    {
        var results = new List<CodeCommandResult>();
        foreach (var call in observations)
        {
            var command = ReadCommand(call.ArgumentsJson);
            if (call.IsShellCommand != true && (command is null || call.Terminals.Count == 0)) continue;
            var terminals = call.GetVerifiedTerminals(observations);
            if (command is null || terminals is not { Count: > 0 })
                results.Add(new(call.ToolCallId, command, null, null, null));
            else
                foreach (var terminal in terminals)
                    results.Add(new(call.ToolCallId, command, terminal.WorkingDirectory, terminal.ExitCode, terminal.SourceToolCallId));
        }
        return results;
    }

    private static string? ReadCommand(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments)) return null;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "command", "cmd", "script" })
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}

public sealed record CodeWriteResult(
    string Path,
    string FullPath,
    long BytesWritten,
    bool CreatedDirectory,
    string? RelativePath = null,
    bool Success = true,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("error_message")] string? ErrorMessage = null)
{
    public bool Ok => Success;
}

public sealed record CodeErrorResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

internal sealed record CodeUsageInfo(long? OutputTokens, string? RequestId, string? InteractionId);
