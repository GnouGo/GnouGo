using GnOuGo.GithubCopilot.Core;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class McpCopilotPermissionEventSink(CodeProgressReporter progress) : ICopilotPermissionEventSink
{
    public void Report(CopilotPermissionEvent permissionEvent)
    {
        progress.Report(
            permissionEvent.Kind,
            permissionEvent.Level,
            BuildActivityMessage(permissionEvent),
            fallbackServer: "GnOuGo.GithubCopilot.Mcp",
            fallbackMethod: "copilot_interactive_one_shot",
            fallbackMcpKind: "tool");
    }

    internal static string BuildActivityMessage(CopilotPermissionEvent permissionEvent)
    {
        var context = permissionEvent.Context;
        var correlation = new List<string>
        {
            "operation=" + Safe(permissionEvent.OperationKind),
            "decision=" + DecisionName(permissionEvent),
            "scope=" + ScopeName(permissionEvent.Scope),
            "sandbox_bypass=" + permissionEvent.SandboxBypass.ToString().ToLowerInvariant()
        };

        Add(correlation, "agent", context.AgentName ?? context.AgentId);
        Add(correlation, "repository", context.Repository);
        Add(correlation, "execution", context.ExecutionId);
        Add(correlation, "run", context.RunId);
        Add(correlation, "step", context.StepId);
        return $"{permissionEvent.Message} [{string.Join("; ", correlation)}]";
    }

    private static string DecisionName(CopilotPermissionEvent permissionEvent)
        => permissionEvent.Automatic
            ? "automatic_reuse"
            : permissionEvent.Kind switch
            {
                "permission.granted" => "human_approved",
                "permission.refused" => "human_refused",
                "permission.grant.revoked" => "human_revoked",
                _ => "human_pending"
            };

    private static string ScopeName(CopilotPermissionGrantScope? scope)
        => scope switch
        {
            CopilotPermissionGrantScope.CurrentTask => "current_task",
            CopilotPermissionGrantScope.WorkflowRun => "workflow_run",
            CopilotPermissionGrantScope.FutureAgentRuns => "future_agent_runs",
            _ => "once"
        };

    private static void Add(List<string> values, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(name + "=" + Safe(value));
    }

    private static string Safe(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Replace(';', ',').Trim();
        return sanitized.Length <= 160 ? sanitized : sanitized[..160] + "...";
    }
}
