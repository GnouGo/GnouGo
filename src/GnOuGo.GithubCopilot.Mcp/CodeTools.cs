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
    private readonly CodeProjectService _projectService;
    private readonly ICodeAssistantClient _assistantClient;
    private readonly ILogger<CodeTools> _logger;

    public CodeTools(CodeProjectService projectService, ICodeAssistantClient assistantClient, ILogger<CodeTools> logger)
    {
        _projectService = projectService;
        _assistantClient = assistantClient;
        _logger = logger;
    }

    [McpServerTool(Name = "code_get_policy"), Description("Returns the active code MCP policy: allowed roots/extensions, write mode, limits, and Copilot/GitHub Models auth source status. Call this first to discover the default workspace.")]
    public CodePolicyInfo GetPolicy() => _projectService.GetPolicy();

    [McpServerTool(Name = "code_project_summary"), Description("Summarizes a project root: solution files, project files, top-level directories, and approximate allowed code file counts. Use relative paths only; omit projectRoot to use the default workspace (recommended).")]
    public object GetProjectSummary([Description("Optional project root override. Omit or pass null to use the default workspace — only the default workspace is authorized.")] string? projectRoot = null)
        => Execute(() => _projectService.GetSummary(projectRoot));

    [McpServerTool(Name = "code_read_file"), Description("Reads one allowlisted text/code file inside the project root. Use a relative path from the workspace root; omit projectRoot to use the default workspace.")]
    public object ReadFile(
        [Description("Project root override or null to use the default workspace (recommended).")] string? projectRoot,
        [Description("Relative file path inside the project root, for example 'src/Program.cs'.")] string relativePath)
        => Execute(() => _projectService.ReadFile(projectRoot ?? string.Empty, relativePath));

    [McpServerTool(Name = "code_search_text"), Description("Searches text in allowlisted project files. Use a simple query string and optional filename glob such as *.cs or *.md. Omit projectRoot to search within the default workspace.")]
    public object SearchText(
        [Description("Project root override or null to use the default workspace (recommended).")] string? projectRoot,
        [Description("Literal text to search for.")] string query,
        [Description("Optional filename glob, for example *.cs. Directory globs are intentionally ignored for safety.")] string? glob = null,
        [Description("Whether matching is case-sensitive.")] bool caseSensitive = false)
        => Execute(() => _projectService.Search(projectRoot ?? string.Empty, query, glob, caseSensitive));

    [McpServerTool(Name = "code_suggest_change"), Description("Asks GitHub Copilot/GitHub Models for a code-change plan or patch suggestion using optional context files. This tool does not write files. Omit projectRoot to use the default workspace. Use relative paths for context files.")]
    public async Task<object> SuggestChangeAsync(
        [Description("Project root override or null to use the default workspace (recommended).")] string? projectRoot,
        [Description("Coding task to perform.")] string task,
        [Description("Optional JSON array of relative file paths to include as context, for example [\"src/App.cs\"]. Paths are relative to the workspace root.")] string? contextFilesJson = null,
        [Description("Optional configured LLM provider name. When provided, Code:Copilot:Providers:<name> configures a custom Copilot provider for this call.")] string? provider = null,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(async () =>
        {
            var root = projectRoot ?? string.Empty;
            var contextFiles = ParseContextFiles(contextFilesJson);
            var files = _projectService.ReadContextFiles(root, contextFiles);
            var resolvedRoot = _projectService.GetSummary(root).RootPath;
            return await _assistantClient.SuggestChangeAsync(task, resolvedRoot, files, provider, cancellationToken);
        });

    [McpServerTool(Name = "code_agent_edit"), Description("Runs GitHub Copilot SDK in agent mode with controlled file editing through the MCP policy. Requires Code:AllowWrites=true. Omit projectRoot to use the default workspace. Use relative paths for context files.")]
    public async Task<object> AgentEditAsync(
        [Description("Project root override or null to use the default workspace (recommended).")] string? projectRoot,
        [Description("Coding task to implement by editing files.")] string task,
        [Description("Optional JSON array of relative file paths to include as initial context, for example [\"src/App.cs\"]. Paths are relative to the workspace root.")] string? contextFilesJson = null,
        [Description("Optional configured LLM provider name. When provided, Code:Copilot:Providers:<name> configures a custom Copilot provider for this call.")] string? provider = null,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(async () =>
        {
            var root = projectRoot ?? string.Empty;
            var contextFiles = ParseContextFiles(contextFilesJson);
            var files = _projectService.ReadContextFiles(root, contextFiles);
            var resolvedRoot = _projectService.GetSummary(root).RootPath;
            return await _assistantClient.AgentEditAsync(task, resolvedRoot, files, provider, cancellationToken);
        });

    [McpServerTool(Name = "code_write_file"), Description("Writes one allowlisted text/code file inside the project root. Disabled unless Code:AllowWrites=true. Omit projectRoot to use the default workspace. Use a relative path for the file.")]
    public object WriteFile(
        [Description("Project root override or null to use the default workspace (recommended).")] string? projectRoot,
        [Description("Relative file path inside the project root, for example 'src/NewFile.cs'.")] string relativePath,
        [Description("UTF-8 text content to write.")] string content)
        => Execute(() => _projectService.WriteFile(projectRoot ?? string.Empty, relativePath, content));

    private object Execute<T>(Func<T> action)
    {
        try
        {
            return action()!;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Code MCP tool policy/input error");
            return new CodeErrorResult("POLICY_OR_INPUT_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Code MCP tool unexpected error");
            return new CodeErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<object> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return (await action())!;
        }
        catch (OperationCanceledException)
        {
            return new CodeErrorResult("CANCELLED", "The operation was cancelled by the client.");
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or UnauthorizedAccessException or IOException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Code MCP async tool policy/input/provider error");
            return new CodeErrorResult("POLICY_INPUT_OR_PROVIDER_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Code MCP async tool unexpected error");
            return new CodeErrorResult("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
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


