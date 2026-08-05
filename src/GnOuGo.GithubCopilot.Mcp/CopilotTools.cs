using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GnOuGo.GithubCopilot.Mcp;

[McpServerToolType]
internal sealed class CopilotTools
{
    private const string ReviewProjectRootDescription = "Required workspace-relative path to an existing checked-out project root. Pass a documented repository-materialization capability output such as projectRootRelative; a URL, repository identifier, or invented path is invalid.";
    private const string ReviewFilesJsonDescription = "Required JSON array of per-file exact comparison patches returned by a documented revision-comparison capability. A raw aggregate diff or invented file list is invalid.";

    private readonly CopilotSessionManager _sessions;
    private readonly CopilotReviewManager _reviews;
    private readonly CodePolicy _policy;
    private readonly CodeServerSettings _settings;
    private readonly CodeMcpTraceContextAccessor _traceContext;
    private readonly McpCopilotHumanInputProvider _humanInput;
    private readonly CodeProgressReporter _progress;

    public CopilotTools(
        CopilotSessionManager sessions,
        CopilotReviewManager reviews,
        CodePolicy policy,
        IOptions<CodeServerSettings> settings,
        CodeMcpTraceContextAccessor traceContext,
        McpCopilotHumanInputProvider humanInput,
        CodeProgressReporter progress)
    {
        _sessions = sessions;
        _reviews = reviews;
        _policy = policy;
        _settings = settings.Value;
        _traceContext = traceContext;
        _humanInput = humanInput;
        _progress = progress;
    }

    [McpServerTool(Name = "copilot_get_capabilities", UseStructuredContent = true, OutputSchemaType = typeof(CopilotCapabilityCatalogResult)), Description("Returns the strict generally-available Copilot SDK capability allowlist and stable MCP protocol revisions. Preview, experimental, remote/cloud, fleet, fork, canvas, extension, manual-compaction, and unknown RPC APIs are excluded.")]
    public CopilotCapabilityCatalogResult GetCapabilities() => GaCapabilityCatalog.Describe();

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
    public Task<CopilotSessionDescriptor> CreateSessionAsync(
        RequestContext<CallToolRequestParams> requestContext,
        [Description("Required workspace-relative existing project root.")] string projectRoot,
        [Description("Permission mode: interactive, auto_approve_allowlist, deny, or approve_all. approve_all is host-policy gated.")] string permissionMode = "interactive",
        [Description("Optional configured KeyVault-backed LLM provider name, for example OpenAi.")] string? provider = null,
        [Description("Optional model override. Provider configuration may select its own model.")] string? model = null,
        [Description("Optional JSON array of read-only tool/path allowlist entries for auto_approve_allowlist.")] string? permissionAllowlistJson = null,
        [Description("Optional JSON array of available Copilot tool names.")] string? availableToolsJson = null,
        [Description("Optional JSON array of excluded Copilot tool names.")] string? excludedToolsJson = null,
        [Description("Optional JSON array of skill directories inside the project.")] string? skillDirectoriesJson = null,
        [Description("Optional JSON array of disabled skill names.")] string? disabledSkillsJson = null,
        [Description("Enable stable Copilot configuration discovery for MCP servers, skills, and repository instructions. Keep false for isolated reviews.")] bool enableConfigDiscovery = false,
        [Description("Enable response streaming events. Raw reasoning is never returned.")] bool streaming = false,
        [Description("Optional explicit tenant id; normally supplied in request _meta.gnougo.tenantId.")] string? tenantId = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => _sessions.CreateAsync(
            new CopilotSessionCreateRequest(
                BuildContext(tenantId),
                BuildConfiguration(projectRoot, provider, model, permissionAllowlistJson, availableToolsJson, excludedToolsJson, skillDirectoriesJson, disabledSkillsJson, enableConfigDiscovery),
                CopilotSessionKind.Managed,
                ParsePermissionMode(permissionMode),
                streaming),
            cancellationToken));

