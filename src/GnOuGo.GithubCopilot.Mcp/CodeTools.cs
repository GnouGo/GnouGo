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

    [McpServerTool(Name = "code_project_summary"), Description("Summarizes and verifies an existing project root: solution files, project files, top-level directories, and approximate allowed code file counts. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object GetProjectSummary([Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot = null)
        => Execute(() => _projectService.GetSummary(projectRoot));

    [McpServerTool(Name = "code_read_file"), Description("Reads one allowlisted text/code file inside an existing project root. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object ReadFile(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("File path relative to the existing projectRoot, for example 'src/Program.cs'.")] string relativePath)
        => Execute(() => _projectService.ReadFile(projectRoot, relativePath));

    [McpServerTool(Name = "code_search_text"), Description("Searches text in allowlisted files inside an existing project root. Omit projectRoot or pass null to search within the default workspace; empty string is invalid.")]
    public object SearchText(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("Literal text to search for.")] string query,
        [Description("Optional filename glob, for example *.cs. Directory globs are intentionally ignored for safety.")] string? glob = null,
        [Description("Whether matching is case-sensitive.")] bool caseSensitive = false)
        => Execute(() => _projectService.Search(projectRoot, query, glob, caseSensitive));

    [McpServerTool(Name = "code_suggest_change"), Description("Asks GitHub Copilot/GitHub Models for a code-change plan or patch suggestion inside an existing project root. This tool does not write files. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public async Task<object> SuggestChangeAsync(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. If the workflow cloned a repository first, pass git_clone.response.projectRootRelative. Do not pass a planned clone target before git_clone succeeds.")] string? projectRoot,
        [Description("Coding task to perform.")] string task,
        [Description("Optional JSON array of file paths relative to the existing projectRoot, for example [\"src/App.cs\"].")] string? contextFilesJson = null,
        [Description("Optional configured LLM provider name. When provided, Code:Copilot:Providers:<name> configures a custom Copilot provider for this call.")] string? provider = null,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(async () =>
        {
            var contextFiles = ParseContextFiles(contextFilesJson);
            var files = _projectService.ReadContextFiles(projectRoot, contextFiles);
            var resolvedRoot = _projectService.GetSummary(projectRoot).RootPath;
            return await _assistantClient.SuggestChangeAsync(task, resolvedRoot, files, provider, cancellationToken);
        });

    [McpServerTool(Name = "code_agent_edit"), Description("Runs GitHub Copilot SDK in agent mode with controlled file editing inside an existing project root. Requires Code:AllowWrites=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public async Task<object> AgentEditAsync(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. If the workflow cloned a repository first, pass git_clone.response.projectRootRelative. Do not pass a planned clone target before git_clone succeeds.")] string? projectRoot,
        [Description("Coding task to implement by editing files.")] string task,
        [Description("Optional JSON array of file paths relative to the existing projectRoot, for example [\"src/App.cs\"].")] string? contextFilesJson = null,
        [Description("Optional configured LLM provider name. When provided, Code:Copilot:Providers:<name> configures a custom Copilot provider for this call.")] string? provider = null,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(async () =>
        {
            var contextFiles = ParseContextFiles(contextFilesJson);
            var files = _projectService.ReadContextFiles(projectRoot, contextFiles);
            var resolvedRoot = _projectService.GetSummary(projectRoot).RootPath;
            return await _assistantClient.AgentEditAsync(task, resolvedRoot, files, provider, cancellationToken);
        });

    [McpServerTool(Name = "code_write_file"), Description("Writes one allowlisted text/code file inside an existing project root. Disabled unless Code:AllowWrites=true. Omit projectRoot or pass null to use the default workspace; empty string is invalid.")]
    public object WriteFile(
        [Description("Existing project root relative to the workspace, or null/omitted to use the default workspace. Empty string is invalid. Use git_clone.response.projectRootRelative after cloning; do not invent this path before it exists.")] string? projectRoot,
        [Description("File path relative to the existing projectRoot, for example 'src/NewFile.cs'.")] string relativePath,
        [Description("UTF-8 text content to write.")] string content)
        => Execute(() => _projectService.WriteFile(projectRoot, relativePath, content));

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
