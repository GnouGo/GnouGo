using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Copilot;

public static class CopilotTaskRunners
{
    /// <summary>Explicit host configuration maps approved runner identifiers to MCP server identifiers.</summary>
    public static WorkflowEngine WithCopilotRunners(this WorkflowEngine engine, IEnumerable<KeyValuePair<string, string>> runners)
        => engine.WithCopilotRunners(runners, null);

    public static WorkflowEngine WithCopilotRunners(this WorkflowEngine engine, IEnumerable<KeyValuePair<string, string>> runners,
        IReadOnlyDictionary<string, IReadOnlyList<CopilotToolBinding>>? allowedTools)
    {
        foreach (var (name, server) in runners)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name); ArgumentException.ThrowIfNullOrWhiteSpace(server);
            engine.AgentTaskRunners.Add(name, new CopilotAgentTaskRunner(engine.McpClientFactory ?? throw new InvalidOperationException("Configure MCP transport before agent runners."), server, allowedTools?.GetValueOrDefault(name)));
        }
        return engine;
    }
}
