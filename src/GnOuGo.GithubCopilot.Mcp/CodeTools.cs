using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

[McpServerToolType]
public sealed class CodeTools
{
    private const string RequiredProjectRootDescription = "Required workspace-relative path to an existing project root. Null, omitted, empty, absolute, file URI, home-relative, and parent-traversal values are invalid. After git_clone succeeds, pass git_clone.response.projectRootRelative; do not invent this path before it exists.";
    private const string RequiredProjectRootToolSuffix = " projectRoot is required and must be a non-empty workspace-relative existing project root; pass git_clone.response.projectRootRelative after cloning.";

    private readonly CodeProjectService _projectService;
    private readonly ICodeAssistantClient _assistantClient;
    private readonly ILogger<CodeTools> _logger;

    public CodeTools(CodeProjectService projectService, ICodeAssistantClient assistantClient, ILogger<CodeTools> logger)
    {
        _projectService = projectService;
        _assistantClient = assistantClient;
        _logger = logger;
    }

    [McpServerTool(Name = "code_get_policy", UseStructuredContent = true, OutputSchemaType = typeof(CodePolicyInfo)), Description("Returns the active code MCP policy: allowed roots/extensions, write mode, limits, and Copilot/GitHub Models auth source status. Call this first to discover the default workspace.")]
    public CodePolicyInfo GetPolicy() => _projectService.GetPolicy();

    [McpServerTool(Name = "code_project_summary", UseStructuredContent = true, OutputSchemaType = typeof(CodeProjectSummary)), Description("Summarizes and verifies an existing project root: solution files, project files, top-level directories, and approximate allowed code file counts." + RequiredProjectRootToolSuffix)]
    public CodeProjectSummary GetProjectSummary([Description(RequiredProjectRootDescription)] string projectRoot)
        => Execute("code_project_summary", () => _projectService.GetSummary(projectRoot));

    [McpServerTool(Name = "code_read_file", UseStructuredContent = true, OutputSchemaType = typeof(CodeFileContent)), Description("Reads one allowlisted text/code file inside an existing project root." + RequiredProjectRootToolSuffix)]
    public CodeFileContent ReadFile(
        [Description(RequiredProjectRootDescription)] string projectRoot,
        [Description("File path relative to the existing projectRoot, for example 'src/Program.cs'.")] string relativePath)
        => Execute("code_read_file", () => _projectService.ReadFile(projectRoot, relativePath));

    [McpServerTool(Name = "code_search_text", UseStructuredContent = true, OutputSchemaType = typeof(CodeSearchResults)), Description("Searches text in allowlisted files inside an existing project root." + RequiredProjectRootToolSuffix)]
    public CodeSearchResults SearchText(
        [Description(RequiredProjectRootDescription)] string projectRoot,
        [Description("Literal text to search for.")] string query,
        [Description("Optional filename glob, for example *.cs. Directory globs are intentionally ignored for safety.")] string? glob = null,
        [Description("Whether matching is case-sensitive.")] bool caseSensitive = false)
        => Execute("code_search_text", () => _projectService.Search(projectRoot, query, glob, caseSensitive));

