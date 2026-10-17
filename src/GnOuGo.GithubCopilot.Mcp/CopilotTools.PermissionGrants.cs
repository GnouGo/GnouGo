using System.ComponentModel;
using GnOuGo.GithubCopilot.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed partial class CopilotTools
{
    [McpServerTool(Name = "copilot_permission_grant_create", UseStructuredContent = true, OutputSchemaType = typeof(CopilotPermissionGrantOperationResult)),
     Description("Management-only operation for Agent.Server Configuration. Requests explicit human confirmation before storing persistent permission approval for one selected agent. Never use from a generated workflow.")]
    [McpMeta("gnougo", JsonValue = ManagementOnlyMetadataJson)]
    public Task<CopilotPermissionGrantOperationResult> CreatePermissionGrantAsync(
        RequestContext<CallToolRequestParams> requestContext,
        string agentId,
        string agentName,
        bool allowSandboxBypass = false,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, cancellationToken,
            () => ConfirmPermissionGrantAsync(agentId, agentName, allowSandboxBypass, _humanInput, cancellationToken));

    internal async Task<CopilotPermissionGrantOperationResult> ConfirmPermissionGrantAsync(
        string agentId, string agentName, bool allowSandboxBypass, ICopilotHumanInputProvider human, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        var context = BuildContext() with { AgentId = agentId, AgentName = agentName };
        if (!_settings.Copilot.EnableApproveAll || allowSandboxBypass && !_settings.Copilot.EnableSandboxBypassGrants)
            return new(false, ErrorCode: "PERMISSION_GRANT_DISABLED", ErrorMessage: "The required Copilot host permission gates are disabled.");
        if (allowSandboxBypass && _permissionGrants is not ICopilotSandboxBypassPermissionGrantStore)
            return new(false, ErrorCode: "PERMISSION_GRANT_UNSUPPORTED", ErrorMessage: "This permission store does not support sandbox-bypass grants.");

        var confirmation = allowSandboxBypass ? "Confirm persistent sandbox-bypass approval" : "Confirm persistent approval";
        // This management confirmation never goes through the auto-approval callback.
        var response = await human.RequestAsync(new(context, "permission_persistence_confirmation",
            "Persist broad Copilot approval for this agent?", [confirmation, "Cancel"], false,
            $"Agent: {agentName}\nAgent ID: {agentId}\nTenant: {context.TenantId}\n"
            + "Applies from the first permission request in future runs, survives restarts, and remains active until revoked.\n"
            + (allowSandboxBypass ? "Includes sandbox-bypass requests. " : "Sandbox-bypass requests still require approval. ")
            + "Host filesystem restrictions and approved task boundaries remain enforced."), ct);
        ct.ThrowIfCancellationRequested();
        if (!response.Accepted || !string.Equals(response.Answer, confirmation, StringComparison.Ordinal))
            return new(false, ErrorCode: "PERMISSION_GRANT_CANCELLED", ErrorMessage: "Persistent approval was cancelled; no grant was created.");

        var grant = allowSandboxBypass
            ? await ((ICopilotSandboxBypassPermissionGrantStore)_permissionGrants).GrantFutureAgentRunsWithSandboxBypassAsync(context, ct)
            : await _permissionGrants.GrantFutureAgentRunsAsync(context, ct);
        _progress.Report("permission.grant.created", "warning", $"Persistent Copilot approval saved for agent '{agentName}' ({agentId}); sandbox bypass: {allowSandboxBypass}.",
            fallbackServer: "GnOuGo.GithubCopilot.Mcp", fallbackMethod: "copilot_permission_grant_create", fallbackMcpKind: "tool");
        return new(true, grant.Id);
    }
}
