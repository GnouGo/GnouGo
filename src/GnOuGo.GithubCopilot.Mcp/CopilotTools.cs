using System.ComponentModel;
using System.Text.Json;
using GnOuGo.GithubCopilot.Core;
using GnOuGo.Mcp.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

[McpServerToolType]
internal sealed partial class CopilotTools
{
    private const string ProjectRootDescription = "Required workspace-relative path to an existing project root outside the reserved .GnOuGo internal directory. Pass a documented workspace.directory artifact output or a caller-provided existing directory.";
    private const string ManagementOnlyMetadataJson = """{"management":{"version":1,"visibility":"management_only"}}""";
    private const string ManagedSessionCreateMetadataJson = """{"artifacts":{"version":1,"produces":[{"kind":"session.handle","pointer":"/handle","mode":"materialize"}],"consumes":[{"kind":"workspace.directory","pointer":"/projectRoot","required":true}]}}""";
    private const string SessionHandleConsumerMetadataJson = """{"artifacts":{"version":1,"consumes":[{"kind":"session.handle","pointer":"/handle","required":true}]}}""";
    internal const string CompleteOneShotMetadataJson = """{"artifacts":{"version":1,"consumes":[{"kind":"workspace.directory","pointer":"/projectRoot","required":true}]},"composition":{"version":1,"kind":"complete_operation","encapsulates":[{"kind":"tool","method":"copilot_session_create"},{"kind":"tool","method":"copilot_session_send"},{"kind":"tool","method":"copilot_session_disconnect"},{"kind":"tool","method":"copilot_session_delete"}]}}""";

    private readonly CopilotSessionManager _sessions;
    private readonly CopilotLogicalOperations _logical;
    private readonly CopilotMcpConfiguration _configuration;
    private readonly CodePolicy _policy;
    private readonly CodeServerSettings _settings;
    private readonly CodeMcpTraceContextAccessor _traceContext;
    private readonly McpCopilotHumanInputProvider _humanInput;
    private readonly CodeProgressReporter _progress;
    private readonly ICopilotPermissionGrantStore _permissionGrants;

    public CopilotTools(
        CopilotSessionManager sessions,
        CodePolicy policy,
        IOptions<CodeServerSettings> settings,
        CodeMcpTraceContextAccessor traceContext,
        McpCopilotHumanInputProvider humanInput,
        CodeProgressReporter progress,
        ICopilotPermissionGrantStore permissionGrants,
        CopilotMcpConfiguration configuration,
        CopilotLogicalOperations logical)
    {
        _sessions = sessions;
        _logical = logical;
        _configuration = configuration;
        _policy = policy;
        _settings = settings.Value;
        _traceContext = traceContext;
        _humanInput = humanInput;
        _progress = progress;
        _permissionGrants = permissionGrants;
    }

    [McpServerTool(Name = "copilot_get_capabilities", UseStructuredContent = true, OutputSchemaType = typeof(CopilotCapabilityCatalogResult)), Description("Returns the strict generally-available Copilot SDK capability allowlist and stable MCP protocol revisions. Preview, experimental, remote/cloud, fleet, fork, canvas, extension, manual-compaction, and unknown RPC APIs are excluded.")]
    public CopilotCapabilityCatalogResult GetCapabilities() => GaCapabilityCatalog.Describe();

