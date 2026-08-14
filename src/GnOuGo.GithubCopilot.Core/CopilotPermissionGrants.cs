using System.Text.Json.Serialization;

namespace GnOuGo.GithubCopilot.Core;

[JsonConverter(typeof(JsonStringEnumConverter<CopilotPermissionGrantScope>))]
public enum CopilotPermissionGrantScope
{
    [JsonStringEnumMemberName("current_task")]
    CurrentTask,

    [JsonStringEnumMemberName("workflow_run")]
    WorkflowRun,

    [JsonStringEnumMemberName("future_agent_runs")]
    FutureAgentRuns
}

public sealed record CopilotPermissionGrant(
    string Id,
    string TenantId,
    CopilotPermissionGrantScope Scope,
    string? ExecutionId,
    string? AgentId,
    string? AgentName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset? ExpiresAt = null,
    bool AllowSandboxBypass = false);

public sealed record CopilotPermissionGrantListResult(
    bool Success,
    IReadOnlyList<CopilotPermissionGrant> Grants,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record CopilotPermissionGrantOperationResult(
    bool Success,
    string? GrantId = null,
    int RevokedCount = 0,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface ICopilotPermissionGrantStore
{
    Task<CopilotPermissionGrant?> FindReusableGrantAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken);

    Task<CopilotPermissionGrant> GrantWorkflowRunAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken);

    Task<CopilotPermissionGrant> GrantFutureAgentRunsAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CopilotPermissionGrant>> ListFutureAgentGrantsAsync(
        string tenantId,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(
        string tenantId,
        string grantId,
        CancellationToken cancellationToken);

    Task<int> RevokeAgentAsync(
        string tenantId,
        string agentId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional capability implemented by permission stores that can remember an
/// explicit sandbox-bypass approval. Keeping this separate preserves compatibility
/// with stores that intentionally support only ordinary broad approvals.
/// </summary>
public interface ICopilotSandboxBypassPermissionGrantStore
{
    Task<CopilotPermissionGrant?> FindReusableSandboxBypassGrantAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken);

    Task<CopilotPermissionGrant> GrantWorkflowRunWithSandboxBypassAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken);

    Task<CopilotPermissionGrant> GrantFutureAgentRunsWithSandboxBypassAsync(
        CopilotRequestContext context,
        CancellationToken cancellationToken);
}

public sealed record CopilotPermissionEvent(
    string Kind,
    string Level,
    string Message,
    string OperationKind,
    CopilotPermissionGrantScope? Scope,
    CopilotRequestContext Context,
    bool Automatic)
{
    public bool SandboxBypass { get; init; }
}

public interface ICopilotPermissionEventSink
{
    void Report(CopilotPermissionEvent permissionEvent);
}
