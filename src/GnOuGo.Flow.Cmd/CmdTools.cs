using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace GnOuGo.Flow.Cmd;

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

    [McpServerTool(Name = "cmd_list_allowed_commands"), Description("Lists the allowlisted commands that this secure command MCP server is allowed to execute.")]
    public CmdAllowedCommandsResult ListAllowedCommands()
        => new(_host.ListAllowedCommands());

    [McpServerTool(Name = "cmd_get_policy"), Description("Returns the active command execution policy, including shells, roots and execution limits.")]
    public CmdPolicyInfo GetPolicy()
        => _host.GetPolicy();

    [McpServerTool(Name = "cmd_run"), Description("Runs one allowlisted command by name. Raw shell commands are not accepted; only preconfigured aliases may be executed.")]
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "cmd_run failed for command={CommandName}", commandName);
            throw;
        }
    }
}

public sealed record CmdAllowedCommandsResult(IReadOnlyList<CmdAllowedCommandInfo> Commands);