    [McpServerTool(Name = "copilot_permission_grants_list", UseStructuredContent = true, OutputSchemaType = typeof(CopilotPermissionGrantListResult)), Description("Management-only operation used by Agent.Server Configuration to list persistent future-agent Copilot permission grants for one tenant. Generated workflows must not call this tool.")]
    [McpMeta("gnougo", JsonValue = ManagementOnlyMetadataJson)]
    public async Task<CopilotPermissionGrantListResult> ListPermissionGrantsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = BuildContext();
            var grants = await _permissionGrants.ListFutureAgentGrantsAsync(context.TenantId, cancellationToken);
            return new CopilotPermissionGrantListResult(true, grants);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return new CopilotPermissionGrantListResult(false, [], "PERMISSION_GRANT_LIST_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "copilot_permission_grant_revoke", UseStructuredContent = true, OutputSchemaType = typeof(CopilotPermissionGrantOperationResult)), Description("Management-only operation used by Agent.Server Configuration to revoke one persistent Copilot permission grant. Generated workflows must not call this tool.")]
    [McpMeta("gnougo", JsonValue = ManagementOnlyMetadataJson)]
    public async Task<CopilotPermissionGrantOperationResult> RevokePermissionGrantAsync(
        string grantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = BuildContext();
            var revoked = await _permissionGrants.RevokeAsync(context.TenantId, grantId, cancellationToken);
            if (revoked)
                ReportGrantRevoked($"Revoked persistent Copilot permission grant '{grantId}'.");
            return revoked
                ? new CopilotPermissionGrantOperationResult(true, grantId, 1)
                : new CopilotPermissionGrantOperationResult(false, grantId, 0, "PERMISSION_GRANT_NOT_FOUND", "The permission grant was not found for this tenant.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return new CopilotPermissionGrantOperationResult(false, grantId, 0, "PERMISSION_GRANT_REVOKE_FAILED", ex.Message);
        }
    }

    [McpServerTool(Name = "copilot_permission_grants_revoke_agent", UseStructuredContent = true, OutputSchemaType = typeof(CopilotPermissionGrantOperationResult)), Description("Management-only operation used by Agent.Server when deleting an agent. Revokes that agent's persistent Copilot permission grants for the tenant. Generated workflows must not call this tool.")]
    [McpMeta("gnougo", JsonValue = ManagementOnlyMetadataJson)]
    public async Task<CopilotPermissionGrantOperationResult> RevokeAgentPermissionGrantsAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var context = BuildContext();
            var count = await _permissionGrants.RevokeAgentAsync(context.TenantId, agentId, cancellationToken);
            if (count > 0)
                ReportGrantRevoked($"Revoked {count} persistent Copilot permission grant(s) for agent '{agentId}'.");
            return new CopilotPermissionGrantOperationResult(true, RevokedCount: count);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return new CopilotPermissionGrantOperationResult(false, RevokedCount: 0, ErrorCode: "PERMISSION_GRANT_REVOKE_FAILED", ErrorMessage: ex.Message);
        }
    }

    [McpServerTool(Name = "copilot_connectivity", UseStructuredContent = true, OutputSchemaType = typeof(CopilotConnectivityResult)), Description("Starts a temporary Copilot SDK client and verifies connectivity without creating a session.")]
    public Task<CopilotConnectivityResult> ConnectivityAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.PingAsync(BuildConfiguration(null), cancellationToken));

    [McpServerTool(Name = "copilot_status", UseStructuredContent = true, OutputSchemaType = typeof(CopilotStatusResult)), Description("Returns stable Copilot CLI/SDK status and protocol information.")]
    public Task<CopilotStatusResult> StatusAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.GetStatusAsync(BuildConfiguration(null), cancellationToken));

    [McpServerTool(Name = "copilot_auth_status", UseStructuredContent = true, OutputSchemaType = typeof(CopilotAuthResult)), Description("Returns Copilot authentication status without exposing credentials.")]
    public Task<CopilotAuthResult> AuthStatusAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.GetAuthStatusAsync(BuildConfiguration(null), cancellationToken));

    [McpServerTool(Name = "copilot_list_models", UseStructuredContent = true, OutputSchemaType = typeof(IReadOnlyList<CopilotModelResult>)), Description("Lists models available to the configured Copilot identity.")]
    public Task<IReadOnlyList<CopilotModelResult>> ListModelsAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.ListModelsAsync(BuildConfiguration(null), cancellationToken));

    [McpServerTool(Name = "copilot_session_create", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSessionDescriptor)), Description("Creates a tenant-bound managed Copilot session and returns an opaque handle. MCP transport/session IDs are never used as Copilot session identity.")]
    [McpMeta(McpArtifactContractMetadata.MetaPropertyName, JsonValue = ManagedSessionCreateMetadataJson)]
    public Task<CopilotSessionDescriptor> CreateSessionAsync(
        RequestContext<CallToolRequestParams> requestContext,
        [Description(ProjectRootDescription)] string projectRoot,
        [Description("Permission mode. interactive is the safe choice for work that may need user approval; auto_approve_allowlist permits only explicitly listed read-only operations; deny rejects tool execution; approve_all is host-policy gated and generated workflows must not select it unless unattended execution was explicitly requested and availability is established.")] CopilotManagedPermissionModeInput permissionMode = CopilotManagedPermissionModeInput.Interactive,
        [Description("Optional configured KeyVault-backed LLM provider name, for example OpenAi.")] string? provider = null,
        [Description("Optional model override. Provider configuration may select its own model.")] string? model = null,
        [Description("Optional array of nonblank read-only tool/path allowlist entries for auto_approve_allowlist.")] IReadOnlyList<string>? permissionAllowlist = null,
        [Description("Optional array of nonblank available Copilot tool names.")] IReadOnlyList<string>? availableTools = null,
        [Description("Optional array of nonblank excluded Copilot tool names.")] IReadOnlyList<string>? excludedTools = null,
        [Description("Optional array of nonblank skill directories inside the project.")] IReadOnlyList<string>? skillDirectories = null,
        [Description("Optional array of nonblank disabled skill names.")] IReadOnlyList<string>? disabledSkills = null,
        [Description("Enable stable Copilot configuration discovery for MCP servers, skills, and repository instructions. Keep false for isolated reviews.")] bool enableConfigDiscovery = false,
        [Description("Enable response streaming events. Raw reasoning is never returned.")] bool streaming = false,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => _sessions.CreateAsync(
            new CopilotSessionCreateRequest(
                BuildContext(),
                BuildConfiguration(projectRoot, provider, model, permissionAllowlist, availableTools, excludedTools, skillDirectories, disabledSkills, enableConfigDiscovery),
                CopilotSessionKind.Managed,
                ToCorePermissionMode(permissionMode),
                streaming),
            cancellationToken));

    [McpServerTool(Name = "copilot_session_resume", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSessionDescriptor)), Description("Reconnects a disconnected managed Copilot session through its opaque tenant-bound handle.")]
    public Task<CopilotSessionDescriptor> ResumeSessionAsync(
        RequestContext<CallToolRequestParams> requestContext,
        string handle,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => _sessions.ResumeAsync(new CopilotSessionResumeRequest(BuildContext(), handle), cancellationToken));

    [McpServerTool(Name = "copilot_session_list", UseStructuredContent = true, OutputSchemaType = typeof(IReadOnlyList<CopilotSessionDescriptor>)), Description("Lists non-expired managed session handles owned by the tenant.")]
    public IReadOnlyList<CopilotSessionDescriptor> ListSessions()
        => _sessions.List(BuildContext().TenantId);

    [McpServerTool(Name = "copilot_session_get_configuration", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSessionConfigurationResult)), Description("Returns the tenant-owned session's non-secret tool, skill, MCP discovery, hook, permission, and elicitation configuration. Credentials are never returned.")]
    public CopilotSessionConfigurationResult GetSessionConfiguration(string handle)
        => _sessions.DescribeConfiguration(BuildContext(), handle);

    [McpServerTool(Name = "copilot_session_disconnect", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Disconnects a managed session while preserving resumable Copilot state until its TTL expires.")]
    [McpMeta(McpArtifactContractMetadata.MetaPropertyName, JsonValue = SessionHandleConsumerMetadataJson)]
    public Task<CopilotOperationResult> DisconnectSessionAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.DisconnectAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_delete", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Permanently deletes a tenant-owned Copilot session and its persisted SDK state.")]
    [McpMeta(McpArtifactContractMetadata.MetaPropertyName, JsonValue = SessionHandleConsumerMetadataJson)]
    public Task<CopilotOperationResult> DeleteSessionAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.DeleteAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_send", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSendResult)), Description("Sends a serialized message to a managed session. deliveryMode enqueue queues a turn; immediate steers an active turn. Streaming progress excludes raw model reasoning.")]
    [McpMeta(McpArtifactContractMetadata.MetaPropertyName, JsonValue = SessionHandleConsumerMetadataJson)]
    public Task<CopilotSendResult> SendAsync(
        RequestContext<CallToolRequestParams> requestContext,
        string handle,
        string prompt,
        string deliveryMode = "enqueue",
        string? agentMode = null,
        [Description(CopilotAttachmentContract.Description)] IReadOnlyList<CopilotAttachmentInput>? attachments = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, cancellationToken, () => SendWithProgressAsync(
            new CopilotSendRequest(BuildContext(), handle, prompt, deliveryMode, agentMode, CopilotAttachmentContract.ToCore(attachments)),
            cancellationToken));

    [McpServerTool(Name = "copilot_one_shot", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSendResult)), Description("Runs one non-interactive call in one ephemeral Copilot session, then disconnects and permanently deletes it. The deny default is appropriate for inference that needs no tool execution. Use copilot_interactive_one_shot for work that may install dependencies, run commands, edit files, or otherwise require user permission. approve_all is host-policy gated and generated workflows must not select it unless unattended execution was explicitly requested and availability is established.")]
    [McpMeta(McpArtifactContractMetadata.MetaPropertyName, JsonValue = CompleteOneShotMetadataJson)]
    public Task<CopilotSendResult> OneShotAsync(
        RequestContext<CallToolRequestParams> requestContext,
        [Description(ProjectRootDescription)] string projectRoot,
        string prompt,
        [Description("Non-interactive permission mode. deny is the safe default; auto_approve_allowlist permits only entries supplied in permissionAllowlist; approve_all is host-policy gated and must not be selected by generated workflows without explicit unattended intent and established availability.")] CopilotOneShotPermissionModeInput permissionMode = CopilotOneShotPermissionModeInput.Deny,
        string? provider = null,
        string? model = null,
        [Description("Optional array of nonblank read-only tool/path allowlist entries used only with auto_approve_allowlist.")] IReadOnlyList<string>? permissionAllowlist = null,
        [Description(CopilotAttachmentContract.Description)] IReadOnlyList<CopilotAttachmentInput>? attachments = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => OneShotWithProgressAsync(
            new CopilotSessionCreateRequest(BuildContext(), BuildConfiguration(projectRoot, provider, model, permissionAllowlist), CopilotSessionKind.OneShot, ToCorePermissionMode(permissionMode)),
            prompt,
            CopilotAttachmentContract.ToCore(attachments),
            cancellationToken));

    [McpServerTool(Name = "copilot_interactive_one_shot", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSendResult)), Description("Runs one bounded logical MCP task with interactive permission and elicitation callbacks. A verified terminal result is saved before the final SDK session is deleted. Verified failure returns an MCP error with partial evidence; unknown external completion retains the session for reconciliation and prohibits automatic replay. Use this capability for work that may install dependencies, run commands, edit files, or otherwise require user permission.")]
    [McpMeta(McpArtifactContractMetadata.MetaPropertyName, JsonValue = CompleteOneShotMetadataJson)]
    public Task<CopilotSendResult> InteractiveOneShotAsync(
        RequestContext<CallToolRequestParams> requestContext,
        [Description(ProjectRootDescription)] string projectRoot,
        string prompt,
        string? provider = null,
        string? model = null,
        [Description("Optional array of nonblank read-only tool/path allowlist entries retained in the ephemeral managed session configuration.")] IReadOnlyList<string>? permissionAllowlist = null,
        [Description(CopilotAttachmentContract.Description)] IReadOnlyList<CopilotAttachmentInput>? attachments = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, cancellationToken, () => InteractiveOneShotWithProgressAsync(
            requestContext.Server,
            new CopilotSessionCreateRequest(
                BuildContext(),
                BuildConfiguration(projectRoot, provider, model, permissionAllowlist),
                CopilotSessionKind.Managed,
                CopilotPermissionMode.Interactive),
            prompt,
            CopilotAttachmentContract.ToCore(attachments),
            cancellationToken));

    [McpServerTool(Name = "copilot_session_history", UseStructuredContent = true, OutputSchemaType = typeof(IReadOnlyList<CopilotHistoryEvent>)), Description("Returns safe session history. Reasoning event content is always removed.")]
    public Task<IReadOnlyList<CopilotHistoryEvent>> HistoryAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.GetHistoryAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_abort", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Aborts the currently active turn without deleting the managed session.")]
    public Task<CopilotOperationResult> AbortAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.AbortAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_set_model", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Switches the stable model/reasoning-effort settings for subsequent turns while preserving history.")]
    public Task<CopilotOperationResult> SetModelAsync(string handle, string model, string? reasoningEffort = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.SetModelAsync(BuildContext(), handle, model, reasoningEffort, cancellationToken));

    [McpServerTool(Name = "copilot_session_get_mode"), Description("Returns the stable session mode: interactive, plan, or autopilot.")]
    public Task<string> GetModeAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.GetModeAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_set_mode", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Sets the stable session mode to interactive, plan, or autopilot.")]
    public Task<CopilotOperationResult> SetModeAsync(string handle, string mode, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.SetModeAsync(BuildContext(), handle, mode, cancellationToken));

    [McpServerTool(Name = "copilot_plan_read", UseStructuredContent = true, OutputSchemaType = typeof(CopilotPlanResult)), Description("Reads the stable session plan file.")]
    public Task<CopilotPlanResult> ReadPlanAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.ReadPlanAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_plan_update", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Replaces the stable session plan content.")]
    public Task<CopilotOperationResult> UpdatePlanAsync(string handle, string content, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.UpdatePlanAsync(BuildContext(), handle, content, cancellationToken));

    [McpServerTool(Name = "copilot_plan_delete", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Deletes the stable session plan file.")]
    public Task<CopilotOperationResult> DeletePlanAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.DeletePlanAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_get_foreground", UseStructuredContent = true, OutputSchemaType = typeof(CopilotForegroundResult)), Description("Returns the foreground managed session only when it is owned by the requesting tenant.")]
    public Task<CopilotForegroundResult> GetForegroundAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.GetForegroundAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_set_foreground", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Moves a connected tenant-owned managed session to the foreground.")]
    public Task<CopilotOperationResult> SetForegroundAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.SetForegroundAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_workspace_list_files", UseStructuredContent = true, OutputSchemaType = typeof(IReadOnlyList<string>)), Description("Lists relative files in the stable Copilot session workspace.")]
    public Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(string handle, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.ListWorkspaceFilesAsync(BuildContext(), handle, cancellationToken));

    [McpServerTool(Name = "copilot_workspace_read_file", UseStructuredContent = true, OutputSchemaType = typeof(CopilotWorkspaceFileResult)), Description("Reads a relative file from the stable Copilot session workspace.")]
    public Task<CopilotWorkspaceFileResult> ReadWorkspaceFileAsync(string handle, string path, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.ReadWorkspaceFileAsync(BuildContext(), handle, path, cancellationToken));

    [McpServerTool(Name = "copilot_workspace_create_file", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Creates or replaces a relative file in the stable Copilot session workspace. It never writes to the user checkout.")]
    public Task<CopilotOperationResult> CreateWorkspaceFileAsync(string handle, string path, string content, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.CreateWorkspaceFileAsync(BuildContext(), handle, path, content, cancellationToken));

    private CopilotRuntimeConfiguration BuildConfiguration(
        string? projectRoot,
        string? provider = null,
        string? model = null,
        IReadOnlyList<string>? permissionAllowlist = null,
        IReadOnlyList<string>? availableTools = null,
        IReadOnlyList<string>? excludedTools = null,
        IReadOnlyList<string>? skillDirectories = null,
        IReadOnlyList<string>? disabledSkills = null,
        bool enableConfigDiscovery = false)
    {
        return _configuration.Build(projectRoot, provider, model) with
        {
            PermissionAllowlist = CopilotListContract.Normalize(permissionAllowlist, nameof(permissionAllowlist)),
            AvailableTools = CopilotListContract.Normalize(availableTools, nameof(availableTools)),
            ExcludedTools = CopilotListContract.Normalize(excludedTools, nameof(excludedTools)),
            SkillDirectories = CopilotListContract.Normalize(skillDirectories, nameof(skillDirectories)),
            DisabledSkills = CopilotListContract.Normalize(disabledSkills, nameof(disabledSkills)),
            EnableConfigDiscovery = enableConfigDiscovery
        };
    }

    private CopilotRequestContext BuildContext() => _configuration.Context();

    private async Task<T> WithServerAsync<T>(RequestContext<CallToolRequestParams> requestContext, Func<Task<T>> action)
    {
        using var scope = _humanInput.Push(requestContext.Server);
        return await ExecuteAsync(action);
    }

    private async Task<T> WithServerAsync<T>(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken,
        Func<Task<T>> action)
    {
        using var scope = _humanInput.Push(requestContext.Server, cancellationToken);
        return await ExecuteAsync(action);
    }

    private async Task<CopilotSendResult> SendWithProgressAsync(CopilotSendRequest request, CancellationToken cancellationToken)
    {
        var result = await _sessions.SendAsync(request with { RequestHeaders = _configuration.RequestHeaders(), Progress = CaptureProgress("copilot_session_send") }, cancellationToken);
        return result;
    }

    private async Task<CopilotSendResult> OneShotWithProgressAsync(
        CopilotSessionCreateRequest request,
        string prompt,
        IReadOnlyList<CopilotAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var result = await _sessions.OneShotAsync(request, prompt, attachments, cancellationToken, CaptureProgress("copilot_one_shot"), _configuration.RequestHeaders());
        return result;
    }

    private async Task<CopilotSendResult> InteractiveOneShotWithProgressAsync(
        McpServer server,
        CopilotSessionCreateRequest request,
        string prompt,
        IReadOnlyList<CopilotAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var result = await _logical.RunAsync(request, prompt, attachments, server,
            CaptureProgress("copilot_interactive_one_shot"), _configuration.RequestHeaders(), cancellationToken);
        return result;
    }

    private Action<CopilotStreamEvent> CaptureProgress(string method)
    {
        var reporter = _progress.Capture();
        return progressEvent => reporter.Report(
            progressEvent.Kind,
            progressEvent.Level,
            progressEvent.Message,
            fallbackServer: "GnOuGo.GithubCopilot.Mcp",
            fallbackMethod: method,
            fallbackMcpKind: "tool");
    }

    private void ReportGrantRevoked(string message)
        => _progress.Report(
            "permission.grant.revoked",
            "info",
            message,
            fallbackServer: "GnOuGo.GithubCopilot.Mcp",
            fallbackMethod: "copilot_permission_grant_revoke",
            fallbackMcpKind: "tool");

    private static async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or UnauthorizedAccessException or IOException or JsonException)
        {
            throw new McpException(ex.Message, ex);
        }
    }

    private static CopilotPermissionMode ToCorePermissionMode(CopilotManagedPermissionModeInput mode)
        => mode switch
        {
            CopilotManagedPermissionModeInput.Interactive => CopilotPermissionMode.Interactive,
            CopilotManagedPermissionModeInput.AutoApproveAllowlist => CopilotPermissionMode.AutoApproveAllowlist,
            CopilotManagedPermissionModeInput.Deny => CopilotPermissionMode.Deny,
            CopilotManagedPermissionModeInput.ApproveAll => CopilotPermissionMode.ApproveAll,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported managed Copilot permission mode.")
        };

    private static CopilotPermissionMode ToCorePermissionMode(CopilotOneShotPermissionModeInput mode)
        => mode switch
        {
            CopilotOneShotPermissionModeInput.AutoApproveAllowlist => CopilotPermissionMode.AutoApproveAllowlist,
            CopilotOneShotPermissionModeInput.Deny => CopilotPermissionMode.Deny,
            CopilotOneShotPermissionModeInput.ApproveAll => CopilotPermissionMode.ApproveAll,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported one-shot Copilot permission mode.")
        };

}
