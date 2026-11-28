using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.Configuration;

namespace GnOuGo.Flow.Copilot;

public static class CopilotTaskRunners
{
    public static WorkflowEngine WithCopilotRunners(this WorkflowEngine engine, CopilotRunnerConfiguration configuration)
        => engine.WithCopilotRunners(configuration.Runners, configuration.Tools, configuration.Host);

    /// <summary>Host-owned bindings select one executor before dispatch.</summary>
    public static WorkflowEngine WithCopilotRunners(this WorkflowEngine engine, IEnumerable<KeyValuePair<string, string>> runners)
        => engine.WithCopilotRunners(runners, null);

    public static WorkflowEngine WithCopilotRunners(this WorkflowEngine engine, IEnumerable<KeyValuePair<string, string>> runners,
        IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>>? allowedTools, IConfiguration? host = null, IAgentTaskRunner? codeRunner = null)
    {
        foreach (var (name, server) in runners)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentException.ThrowIfNullOrWhiteSpace(server);
            var tools = allowedTools?.GetValueOrDefault(name);
            IAgentTaskRunner runner = (server, tools) switch
            {
                ("business" or "GnOuGo.GithubCopilot.Mcp", not null) => new BusinessAgentTaskRunner(
                    engine.McpClientFactory ?? throw new InvalidOperationException("Business tools require a configured MCP transport."), tools),
                ("code" or "GnOuGo.GithubCopilot.Mcp", null) => codeRunner ?? new CopilotAgentTaskRunner(host ?? new ConfigurationBuilder().AddEnvironmentVariables().Build()),
                _ => throw new ArgumentException("The agent profile and configured tool scope are incompatible.")
            };
            engine.AgentTaskRunners.Add(name, runner);
        }
        return engine;
    }
}
