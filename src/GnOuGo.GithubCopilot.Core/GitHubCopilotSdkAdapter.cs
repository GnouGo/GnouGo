using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.RegularExpressions;

namespace GnOuGo.GithubCopilot.Core;

public sealed class GitHubCopilotSdkClientFactory : ICopilotSdkClientFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public GitHubCopilotSdkClientFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public ICopilotSdkClient Create(CopilotRuntimeConfiguration configuration)
    {
        var options = new CopilotClientOptions
        {
            WorkingDirectory = configuration.WorkingDirectory,
            GitHubToken = string.IsNullOrWhiteSpace(configuration.GitHubToken) ? null : configuration.GitHubToken,
            UseLoggedInUser = configuration.UseLoggedInUser,
            Environment = configuration.Environment,
            Logger = _loggerFactory.CreateLogger<GitHubCopilotSdkClient>()
        };
        return new GitHubCopilotSdkClient(
            new CopilotClient(options),
            configuration,
            _loggerFactory.CreateLogger<GitHubCopilotSdkClient>());
    }
}

internal sealed class GitHubCopilotSdkClient : ICopilotSdkClient
{
    internal const string PermissionAllowOnceChoice = "Allow once";
    internal const string PermissionRefuseChoice = "Refuse";
    internal const string PermissionAllowForSessionChoice = "Allow similar operations for this task";
    internal const string PermissionAllowAllTaskChoice = "Allow all for this Copilot task";
    internal const string PermissionAllowAllWorkflowChoice = "Allow all for this workflow run";
    internal const string PermissionAllowAllFutureAgentChoice = "Allow all future runs for this agent";
    internal const string PermissionConfirmFutureAgentChoice = "Confirm persistent approval";
    internal const string PermissionCancelFutureAgentChoice = "Cancel";

    private readonly CopilotClient _client;
    private readonly CopilotRuntimeConfiguration _configuration;
    private readonly ILogger _logger;
    private bool _started;

