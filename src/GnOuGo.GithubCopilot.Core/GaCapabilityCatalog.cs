namespace GnOuGo.GithubCopilot.Core;

public static class GaCapabilityCatalog
{
    public const string McpPackageVersion = "2.0.0";
    public const string RequiredMcpRevision = "2026-07-28";
    public const string FallbackMcpRevision = "2025-11-25";
    public const string CopilotSdkPackageVersion = "1.0.8";

    private static readonly CopilotCapability[] Allowed =
    [
        new("connectivity", "CopilotClient.PingAsync"),
        new("status", "CopilotClient.GetStatusAsync"),
        new("authentication", "CopilotClient.GetAuthStatusAsync"),
        new("models", "CopilotClient.ListModelsAsync"),
        new("session.create", "CopilotClient.CreateSessionAsync"),
        new("session.resume", "CopilotClient.ResumeSessionAsync"),
        new("session.list", "CopilotClient.ListSessionsAsync"),
        new("session.foreground", "CopilotClient.GetForegroundSessionIdAsync/SetForegroundSessionIdAsync"),
        new("session.disconnect", "CopilotSession.DisposeAsync"),
        new("session.delete", "CopilotClient.DeleteSessionAsync"),
        new("message.send", "CopilotSession.SendAndWaitAsync"),
        new("message.stream", "CopilotSession.On<SessionEvent>"),
        new("message.steer", "MessageOptions.Mode=immediate"),
        new("message.queue", "MessageOptions.Mode=enqueue"),
        new("message.attachments", "MessageOptions.Attachments"),
        new("history", "CopilotSession.GetEventsAsync"),
        new("abort", "CopilotSession.AbortAsync"),
        new("model.switch", "CopilotSession.SetModelAsync"),
        new("mode", "CopilotSession.Mode.GetAsync/SetAsync"),
        new("plan", "CopilotSession.Plan.ReadAsync/UpdateAsync/DeleteAsync"),
        new("workspace.files", "CopilotSession.Rpc.Workspaces"),
        new("skills", "SessionConfig.SkillDirectories/DisabledSkills"),
        new("hooks", "SessionConfig.Hooks"),
        new("permissions", "SessionConfig.OnPermissionRequest"),
        new("user.input", "SessionConfig.OnUserInputRequest"),
        new("elicitation", "SessionConfig.OnElicitationRequest"),
        new("mcp.configuration", "SessionConfig.McpServers")
    ];

    private static readonly string[] Excluded =
    [
        "experimental", "preview", "insiders", "fleet", "fork", "remote-session",
        "cloud-sandbox", "cloud", "canvas", "extension", "manual-compaction",
        "history-truncate", "agent-management", "citations", "unclassified-rpc"
    ];

    public static CopilotCapabilityCatalogResult Describe()
        => new(CopilotSdkPackageVersion, McpPackageVersion, RequiredMcpRevision, FallbackMcpRevision, Allowed, Excluded);

    public static bool IsAllowed(string capability)
        => Allowed.Any(item => string.Equals(item.Name, capability, StringComparison.OrdinalIgnoreCase));

    public static void RequireAllowed(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability) || !IsAllowed(capability))
            throw new InvalidOperationException($"Copilot capability '{capability}' is not classified as generally available and is therefore disabled.");
    }
}
