using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Copilot;

public static class CopilotTaskRunners
{
    /// <summary>Explicit host configuration maps approved runner identifiers to MCP server identifiers.</summary>
    public static WorkflowEngine WithCopilotRunners(this WorkflowEngine engine, IEnumerable<KeyValuePair<string, string>> runners)
    {
        foreach (var (name, server) in runners)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentException.ThrowIfNullOrWhiteSpace(server);
            engine.AgentTaskRunners.Add(name, new CopilotAgentTaskRunner(engine.McpClientFactory ?? throw new InvalidOperationException("Configure MCP transport before agent runners."), server));
        }
        return engine;
    }
}