    [McpServerTool(Name = "code_suggest_change", UseStructuredContent = true, OutputSchemaType = typeof(CodeSuggestionResult)), Description("Asks GitHub Copilot/GitHub Models for a code-change plan or patch suggestion inside an existing project root. This tool does not write files." + RequiredProjectRootToolSuffix)]
    public async Task<CodeSuggestionResult> SuggestChangeAsync(
        [Description(RequiredProjectRootDescription)] string projectRoot,
        [Description("Coding task to perform.")] string task,
        [Description("Optional JSON array of file paths relative to the existing projectRoot, for example [\"src/App.cs\"].")] string? contextFilesJson = null,
        [Description("Optional configured LLM provider name. When provided, Code:Copilot:Providers:<name> configures a custom Copilot provider for this call.")] string? provider = null,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync("code_suggest_change", async () =>
        {
            var contextFiles = ParseContextFiles(contextFilesJson);
            var files = _projectService.ReadContextFiles(projectRoot, contextFiles);
            var resolvedRoot = _projectService.GetSummary(projectRoot).RootPath;
            return await _assistantClient.SuggestChangeAsync(task, resolvedRoot, files, provider, cancellationToken);
        });

    [McpServerTool(Name = "code_agent_edit", UseStructuredContent = true, OutputSchemaType = typeof(CodeAgentEditResult)), Description("Runs GitHub Copilot SDK in agent mode with controlled file editing inside an existing project root. Requires Code:AllowWrites=true." + RequiredProjectRootToolSuffix)]
    public async Task<CodeAgentEditResult> AgentEditAsync(
        [Description(RequiredProjectRootDescription)] string projectRoot,
        [Description("Coding task to implement by editing files.")] string task,
        [Description("Optional JSON array of file paths relative to the existing projectRoot, for example [\"src/App.cs\"].")] string? contextFilesJson = null,
        [Description("Optional configured LLM provider name. When provided, Code:Copilot:Providers:<name> configures a custom Copilot provider for this call.")] string? provider = null,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync("code_agent_edit", async () =>
        {
            var contextFiles = ParseContextFiles(contextFilesJson);
            var files = _projectService.ReadContextFiles(projectRoot, contextFiles);
            var resolvedRoot = _projectService.GetSummary(projectRoot).RootPath;
            return await _assistantClient.AgentEditAsync(task, resolvedRoot, files, provider, cancellationToken);
        });

    [McpServerTool(Name = "code_write_file", UseStructuredContent = true, OutputSchemaType = typeof(CodeWriteResult)), Description("Writes one allowlisted text/code file inside an existing project root. Disabled unless Code:AllowWrites=true." + RequiredProjectRootToolSuffix)]
    public CodeWriteResult WriteFile(
        [Description(RequiredProjectRootDescription)] string projectRoot,
        [Description("File path relative to the existing projectRoot, for example 'src/NewFile.cs'.")] string relativePath,
        [Description("UTF-8 text content to write.")] string content)
        => Execute("code_write_file", () => _projectService.WriteFile(projectRoot, relativePath, content));

    private T Execute<T>(string toolName, Func<T> action)
    {
        try
        {
            return action()!;
        }
        catch (McpException ex)
        {
            _logger.LogWarning(ex, "Code MCP tool error for {ToolName}", toolName);
            return CodeToolFailure.Create<T>(toolName, "MCP_TOOL_ERROR", ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Code MCP tool policy/input error");
            return CodeToolFailure.Create<T>(toolName, CodeToolFailure.Classify(ex), ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Code MCP tool unexpected error");
            return CodeToolFailure.Create<T>(toolName, "INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<T> ExecuteAsync<T>(string toolName, Func<Task<T>> action)
    {
        try
        {
            return (await action())!;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Code MCP async tool cancelled for {ToolName}", toolName);
            return CodeToolFailure.Create<T>(toolName, "CANCELLED", "The operation was cancelled by the client.");
        }
        catch (McpException ex)
        {
            _logger.LogWarning(ex, "Code MCP async tool error for {ToolName}", toolName);
            return CodeToolFailure.Create<T>(toolName, "MCP_TOOL_ERROR", ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Code MCP async tool policy/input/provider error");
            return CodeToolFailure.Create<T>(toolName, CodeToolFailure.Classify(ex), ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Code MCP async tool unexpected error");
            return CodeToolFailure.Create<T>(toolName, "INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static IReadOnlyList<string> ParseContextFiles(string? contextFilesJson)
    {
        if (string.IsNullOrWhiteSpace(contextFilesJson))
            return [];
        var values = JsonSerializer.Deserialize(contextFilesJson, CodeMcpJsonContext.Default.ListString);
        if (values is null)
            return [];

        var result = new List<string>();
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && !result.Contains(value, StringComparer.OrdinalIgnoreCase))
                result.Add(value);
        }

        return result;
    }
}

internal static class CodeToolFailure
{
    public static string Classify(Exception exception)
        => exception switch
        {
            ArgumentException or InvalidOperationException or JsonException => "INVALID_INPUT",
            FileNotFoundException or DirectoryNotFoundException => "FILE_NOT_FOUND",
            UnauthorizedAccessException => "ACCESS_DENIED",
            HttpRequestException => "PROVIDER_ERROR",
            IOException => "IO_ERROR",
            _ => "INTERNAL_ERROR"
        };

    public static T Create<T>(string toolName, string errorCode, string errorMessage)
    {
        object result = typeof(T) switch
        {
            var type when type == typeof(CodeProjectSummary) => new CodeProjectSummary(
                RootPath: string.Empty,
                SolutionFiles: [],
                ProjectFiles: [],
                TopLevelDirectories: [],
                CodeFileCount: 0,
                ApproximateBytes: 0,
                Success: false,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage),

            var type when type == typeof(CodeFileContent) => new CodeFileContent(
                Path: string.Empty,
                FullPath: string.Empty,
                Content: string.Empty,
                LengthBytes: 0,
                Success: false,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage),

            var type when type == typeof(CodeSearchResults) => new CodeSearchResults(
                Results: [],
                Truncated: false,
                Success: false,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage),

            var type when type == typeof(CodeSuggestionResult) => new CodeSuggestionResult(
                Task: toolName,
                Files: [],
                Suggestion: string.Empty,
                Model: null,
                UsageJson: null,
                ProgressEvents: [],
                Success: false,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage),

            var type when type == typeof(CodeAgentEditResult) => new CodeAgentEditResult(
                Task: toolName,
                ContextFiles: [],
                ModifiedFiles: [],
                Summary: errorMessage,
                Model: null,
                UsageJson: null,
                ProgressEvents: [],
                Output: errorMessage,
                Success: false,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage),

            var type when type == typeof(CodeWriteResult) => new CodeWriteResult(
                Path: string.Empty,
                FullPath: string.Empty,
                BytesWritten: 0,
                CreatedDirectory: false,
                Success: false,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage),

            _ => throw new InvalidOperationException($"Code MCP cannot create a structured error result for {typeof(T).Name}.")
        };

        return (T)result;
    }
}

internal static class CodeMcpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Insert(0, CodeMcpJsonContext.Default);
        return options;
    }
}

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(CodePolicyInfo))]
[JsonSerializable(typeof(CodeProjectSummary))]
[JsonSerializable(typeof(CodeFileContent))]
[JsonSerializable(typeof(CodeSearchResult))]
[JsonSerializable(typeof(IReadOnlyList<CodeSearchResult>))]
[JsonSerializable(typeof(CodeSearchResults))]
[JsonSerializable(typeof(CodeProgressEvent))]
[JsonSerializable(typeof(CodeMcpProgressEnvelope))]
[JsonSerializable(typeof(IReadOnlyList<CodeProgressEvent>))]
[JsonSerializable(typeof(CodeSuggestionResult))]
[JsonSerializable(typeof(CodeAgentEditResult))]
[JsonSerializable(typeof(CodeWriteResult))]
[JsonSerializable(typeof(CodeErrorResult))]
[JsonSerializable(typeof(CodeUsageInfo))]
[JsonSerializable(typeof(CodeServerSettings))]
[JsonSerializable(typeof(CodeCopilotSettings))]
[JsonSerializable(typeof(CodeCopilotTelemetrySettings))]
internal sealed partial class CodeMcpJsonContext : JsonSerializerContext;
