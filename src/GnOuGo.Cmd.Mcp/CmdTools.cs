using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace GnOuGo.Cmd.Mcp;

[McpServerToolType]
public sealed class CmdTools
{
    private readonly CommandExecutionHost _host;
    private readonly ILogger<CmdTools> _logger;

    public CmdTools(CommandExecutionHost host, ILogger<CmdTools> logger)
    {
        _host = host;
        _logger = logger;
    }

    [McpServerTool(Name = "cmd_list_allowed_commands"), Description("Lists the allowlisted commands that this secure command MCP server is allowed to execute. Takes no arguments. Output shape: { \"commands\": [ { \"name\": string, \"description\": string|null, \"shell\": string, \"workingDirectory\": string, \"workingDirectoryRelative\": string|null, \"parameters\": string[] } ] }. Each entry includes the exact commandName value to pass to cmd_run, its description, target shell, resolved working directory, relative working directory when available, and accepted parameters. Commands run within the default workspace.")]
    public CmdAllowedCommandsResult ListAllowedCommands()
        => new(_host.ListAllowedCommands());

    [McpServerTool(Name = "cmd_get_policy"), Description("Returns the active command execution policy. Takes no arguments. Output shape: { \"allowedShells\": string[], \"allowedWorkingRoots\": string[], \"allowedWorkingRootsRelative\": string[]|null, \"defaultWorkingDirectory\": string|null, \"defaultWorkingDirectoryRelative\": string|null, \"defaultTimeoutMs\": integer, \"maxTimeoutMs\": integer, \"maxOutputCharacters\": integer, \"allowedCommandCount\": integer, \"environment\": { \"operatingSystem\": string, \"architecture\": string, \"machineName\": string, \"availableShells\": [ { \"name\": string, \"available\": boolean, \"resolvedPath\": string|null } ] } }. Use it to discover execution limits; for workflow chaining prefer relative path fields over absolute roots.")]
    public CmdPolicyInfo GetPolicy()
        => _host.GetPolicy();


    [McpServerTool(Name = "cmd_run"), Description("Runs one allowlisted command by name. Raw shell commands are not accepted; only preconfigured aliases may be executed. Commands execute within the default workspace. Returns a structured result with stdout, stderr, exit code, success flag, and error details if any.")]
    public async Task<CmdRunResult> RunAsync(
        [Description("Allowlisted command alias to execute.")] string commandName,
        [Description("Optional JSON object string of named parameters, for example {\"path\":\"src\"}.")] string? parametersJson = null,
        [Description("Optional timeout override in milliseconds. It will be clamped by the server policy.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _host.RunAsync(commandName, parametersJson, timeoutMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("cmd_run cancelled for command={CommandName}", commandName);
            return CmdRunResult.FromError(commandName, "CANCELLED", "The operation was cancelled by the client.");
        }
        catch (InvalidOperationException ex)
        {
            // Policy violations (unknown command, bad parameters, shell not found, etc.)
            _logger.LogWarning(ex, "cmd_run policy violation for command={CommandName}", commandName);
            return CmdRunResult.FromError(commandName, "POLICY_VIOLATION", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "cmd_run unexpected error for command={CommandName}", commandName);
            return CmdRunResult.FromError(commandName, "INTERNAL_ERROR",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

public sealed record CmdAllowedCommandsResult(IReadOnlyList<CmdAllowedCommandInfo> Commands);