    public GitHubCopilotSdkClient(CopilotClient client, CopilotRuntimeConfiguration configuration, ILogger logger)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
    }

    public string ConnectionState => _started ? "connected" : "disconnected";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _client.StartAsync(cancellationToken);
        _started = true;
    }

    public async Task<CopilotConnectivityResult> PingAsync(CancellationToken cancellationToken)
    {
        var result = await _client.PingAsync("gnougo", cancellationToken);
        return new CopilotConnectivityResult(result.Message, result.Timestamp.ToString("O"), result.ProtocolVersion?.ToString() ?? string.Empty);
    }

    public async Task<CopilotStatusResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var result = await _client.GetStatusAsync(cancellationToken);
        return new CopilotStatusResult(result.Version, result.ProtocolVersion.ToString(), ConnectionState);
    }

    public async Task<CopilotAuthResult> GetAuthStatusAsync(CancellationToken cancellationToken)
    {
        var result = await _client.GetAuthStatusAsync(cancellationToken);
        return new CopilotAuthResult(result.IsAuthenticated, result.AuthType, result.Host, result.Login, result.StatusMessage);
    }

    public async Task<IReadOnlyList<CopilotModelResult>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var result = await _client.ListModelsAsync(cancellationToken);
        return result.Select(static model => new CopilotModelResult(
            model.Id,
            model.Name,
            model.Capabilities?.Supports?.Vision == true,
            model.Capabilities?.Supports?.ReasoningEffort == true,
            model.SupportedReasoningEfforts?.ToArray() ?? [],
            model.DefaultReasoningEffort,
            model.Policy?.State)).ToArray();
    }

    public async Task<ICopilotSdkSession> CreateSessionAsync(CopilotSdkSessionConfiguration configuration, CancellationToken cancellationToken)
    {
        var session = await _client.CreateSessionAsync(BuildCreateConfig(configuration), cancellationToken);
        return new GitHubCopilotSdkSession(session, _configuration);
    }

    public async Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, CopilotSdkSessionConfiguration configuration, CancellationToken cancellationToken)
    {
        var session = await _client.ResumeSessionAsync(sessionId, BuildResumeConfig(configuration), cancellationToken);
        return new GitHubCopilotSdkSession(session, _configuration);
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
        => _client.DeleteSessionAsync(sessionId, cancellationToken);

    public Task<string?> GetForegroundSessionIdAsync(CancellationToken cancellationToken)
        => _client.GetForegroundSessionIdAsync(cancellationToken);

    public Task SetForegroundSessionIdAsync(string sessionId, CancellationToken cancellationToken)
        => _client.SetForegroundSessionIdAsync(sessionId, cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private SessionConfig BuildCreateConfig(CopilotSdkSessionConfiguration source)
    {
        var request = source.Request;
        var configuration = request.Configuration;
        return new SessionConfig
        {
            SessionId = string.IsNullOrWhiteSpace(request.RequestedSessionId) ? null : request.RequestedSessionId,
            ClientName = "GnOuGo.GithubCopilot.Core",
            Model = source.Provider?.Model ?? configuration.Model,
            ReasoningEffort = NormalizeNullable(configuration.ReasoningEffort),
            Provider = source.Provider?.Provider,
            WorkingDirectory = configuration.WorkingDirectory,
            Streaming = request.Streaming,
            SystemMessage = BuildSystemMessage(configuration.SystemMessage),
            AvailableTools = configuration.AvailableTools?.ToArray(),
            ExcludedTools = configuration.ExcludedTools?.ToArray(),
            McpServers = configuration.McpServers?.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal),
            SkillDirectories = configuration.SkillDirectories?.ToArray(),
            DisabledSkills = configuration.DisabledSkills?.ToArray(),
            EnableConfigDiscovery = configuration.EnableConfigDiscovery,
            SkipEmbeddingRetrieval = true,
            Hooks = BuildAuditHooks(_logger),
            OnPermissionRequest = BuildPermissionHandler(source),
            OnUserInputRequest = BuildUserInputHandler(source),
            OnElicitationRequest = BuildElicitationHandler(source)
        };
    }

    private ResumeSessionConfig BuildResumeConfig(CopilotSdkSessionConfiguration source)
    {
        var create = BuildCreateConfig(source);
        return new ResumeSessionConfig
        {
            ClientName = create.ClientName,
            Model = create.Model,
            ReasoningEffort = create.ReasoningEffort,
            Provider = create.Provider,
            WorkingDirectory = create.WorkingDirectory,
            Streaming = create.Streaming,
            SystemMessage = create.SystemMessage,
            AvailableTools = create.AvailableTools,
            ExcludedTools = create.ExcludedTools,
            McpServers = create.McpServers,
            SkillDirectories = create.SkillDirectories,
            DisabledSkills = create.DisabledSkills,
            EnableConfigDiscovery = create.EnableConfigDiscovery,
            SkipEmbeddingRetrieval = true,
            OnPermissionRequest = create.OnPermissionRequest,
            OnUserInputRequest = create.OnUserInputRequest,
            ContinuePendingWork = false
        };
    }

    private static SystemMessageConfig? BuildSystemMessage(string? content)
        => string.IsNullOrWhiteSpace(content)
            ? null
            : new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = content.Trim() };

    private static SessionHooks BuildAuditHooks(ILogger logger)
        => new()
        {
            OnPreToolUse = (input, _) =>
            {
                logger.LogDebug("Copilot hook pre-tool-use: {ToolName}", input.ToolName);
                return Task.FromResult<PreToolUseHookOutput?>(null);
            },
            OnPostToolUse = (input, _) =>
            {
                logger.LogDebug("Copilot hook post-tool-use: {ToolName}", input.ToolName);
                return Task.FromResult<PostToolUseHookOutput?>(null);
            },
            OnPostToolUseFailure = (input, _) =>
            {
                logger.LogWarning("Copilot hook tool-failure: {ToolName}", input.ToolName);
                return Task.FromResult<PostToolUseFailureHookOutput?>(null);
            },
            OnUserPromptSubmitted = (input, _) =>
            {
                logger.LogDebug("Copilot hook user-prompt-submitted.");
                return Task.FromResult<UserPromptSubmittedHookOutput?>(null);
            },
            OnSessionStart = (input, _) =>
            {
                logger.LogDebug("Copilot hook session-start: {Source}", input.Source);
                return Task.FromResult<SessionStartHookOutput?>(null);
            },
            OnSessionEnd = (input, _) =>
            {
                logger.LogDebug("Copilot hook session-end: {Reason}", input.Reason);
                return Task.FromResult<SessionEndHookOutput?>(null);
            },
            OnErrorOccurred = (input, _) =>
            {
                logger.LogWarning("Copilot hook error-occurred: {ErrorContext}", input.ErrorContext);
                return Task.FromResult<ErrorOccurredHookOutput?>(null);
            }
        };

    private static Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> BuildPermissionHandler(CopilotSdkSessionConfiguration source)
        => source.Request.PermissionMode switch
        {
            CopilotPermissionMode.ApproveAll => PermissionHandler.ApproveAll,
            CopilotPermissionMode.Deny => static (_, _) => Task.FromResult(PermissionDecision.Reject("The host permission policy denies tool execution.")),
            CopilotPermissionMode.AutoApproveAllowlist => (request, _) => Task.FromResult(IsAllowlisted(request, source.Request.Configuration.PermissionAllowlist)
                ? PermissionDecision.ApproveOnce()
                : PermissionDecision.Reject("The requested operation is outside the read-only allowlist.")),
            CopilotPermissionMode.Interactive => BuildInteractivePermissionHandler(source),
            _ => static (_, _) => Task.FromResult(PermissionDecision.UserNotAvailable())
        };

    private static Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> BuildInteractivePermissionHandler(
        CopilotSdkSessionConfiguration source)
    {
        var taskState = new InteractivePermissionTaskState();
        return async (request, _) => await RequestInteractivePermissionAsync(request, source, taskState);
    }

    private static Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? BuildUserInputHandler(CopilotSdkSessionConfiguration source)
    {
        if (source.HumanInputProvider is null)
            return null;

        return async (request, _) =>
        {
            var response = await source.HumanInputProvider.RequestAsync(
                new CopilotHumanInputRequest(
                    source.Request.Context,
                    "user_input",
                    request.Question,
                    request.Choices?.ToArray() ?? [],
                    request.AllowFreeform ?? true),
                CancellationToken.None);
            return new UserInputResponse
            {
                Answer = response.Accepted ? response.Answer ?? string.Empty : string.Empty,
                WasFreeform = response.WasFreeform
            };
        };
    }

    private static Func<ElicitationContext, Task<ElicitationResult>>? BuildElicitationHandler(CopilotSdkSessionConfiguration source)
    {
        if (source.HumanInputProvider is null)
            return null;

        return async context =>
        {
            var schema = context.RequestedSchema is null
                ? (System.Text.Json.JsonElement?)null
                : System.Text.Json.JsonSerializer.SerializeToElement(context.RequestedSchema, CopilotCoreJsonContext.Default.ElicitationSchema);
            var response = await source.HumanInputProvider.RequestAsync(
                new CopilotHumanInputRequest(
                    source.Request.Context,
                    "elicitation",
                    context.Message,
                    [],
                    true,
                    context.ElicitationSource,
                    schema),
                CancellationToken.None);
            return new ElicitationResult
            {
                Action = response.Accepted ? UIElicitationResponseAction.Accept : UIElicitationResponseAction.Decline,
                Content = response.Accepted && response.Content is { } content
                    ? System.Text.Json.JsonSerializer.Deserialize(content.GetRawText(), CopilotCoreJsonContext.Default.DictionaryStringObject)
                    : null
            };
        };
    }

    internal static async Task<PermissionDecision> RequestInteractivePermissionAsync(
        PermissionRequest request,
        CopilotSdkSessionConfiguration source,
        InteractivePermissionTaskState taskState)
    {
        if (source.HumanInputProvider is null)
            return PermissionDecision.UserNotAvailable();

        var operationKind = DescribePermissionKind(request);
        var details = DescribePermission(request);
        var safeDetails = RedactPermissionDetails(details);
        ReportPermission(source, "permission.requested", "thinking", $"Copilot requested permission for {safeDetails}", operationKind, null, automatic: false);

        var sandboxBypass = RequestsSandboxBypass(request);
        if (!sandboxBypass && taskState.AllowAll)
        {
            ReportPermission(source, "permission.auto_approved", "info", $"Auto-approved {safeDetails} using the current-task grant.", operationKind, CopilotPermissionGrantScope.CurrentTask, automatic: true);
            return PermissionDecision.ApproveOnce();
        }

        if (!sandboxBypass && source.PermissionGrantStore is not null)
        {
            try
            {
                var reusable = await source.PermissionGrantStore.FindReusableGrantAsync(source.Request.Context, CancellationToken.None);
                if (reusable is not null)
                {
                    ReportPermission(source, "permission.auto_approved", "info", $"Auto-approved {safeDetails} using the {FormatGrantScope(reusable.Scope)} grant.", operationKind, reusable.Scope, automatic: true);
                    return PermissionDecision.ApproveOnce();
                }
            }
            catch (Exception ex)
            {
                ReportPermission(source, "permission.grant_lookup_failed", "warning", $"Could not load a reusable Copilot permission grant; explicit approval is required. {ex.GetType().Name}", operationKind, null, automatic: false);
            }
        }

        var hasSessionApproval = TryBuildSessionApproval(request, out var sessionApproval, out var sessionScope);
        var choices = new List<string> { PermissionAllowOnceChoice };
        if (!sandboxBypass && hasSessionApproval)
            choices.Add(PermissionAllowForSessionChoice);
        if (!sandboxBypass && source.Request.Configuration.EnableApproveAll)
        {
            choices.Add(PermissionAllowAllTaskChoice);
            if (source.PermissionGrantStore is not null && !string.IsNullOrWhiteSpace(source.Request.Context.ExecutionId))
                choices.Add(PermissionAllowAllWorkflowChoice);
            if (source.PermissionGrantStore is not null && !string.IsNullOrWhiteSpace(source.Request.Context.AgentId))
                choices.Add(PermissionAllowAllFutureAgentChoice);
        }
        choices.Add(PermissionRefuseChoice);

        if (!string.IsNullOrWhiteSpace(sessionScope))
            details += $"\n\nIf allowed for this session: {sessionScope}";
        if (source.Request.Configuration.EnableApproveAll && !sandboxBypass)
            details += "\n\nBroad approvals never include sandbox-bypass requests. Every automatically approved operation remains visible in workflow activity.";

        var response = await source.HumanInputProvider.RequestAsync(
            new CopilotHumanInputRequest(
                source.Request.Context,
                "permission",
                "Allow this Copilot operation?",
                choices,
                false,
                details),
            CancellationToken.None);

        if (!response.Accepted
            || string.Equals(response.Answer, PermissionRefuseChoice, StringComparison.OrdinalIgnoreCase))
        {
            ReportPermission(source, "permission.refused", "warning", $"Refused {safeDetails}.", operationKind, null, automatic: false);
            return PermissionDecision.Reject("The user refused the operation.");
        }

        if (string.Equals(response.Answer, PermissionAllowOnceChoice, StringComparison.OrdinalIgnoreCase))
        {
            ReportPermission(source, "permission.granted", "info", $"Allowed once: {safeDetails}.", operationKind, null, automatic: false);
            return PermissionDecision.ApproveOnce();
        }

        if (sessionApproval is not null
            && string.Equals(response.Answer, PermissionAllowForSessionChoice, StringComparison.OrdinalIgnoreCase))
        {
            ReportPermission(source, "permission.granted", "info", $"Allowed similar operations for this task: {safeDetails}.", operationKind, CopilotPermissionGrantScope.CurrentTask, automatic: false);
            return sessionApproval;
        }

        if (!sandboxBypass
            && source.Request.Configuration.EnableApproveAll
            && string.Equals(response.Answer, PermissionAllowAllTaskChoice, StringComparison.OrdinalIgnoreCase))
        {
            taskState.AllowAll = true;
            ReportPermission(source, "permission.granted", "warning", $"Allowed all non-bypass operations for this Copilot task, beginning with {safeDetails}.", operationKind, CopilotPermissionGrantScope.CurrentTask, automatic: false);
            return PermissionDecision.ApproveOnce();
        }

        if (!sandboxBypass
            && source.Request.Configuration.EnableApproveAll
            && source.PermissionGrantStore is not null
            && string.Equals(response.Answer, PermissionAllowAllWorkflowChoice, StringComparison.OrdinalIgnoreCase))
        {
            var grant = await source.PermissionGrantStore.GrantWorkflowRunAsync(source.Request.Context, CancellationToken.None);
            ReportPermission(source, "permission.granted", "warning", $"Allowed all non-bypass operations for this workflow run, beginning with {safeDetails}.", operationKind, grant.Scope, automatic: false);
            return PermissionDecision.ApproveOnce();
        }

        if (!sandboxBypass
            && source.Request.Configuration.EnableApproveAll
            && source.PermissionGrantStore is not null
            && string.Equals(response.Answer, PermissionAllowAllFutureAgentChoice, StringComparison.OrdinalIgnoreCase))
        {
            var confirmation = await source.HumanInputProvider.RequestAsync(
                new CopilotHumanInputRequest(
                    source.Request.Context,
                    "permission_persistence_confirmation",
                    "Persist broad Copilot approval for this agent?",
                    [PermissionConfirmFutureAgentChoice, PermissionCancelFutureAgentChoice],
                    false,
                    $"Agent: {source.Request.Context.AgentName ?? source.Request.Context.AgentId}\nTenant: {source.Request.Context.TenantId}\nThis survives restarts until revoked. Sandbox-bypass requests will still require approval."),
                CancellationToken.None);
            if (!confirmation.Accepted
                || !string.Equals(confirmation.Answer, PermissionConfirmFutureAgentChoice, StringComparison.OrdinalIgnoreCase))
            {
                ReportPermission(source, "permission.refused", "warning", "Persistent Copilot approval was cancelled; the pending operation was refused.", operationKind, CopilotPermissionGrantScope.FutureAgentRuns, automatic: false);
                return PermissionDecision.Reject("The user cancelled persistent approval.");
            }

            var grant = await source.PermissionGrantStore.GrantFutureAgentRunsAsync(source.Request.Context, CancellationToken.None);
            ReportPermission(source, "permission.granted", "warning", $"Persisted broad approval for future runs of agent '{grant.AgentName ?? grant.AgentId}', beginning with {safeDetails}.", operationKind, grant.Scope, automatic: false);
            return PermissionDecision.ApproveOnce();
        }

        ReportPermission(source, "permission.refused", "warning", "The Copilot permission response was not recognized and was rejected.", operationKind, null, automatic: false);
        return PermissionDecision.Reject("The permission response was not recognized.");
    }

    private static bool RequestsSandboxBypass(PermissionRequest request)
        => request switch
        {
            PermissionRequestRead read => read.RequestSandboxBypass == true,
            PermissionRequestWrite write => write.RequestSandboxBypass == true,
            PermissionRequestShell shell => shell.RequestSandboxBypass == true,
            PermissionRequestUrl url => url.RequestSandboxBypass == true,
            _ => false
        };

    private static string DescribePermissionKind(PermissionRequest request)
        => request switch
        {
            PermissionRequestRead => "filesystem_read",
            PermissionRequestWrite => "filesystem_write",
            PermissionRequestShell => "shell",
            PermissionRequestMcp => "mcp_tool",
            PermissionRequestUrl => "url",
            PermissionRequestCustomTool => "custom_tool",
            _ => request.Kind ?? "unknown"
        };

    private static string FormatGrantScope(CopilotPermissionGrantScope scope)
        => scope switch
        {
            CopilotPermissionGrantScope.CurrentTask => "current-task",
            CopilotPermissionGrantScope.WorkflowRun => "workflow-run",
            CopilotPermissionGrantScope.FutureAgentRuns => "future-agent-runs",
            _ => scope.ToString()
        };

    private static void ReportPermission(
        CopilotSdkSessionConfiguration source,
        string kind,
        string level,
        string message,
        string operationKind,
        CopilotPermissionGrantScope? scope,
        bool automatic)
    {
        try
        {
            source.PermissionEventSink?.Report(new CopilotPermissionEvent(
                kind,
                level,
                message.Length <= 1200 ? message : message[..1200] + "...",
                operationKind,
                scope,
                source.Request.Context,
                automatic));
        }
        catch
        {
            // Observability must never change the permission decision.
        }
    }

    private static string RedactPermissionDetails(string details)
    {
        var redacted = Regex.Replace(
            details,
            @"(?i)\b(authorization|api[_-]?key|password|secret|token)\s*[:=]\s*(?:bearer\s+)?[^\s;]+",
            "$1=<redacted>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        redacted = redacted.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return redacted.Length <= 800 ? redacted : redacted[..800] + "...";
    }

    internal sealed class InteractivePermissionTaskState
    {
        public bool AllowAll { get; set; }
    }

    internal static PermissionDecision ResolveInteractivePermissionResponse(
        CopilotHumanInputResponse response,
        PermissionDecision? sessionApproval)
    {
        if (!response.Accepted
            || string.Equals(response.Answer, PermissionRefuseChoice, StringComparison.OrdinalIgnoreCase))
        {
            return PermissionDecision.Reject("The user refused the operation.");
        }

        if (string.Equals(response.Answer, PermissionAllowOnceChoice, StringComparison.OrdinalIgnoreCase))
            return PermissionDecision.ApproveOnce();

        if (sessionApproval is not null
            && string.Equals(response.Answer, PermissionAllowForSessionChoice, StringComparison.OrdinalIgnoreCase))
        {
            return sessionApproval;
        }

        return PermissionDecision.Reject("The permission response was not recognized.");
    }

    internal static bool TryBuildSessionApproval(
        PermissionRequest request,
        out PermissionDecision? decision,
        out string? scopeDescription)
    {
        PermissionDecisionApproveForSessionApproval? approval = null;
        string? domain = null;

        switch (request)
        {
            case PermissionRequestShell shell
                when shell.CanOfferSessionApproval == true
                     && shell.Commands?.Select(static command => command.Identifier)
                         .Where(static identifier => !string.IsNullOrWhiteSpace(identifier))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToArray() is { Length: > 0 } commandIdentifiers:
                approval = new PermissionDecisionApproveForSessionApprovalCommands
                {
                    CommandIdentifiers = commandIdentifiers
                };
                scopeDescription = $"commands identified as {string.Join(", ", commandIdentifiers)} until this Copilot session ends.";
                break;

            case PermissionRequestWrite write when write.CanOfferSessionApproval == true:
                approval = new PermissionDecisionApproveForSessionApprovalWrite();
                scopeDescription = "filesystem write operations until this Copilot session ends.";
                break;

            case PermissionRequestRead:
                approval = new PermissionDecisionApproveForSessionApprovalRead();
                scopeDescription = "filesystem read operations until this Copilot session ends.";
                break;

            case PermissionRequestMcp mcp
                when !string.IsNullOrWhiteSpace(mcp.ServerName)
                     && !string.IsNullOrWhiteSpace(mcp.ToolName):
                approval = new PermissionDecisionApproveForSessionApprovalMcp
                {
                    ServerName = mcp.ServerName,
                    ToolName = mcp.ToolName
                };
                scopeDescription = $"MCP tool {mcp.ServerName}/{mcp.ToolName} until this Copilot session ends.";
                break;

            case PermissionRequestUrl url when TryGetPermissionDomain(url.Url, out var resolvedDomain):
                domain = resolvedDomain;
                scopeDescription = $"URL domain {resolvedDomain} until this Copilot session ends.";
                break;

            case PermissionRequestCustomTool tool when !string.IsNullOrWhiteSpace(tool.ToolName):
                approval = new PermissionDecisionApproveForSessionApprovalCustomTool
                {
                    ToolName = tool.ToolName
                };
                scopeDescription = $"custom tool {tool.ToolName} until this Copilot session ends.";
                break;

            default:
                decision = null;
                scopeDescription = null;
                return false;
        }

        decision = new PermissionDecisionApproveForSession
        {
            Approval = approval,
            Domain = domain
        };
        return true;
    }

    internal static bool IsAllowlisted(PermissionRequest request, IReadOnlyList<string>? allowlist)
    {
        var allowed = allowlist ?? [];
        return request switch
        {
            PermissionRequestRead read => allowed.Any(value => PathOrNameMatches(read.Path, value)),
            PermissionRequestMcp mcp => mcp.ReadOnly == true && allowed.Any(value => NameMatches($"{mcp.ServerName}/{mcp.ToolName}", value) || NameMatches(mcp.ToolName, value)),
            PermissionRequestShell shell => shell.HasWriteFileRedirection != true
                && shell.RequestSandboxBypass != true
                && shell.Commands?.Any() == true
                && shell.Commands.All(command => command.ReadOnly == true && allowed.Any(value => NameMatches(command.Identifier, value))),
            PermissionRequestCustomTool tool => allowed.Any(value => NameMatches(tool.ToolName, value)),
            _ => false
        };
    }

    internal static string DescribePermission(PermissionRequest request)
    {
        var details = request switch
        {
            PermissionRequestRead read => $"Read path: {read.Path}\nIntention: {read.Intention ?? "unspecified"}",
            PermissionRequestWrite write => $"Write file: {write.FileName}\nIntention: {write.Intention ?? "unspecified"}",
            PermissionRequestShell shell => $"Shell command: {shell.FullCommandText ?? "unspecified"}\nIntention: {shell.Intention ?? "unspecified"}",
            PermissionRequestMcp mcp => $"MCP tool: {mcp.ServerName}/{mcp.ToolName}\nRead-only: {mcp.ReadOnly}",
            PermissionRequestUrl url => $"URL access: {url.Url}\nIntention: {url.Intention ?? "unspecified"}",
            PermissionRequestCustomTool tool => $"Custom tool: {tool.ToolName}\nDescription: {tool.ToolDescription ?? "unspecified"}",
            _ => $"Permission kind: {request.Kind}"
        };

        var warning = request switch
        {
            PermissionRequestShell shell => shell.Warning,
            _ => null
        };
        var sandboxBypass = request switch
        {
            PermissionRequestRead read => (read.RequestSandboxBypass, read.RequestSandboxBypassReason),
            PermissionRequestWrite write => (write.RequestSandboxBypass, write.RequestSandboxBypassReason),
            PermissionRequestShell shell => (shell.RequestSandboxBypass, shell.RequestSandboxBypassReason),
            PermissionRequestUrl url => (url.RequestSandboxBypass, url.RequestSandboxBypassReason),
            _ => ((bool?)false, (string?)null)
        };
        var reportsSandboxBypass = request is PermissionRequestRead
            or PermissionRequestWrite
            or PermissionRequestShell
            or PermissionRequestUrl;

        if (!string.IsNullOrWhiteSpace(warning))
            details += $"\nWarning: {warning}";
        if (sandboxBypass.Item1 == true)
            details += $"\nSandbox bypass requested: yes ({sandboxBypass.Item2 ?? "no reason supplied"})";
        else if (reportsSandboxBypass)
            details += "\nSandbox bypass requested: no";
        return details;
    }

    private static bool TryGetPermissionDomain(string? value, out string domain)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            domain = uri.IdnHost;
            return true;
        }

        domain = string.Empty;
        return false;
    }

    private static bool NameMatches(string? name, string candidate)
        => !string.IsNullOrWhiteSpace(name) && (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase) || candidate == "*");

    private static bool PathOrNameMatches(string? path, string candidate)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (candidate == "*")
            return true;
        var fullPath = Path.GetFullPath(path);
        var fullCandidate = Path.GetFullPath(candidate);
        return string.Equals(fullPath, fullCandidate, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullCandidate.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class GitHubCopilotSdkSession : ICopilotSdkSession
{
    private readonly CopilotSession _session;
    private readonly CopilotRuntimeConfiguration _configuration;

    public GitHubCopilotSdkSession(CopilotSession session, CopilotRuntimeConfiguration configuration)
    {
        _session = session;
        _configuration = configuration;
    }

    public string SessionId => _session.SessionId;

    public async Task<CopilotSendResult> SendAsync(string handle, CopilotSendRequest request, CancellationToken cancellationToken)
    {
        var events = new List<CopilotStreamEvent>
        {
            new("request_send", "thinking", "Sending a message to Copilot.", DateTimeOffset.UtcNow)
        };
        using var subscription = _session.On<SessionEvent>(evt =>
        {
            if (evt is AssistantReasoningEvent or AssistantReasoningDeltaEvent)
                return;
            events.Add(new CopilotStreamEvent(evt.Type, "thinking", SafeEventMessage(evt), DateTimeOffset.UtcNow));
        });

        var timeout = TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds ?? _configuration.RequestTimeoutSeconds));
        var response = await _session.SendAndWaitAsync(new MessageOptions
        {
            Prompt = request.Prompt,
            Mode = request.DeliveryMode,
            AgentMode = ParseAgentMode(request.AgentMode),
            Attachments = BuildAttachments(request.Attachments, _configuration.WorkingDirectory)
        }, timeout, cancellationToken);

        var content = response?.Data?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("GitHub Copilot returned an empty response.");
        events.Add(new CopilotStreamEvent("completed", "info", "Copilot completed the message.", DateTimeOffset.UtcNow));
        return new CopilotSendResult(handle, SessionId, content, response?.Data?.Model, events.ToArray());
    }

    public async Task<IReadOnlyList<CopilotHistoryEvent>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        var events = await _session.GetEventsAsync(cancellationToken);
        return events.Select(static evt => evt switch
        {
            AssistantMessageEvent assistant => new CopilotHistoryEvent(evt.Type, assistant.Data.Content),
            UserMessageEvent user => new CopilotHistoryEvent(evt.Type, user.Data.Content),
            AssistantReasoningEvent or AssistantReasoningDeltaEvent => new CopilotHistoryEvent(evt.Type, null),
            _ => new CopilotHistoryEvent(evt.Type, null)
        }).ToArray();
    }

    public Task AbortAsync(CancellationToken cancellationToken) => _session.AbortAsync(cancellationToken);

    public Task SetModelAsync(string model, string? reasoningEffort, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(reasoningEffort)
            ? _session.SetModelAsync(model, cancellationToken)
            : _session.SetModelAsync(model, reasoningEffort, null, cancellationToken);

    public async Task<string> GetModeAsync(CancellationToken cancellationToken)
        => (await _session.Rpc.Mode.GetAsync(cancellationToken)).Value;

    public Task SetModeAsync(string mode, CancellationToken cancellationToken)
        => _session.Rpc.Mode.SetAsync(new SessionMode(mode), cancellationToken);

    public async Task<CopilotPlanResult> ReadPlanAsync(CancellationToken cancellationToken)
    {
        var result = await _session.Rpc.Plan.ReadAsync(cancellationToken);
        return new CopilotPlanResult(result.Exists, result.Content);
    }

    public Task UpdatePlanAsync(string content, CancellationToken cancellationToken) => _session.Rpc.Plan.UpdateAsync(content, cancellationToken);

    public Task DeletePlanAsync(CancellationToken cancellationToken) => _session.Rpc.Plan.DeleteAsync(cancellationToken);

    public async Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(CancellationToken cancellationToken)
        => (await _session.Rpc.Workspaces.ListFilesAsync(cancellationToken)).Files.ToArray();

    public async Task<CopilotWorkspaceFileResult> ReadWorkspaceFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _session.Rpc.Workspaces.ReadFileAsync(path, cancellationToken);
            return new CopilotWorkspaceFileResult(path, result.Content, true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or KeyNotFoundException)
        {
            return new CopilotWorkspaceFileResult(path, null, false);
        }
    }

    public Task CreateWorkspaceFileAsync(string path, string content, CancellationToken cancellationToken)
        => _session.Rpc.Workspaces.CreateFileAsync(path, content, cancellationToken);

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private static IList<Attachment>? BuildAttachments(IReadOnlyList<CopilotAttachment>? attachments, string workingDirectory)
    {
        if (attachments is null || attachments.Count == 0)
            return null;

        var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return attachments.Select<CopilotAttachment, Attachment>(attachment => attachment.Type.Trim().ToLowerInvariant() switch
        {
            "file" => BuildFileAttachment(attachment, root),
            "blob" => new AttachmentBlob
            {
                Data = attachment.Content ?? throw new ArgumentException("Blob attachments require base64 content."),
                MimeType = attachment.MimeType ?? "application/octet-stream",
                DisplayName = attachment.Path
            },
            _ => throw new ArgumentException($"Unsupported GA attachment type '{attachment.Type}'. Supported types: file, blob.")
        }).ToArray();
    }

    private static AgentMode? ParseAgentMode(string? mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "interactive" => AgentMode.Interactive,
            "plan" => AgentMode.Plan,
            "autopilot" => AgentMode.Autopilot,
            _ => throw new ArgumentException("agentMode must be interactive, plan, or autopilot.")
        };

    private static AttachmentFile BuildFileAttachment(CopilotAttachment attachment, string root)
    {
        if (string.IsNullOrWhiteSpace(attachment.Path))
            throw new ArgumentException("File attachments require a path.");
        var fullPath = Path.GetFullPath(Path.IsPathFullyQualified(attachment.Path) ? attachment.Path : Path.Combine(root, attachment.Path));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new UnauthorizedAccessException("File attachments must be existing files inside the configured working directory.");
        return new AttachmentFile { Path = fullPath, DisplayName = Path.GetFileName(fullPath) };
    }

    private static string SafeEventMessage(SessionEvent evt)
        => evt switch
        {
            AssistantMessageStartEvent => "Copilot started an assistant message.",
            AssistantMessageDeltaEvent => "Copilot streamed assistant output.",
            AssistantMessageEvent => "Copilot produced an assistant message.",
            ToolExecutionStartEvent => "Copilot started a tool.",
            ToolExecutionCompleteEvent => "Copilot completed a tool.",
            PermissionRequestedEvent => "Copilot requested permission.",
            UserInputRequestedEvent => "Copilot requested user input.",
            AbortEvent => "The Copilot turn was aborted.",
            _ => $"Copilot event: {evt.Type}."
        };
}