    [McpServerTool(Name = "copilot_session_resume", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSessionDescriptor)), Description("Reconnects a disconnected managed Copilot session through its opaque tenant-bound handle.")]
    public Task<CopilotSessionDescriptor> ResumeSessionAsync(
        RequestContext<CallToolRequestParams> requestContext,
        string handle,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => _sessions.ResumeAsync(new CopilotSessionResumeRequest(BuildContext(tenantId), handle), cancellationToken));

    [McpServerTool(Name = "copilot_session_list", UseStructuredContent = true, OutputSchemaType = typeof(IReadOnlyList<CopilotSessionDescriptor>)), Description("Lists non-expired managed session handles owned by the tenant.")]
    public IReadOnlyList<CopilotSessionDescriptor> ListSessions(string? tenantId = null)
        => _sessions.List(BuildContext(tenantId).TenantId);

    [McpServerTool(Name = "copilot_session_get_configuration", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSessionConfigurationResult)), Description("Returns the tenant-owned session's non-secret tool, skill, MCP discovery, hook, permission, and elicitation configuration. Credentials are never returned.")]
    public CopilotSessionConfigurationResult GetSessionConfiguration(string handle, string? tenantId = null)
        => _sessions.DescribeConfiguration(BuildContext(tenantId), handle);

    [McpServerTool(Name = "copilot_session_disconnect", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Disconnects a managed session while preserving resumable Copilot state until its TTL expires.")]
    public Task<CopilotOperationResult> DisconnectSessionAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.DisconnectAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_delete", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Permanently deletes a tenant-owned Copilot session and its persisted SDK state.")]
    public Task<CopilotOperationResult> DeleteSessionAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.DeleteAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_send", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSendResult)), Description("Sends a serialized message to a managed session. deliveryMode enqueue queues a turn; immediate steers an active turn. Streaming progress excludes raw model reasoning.")]
    public Task<CopilotSendResult> SendAsync(
        RequestContext<CallToolRequestParams> requestContext,
        string handle,
        string prompt,
        string deliveryMode = "enqueue",
        string? agentMode = null,
        [Description("Optional JSON array of file/blob attachments using the stable attachment contract.")] string? attachmentsJson = null,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => SendWithProgressAsync(
            new CopilotSendRequest(BuildContext(tenantId), handle, prompt, deliveryMode, agentMode, ParseAttachments(attachmentsJson)),
            cancellationToken));

    [McpServerTool(Name = "copilot_one_shot", UseStructuredContent = true, OutputSchemaType = typeof(CopilotSendResult)), Description("Runs one call in one ephemeral Copilot session, then disconnects and permanently deletes it. Interactive callbacks are not supported in one-shot mode.")]
    public Task<CopilotSendResult> OneShotAsync(
        RequestContext<CallToolRequestParams> requestContext,
        string projectRoot,
        string prompt,
        string permissionMode = "deny",
        string? provider = null,
        string? model = null,
        string? attachmentsJson = null,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => OneShotWithProgressAsync(
            new CopilotSessionCreateRequest(BuildContext(tenantId), BuildConfiguration(projectRoot, provider, model), CopilotSessionKind.OneShot, ParsePermissionMode(permissionMode)),
            prompt,
            ParseAttachments(attachmentsJson),
            cancellationToken));

    [McpServerTool(Name = "copilot_session_history", UseStructuredContent = true, OutputSchemaType = typeof(IReadOnlyList<CopilotHistoryEvent>)), Description("Returns safe session history. Reasoning event content is always removed.")]
    public Task<IReadOnlyList<CopilotHistoryEvent>> HistoryAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.GetHistoryAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_abort", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Aborts the currently active turn without deleting the managed session.")]
    public Task<CopilotOperationResult> AbortAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.AbortAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_set_model", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Switches the stable model/reasoning-effort settings for subsequent turns while preserving history.")]
    public Task<CopilotOperationResult> SetModelAsync(string handle, string model, string? reasoningEffort = null, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.SetModelAsync(BuildContext(tenantId), handle, model, reasoningEffort, cancellationToken));

    [McpServerTool(Name = "copilot_session_get_mode"), Description("Returns the stable session mode: interactive, plan, or autopilot.")]
    public Task<string> GetModeAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.GetModeAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_set_mode", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Sets the stable session mode to interactive, plan, or autopilot.")]
    public Task<CopilotOperationResult> SetModeAsync(string handle, string mode, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.SetModeAsync(BuildContext(tenantId), handle, mode, cancellationToken));

    [McpServerTool(Name = "copilot_plan_read", UseStructuredContent = true, OutputSchemaType = typeof(CopilotPlanResult)), Description("Reads the stable session plan file.")]
    public Task<CopilotPlanResult> ReadPlanAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.ReadPlanAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_plan_update", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Replaces the stable session plan content.")]
    public Task<CopilotOperationResult> UpdatePlanAsync(string handle, string content, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.UpdatePlanAsync(BuildContext(tenantId), handle, content, cancellationToken));

    [McpServerTool(Name = "copilot_plan_delete", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Deletes the stable session plan file.")]
    public Task<CopilotOperationResult> DeletePlanAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.DeletePlanAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_get_foreground", UseStructuredContent = true, OutputSchemaType = typeof(CopilotForegroundResult)), Description("Returns the foreground managed session only when it is owned by the requesting tenant.")]
    public Task<CopilotForegroundResult> GetForegroundAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.GetForegroundAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_session_set_foreground", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Moves a connected tenant-owned managed session to the foreground.")]
    public Task<CopilotOperationResult> SetForegroundAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.SetForegroundAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_workspace_list_files", UseStructuredContent = true, OutputSchemaType = typeof(IReadOnlyList<string>)), Description("Lists relative files in the stable Copilot session workspace.")]
    public Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(string handle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.ListWorkspaceFilesAsync(BuildContext(tenantId), handle, cancellationToken));

    [McpServerTool(Name = "copilot_workspace_read_file", UseStructuredContent = true, OutputSchemaType = typeof(CopilotWorkspaceFileResult)), Description("Reads a relative file from the stable Copilot session workspace.")]
    public Task<CopilotWorkspaceFileResult> ReadWorkspaceFileAsync(string handle, string path, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.ReadWorkspaceFileAsync(BuildContext(tenantId), handle, path, cancellationToken));

    [McpServerTool(Name = "copilot_workspace_create_file", UseStructuredContent = true, OutputSchemaType = typeof(CopilotOperationResult)), Description("Creates or replaces a relative file in the stable Copilot session workspace. It never writes to the user checkout.")]
    public Task<CopilotOperationResult> CreateWorkspaceFileAsync(string handle, string path, string content, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _sessions.CreateWorkspaceFileAsync(BuildContext(tenantId), handle, path, content, cancellationToken));

    [McpServerTool(Name = "copilot_review_start", UseStructuredContent = true, OutputSchemaType = typeof(CopilotReviewSession)), Description("Starts a read-only, batched PR review in one managed ephemeral Copilot session. filesJson must contain exact Git MCP compare patches. Optional reviewInstructions are applied to every batch, and existingCommentsJson is used to suppress duplicate findings. Omit provider and model to use the host's configured KeyVault-backed default; do not copy them from code_get_policy.")]
    public Task<CopilotReviewSession> ReviewStartAsync(
        RequestContext<CallToolRequestParams> requestContext,
        [Description(ReviewProjectRootDescription)] string projectRoot,
        [Description("Required exact base revision identifier returned by repository or comparison metadata.")] string baseSha,
        [Description("Required exact head revision identifier returned by repository or comparison metadata.")] string headSha,
        [Description(ReviewFilesJsonDescription)] string filesJson,
        [Description("Optional caller review instructions applied to every batch; maximum 32000 characters.")] string? reviewInstructions = null,
        [Description("Optional JSON array of existing inline review comments with path, side, line range, body, and optional fingerprint.")] string? existingCommentsJson = null,
        [Description("Optional explicit configured KeyVault-backed provider override. Omit to use the host default; do not copy code_get_policy.provider.")] string? provider = null,
        [Description("Optional model override for an explicitly selected provider. Omit to use the host/provider default; do not copy code_get_policy.model.")] string? model = null,
        int maxBatchCharacters = 60_000,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => _reviews.StartAsync(
            new CopilotReviewStartRequest(
                BuildContext(tenantId),
                BuildConfiguration(projectRoot, provider, model),
                baseSha,
                headSha,
                ParseReviewFiles(filesJson),
                maxBatchCharacters,
                ReviewInstructions: reviewInstructions,
                ExistingComments: ParseExistingReviewComments(existingCommentsJson)),
            cancellationToken));

    [McpServerTool(Name = "copilot_review_analyze_batch", UseStructuredContent = true, OutputSchemaType = typeof(CopilotReviewAnalyzeResult)), Description("Analyzes one bounded review batch and returns only validated findings on exact diff lines.")]
    public Task<CopilotReviewAnalyzeResult> ReviewAnalyzeBatchAsync(
        RequestContext<CallToolRequestParams> requestContext,
        string reviewHandle,
        int batchIndex,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => _reviews.AnalyzeBatchAsync(BuildContext(tenantId), reviewHandle, batchIndex, cancellationToken));

    [McpServerTool(Name = "copilot_review_finish", UseStructuredContent = true, OutputSchemaType = typeof(CopilotReviewResult)), Description("Deduplicates validated findings, reports coverage/skips/truncation, then deletes the ephemeral Copilot review session.")]
    public Task<CopilotReviewResult> ReviewFinishAsync(string reviewHandle, string? tenantId = null, CancellationToken cancellationToken = default)
        => ExecuteAsync(() => _reviews.FinishAsync(BuildContext(tenantId), reviewHandle, cancellationToken));

    [McpServerTool(Name = "copilot_review", UseStructuredContent = true, OutputSchemaType = typeof(CopilotReviewResult)), Description("Runs all bounded PR review batches in one ephemeral Copilot session and permanently deletes session state afterward. Optional reviewInstructions are applied to every batch, and existingCommentsJson is used to suppress duplicate findings. Omit provider and model to use the host's configured KeyVault-backed default; do not copy them from code_get_policy.")]
    public Task<CopilotReviewResult> ReviewAsync(
        RequestContext<CallToolRequestParams> requestContext,
        [Description(ReviewProjectRootDescription)] string projectRoot,
        [Description("Required exact base revision identifier returned by repository or comparison metadata.")] string baseSha,
        [Description("Required exact head revision identifier returned by repository or comparison metadata.")] string headSha,
        [Description(ReviewFilesJsonDescription)] string filesJson,
        [Description("Optional caller review instructions applied to every batch; maximum 32000 characters.")] string? reviewInstructions = null,
        [Description("Optional JSON array of existing inline review comments with path, side, line range, body, and optional fingerprint.")] string? existingCommentsJson = null,
        [Description("Optional explicit configured KeyVault-backed provider override. Omit to use the host default; do not copy code_get_policy.provider.")] string? provider = null,
        [Description("Optional model override for an explicitly selected provider. Omit to use the host/provider default; do not copy code_get_policy.model.")] string? model = null,
        int maxBatchCharacters = 60_000,
        string? tenantId = null,
        CancellationToken cancellationToken = default)
        => WithServerAsync(requestContext, () => _reviews.ReviewAsync(
            new CopilotReviewStartRequest(
                BuildContext(tenantId),
                BuildConfiguration(projectRoot, provider, model),
                baseSha,
                headSha,
                ParseReviewFiles(filesJson),
                maxBatchCharacters,
                ReviewInstructions: reviewInstructions,
                ExistingComments: ParseExistingReviewComments(existingCommentsJson)),
            cancellationToken));

    [McpServerTool(Name = "copilot_review_publication_gate", UseStructuredContent = true, OutputSchemaType = typeof(ReviewPublicationGateResult)), Description("Makes the final fail-closed publication decision after the GitHub MCP re-reads the PR head SHA. dry_run never writes, interactive requires explicit approval, auto_comment can only submit COMMENT, and APPROVE is not representable.")]
    public ReviewPublicationGateResult ReviewPublicationGate(
        string expectedHeadSha,
        string currentHeadSha,
        ReviewPublicationPolicy publicationPolicy,
        int validatedFindingCount,
        bool humanApproved = false,
        ReviewSubmitEvent proposedEvent = ReviewSubmitEvent.Comment)
        => ReviewValidation.EvaluatePublication(new ReviewPublicationGateRequest(
            expectedHeadSha,
            currentHeadSha,
            publicationPolicy,
            validatedFindingCount,
            humanApproved,
            proposedEvent));

    private CopilotRuntimeConfiguration BuildConfiguration(
        string? projectRoot,
        string? provider = null,
        string? model = null,
        string? permissionAllowlistJson = null,
        string? availableToolsJson = null,
        string? excludedToolsJson = null,
        string? skillDirectoriesJson = null,
        string? disabledSkillsJson = null,
        bool enableConfigDiscovery = false)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(projectRoot) ? _policy.DefaultWorkingDirectory : _policy.ResolveProjectRoot(projectRoot);
        var providerName = string.IsNullOrWhiteSpace(provider)
            ? (string.Equals(_settings.Copilot.Provider, "Copilot", StringComparison.OrdinalIgnoreCase) ? null : _settings.Copilot.Provider)
            : provider.Trim();
        return new CopilotRuntimeConfiguration(
            workingDirectory,
            string.IsNullOrWhiteSpace(model) ? _settings.Copilot.Model : model.Trim(),
            _settings.Copilot.ReasoningEffort,
            providerName,
            _policy.ResolveConfiguredToken(),
            _settings.Copilot.UseLoggedInUser,
            _settings.Copilot.RequestTimeoutSeconds,
            _settings.Copilot.ManagedSessionTtlSeconds,
            _settings.Copilot.EnableApproveAll,
            ParseStringList(permissionAllowlistJson),
            ParseStringList(availableToolsJson),
            ParseStringList(excludedToolsJson),
            ParseStringList(skillDirectoriesJson),
            ParseStringList(disabledSkillsJson),
            McpServers: null,
            EnableConfigDiscovery: enableConfigDiscovery);
    }

    private CopilotRequestContext BuildContext(string? tenantId)
    {
        var trace = _traceContext.Current ?? CodeMcpTraceContext.Capture(_traceContext);
        var resolvedTenant = string.IsNullOrWhiteSpace(tenantId) ? trace?.TenantId : tenantId.Trim();
        if (string.IsNullOrWhiteSpace(resolvedTenant))
            throw new McpException("TenantId is required in _meta.gnougo.tenantId or the tenantId argument.");
        return new CopilotRequestContext(resolvedTenant, trace?.CorrelationId, trace?.RunId, trace?.StepId, trace?.Repository, trace?.PullRequestNumber, trace?.HeadSha);
    }

    private async Task<T> WithServerAsync<T>(RequestContext<CallToolRequestParams> requestContext, Func<Task<T>> action)
    {
        using var scope = _humanInput.Push(requestContext.Server);
        return await ExecuteAsync(action);
    }

    private async Task<CopilotSendResult> SendWithProgressAsync(CopilotSendRequest request, CancellationToken cancellationToken)
    {
        var result = await _sessions.SendAsync(request, cancellationToken);
        ReportProgress(result);
        return result;
    }

    private async Task<CopilotSendResult> OneShotWithProgressAsync(
        CopilotSessionCreateRequest request,
        string prompt,
        IReadOnlyList<CopilotAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        var result = await _sessions.OneShotAsync(request, prompt, attachments, cancellationToken);
        ReportProgress(result);
        return result;
    }

    private void ReportProgress(CopilotSendResult result)
    {
        foreach (var progressEvent in result.Events)
        {
            _progress.Report(
                progressEvent.Kind,
                progressEvent.Level,
                progressEvent.Message,
                fallbackServer: "GnOuGo.GithubCopilot.Mcp",
                fallbackMethod: "copilot_session_send",
                fallbackMcpKind: "tool");
        }
    }

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

    private static CopilotPermissionMode ParsePermissionMode(string mode)
        => mode.Trim().ToLowerInvariant() switch
        {
            "interactive" => CopilotPermissionMode.Interactive,
            "auto_approve_allowlist" => CopilotPermissionMode.AutoApproveAllowlist,
            "deny" => CopilotPermissionMode.Deny,
            "approve_all" => CopilotPermissionMode.ApproveAll,
            _ => throw new McpException("permissionMode must be interactive, auto_approve_allowlist, deny, or approve_all.")
        };

    private static IReadOnlyList<string>? ParseStringList(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize(json, CodeMcpJsonContext.Default.ListString)?.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<CopilotAttachment>? ParseAttachments(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize(json, CopilotCoreJsonContext.Default.IReadOnlyListCopilotAttachment);

    private static IReadOnlyList<ReviewFilePatch> ParseReviewFiles(string json)
        => JsonSerializer.Deserialize(json, CopilotCoreJsonContext.Default.IReadOnlyListReviewFilePatch)
           ?? throw new McpException("filesJson must be a JSON array of review file patches.");

    private static IReadOnlyList<ExistingReviewComment> ParseExistingReviewComments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)
            || string.Equals(json.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<ExistingReviewComment>();

        var root = JsonNode.Parse(json)
                   ?? throw new McpException("existingCommentsJson must contain JSON review comments or a documented comments envelope.");
        if (root is not JsonArray && root is not JsonObject)
            throw new McpException("existingCommentsJson must be a JSON array or object envelope containing review comments.");

        var comments = new List<ExistingReviewComment>();
        CollectExistingReviewComments(root, comments, null, null, null, null, depth: 0);
        if (comments.Count == 0 && root is JsonObject envelope
                                && ReadInteger(envelope, "totalCount", "total_count") is > 0)
        {
            throw new McpException("existingCommentsJson reports existing comments but none could be mapped to path/body review-comment fields.");
        }

        return comments;
    }

    private static void CollectExistingReviewComments(
        JsonNode node,
        List<ExistingReviewComment> comments,
        string? inheritedPath,
        ReviewDiffSide? inheritedSide,
        int? inheritedStartLine,
        int? inheritedEndLine,
        int depth)
    {
        if (depth > 8)
            return;
        if (node is JsonArray array)
        {
            foreach (var item in array.OfType<JsonNode>())
                CollectExistingReviewComments(item, comments, inheritedPath, inheritedSide, inheritedStartLine, inheritedEndLine, depth + 1);
            return;
        }
        if (node is not JsonObject obj)
            return;

        var path = ReadString(obj, "path", "filePath", "file_path") ?? inheritedPath;
        var side = ReadReviewSide(obj) ?? inheritedSide;
        var startLine = ReadInteger(obj, "startLine", "start_line", "originalStartLine", "original_start_line") ?? inheritedStartLine;
        var endLine = ReadInteger(obj, "endLine", "end_line", "line", "originalLine", "original_line") ?? inheritedEndLine;
        var body = ReadString(obj, "body", "bodyText", "body_text");
        if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(body))
        {
            comments.Add(new ExistingReviewComment(
                path,
                side,
                startLine,
                endLine,
                body,
                ReadString(obj, "fingerprint")));
        }

        foreach (var child in obj.Select(static property => property.Value).OfType<JsonNode>())
        {
            if (child is JsonArray or JsonObject)
                CollectExistingReviewComments(child, comments, path, side, startLine, endLine, depth + 1);
        }
    }

    private static ReviewDiffSide? ReadReviewSide(JsonObject obj)
    {
        var value = ReadString(obj, "side", "diffSide", "diff_side");
        return value?.Trim().ToUpperInvariant() switch
        {
            "LEFT" => ReviewDiffSide.Left,
            "RIGHT" => ReviewDiffSide.Right,
            _ => null
        };
    }

    private static string? ReadString(JsonObject obj, params string[] names)
    {
        var node = FindProperty(obj, names);
        return node is JsonValue scalar && scalar.TryGetValue<string>(out var value)
            ? value
            : null;
    }

    private static int? ReadInteger(JsonObject obj, params string[] names)
    {
        var node = FindProperty(obj, names);
        if (node is not JsonValue scalar)
            return null;
        if (scalar.TryGetValue<int>(out var value))
            return value;
        if (scalar.TryGetValue<long>(out var longValue) && longValue is >= int.MinValue and <= int.MaxValue)
            return (int)longValue;
        return scalar.TryGetValue<string>(out var text) && int.TryParse(text, out value) ? value : null;
    }

    private static JsonNode? FindProperty(JsonObject obj, IReadOnlyList<string> names)
    {
        foreach (var property in obj)
        foreach (var name in names)
        {
            if (string.Equals(NormalizeJsonPropertyName(property.Key), NormalizeJsonPropertyName(name), StringComparison.Ordinal))
                return property.Value;
        }
        return null;
    }

    private static string NormalizeJsonPropertyName(string value)
        => value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
}
