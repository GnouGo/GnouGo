using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
            CopilotPermissionMode.Interactive => async (request, _) => await RequestInteractivePermissionAsync(request, source),
            _ => static (_, _) => Task.FromResult(PermissionDecision.UserNotAvailable())
        };

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

    private static async Task<PermissionDecision> RequestInteractivePermissionAsync(PermissionRequest request, CopilotSdkSessionConfiguration source)
    {
        if (source.HumanInputProvider is null)
            return PermissionDecision.UserNotAvailable();

        var response = await source.HumanInputProvider.RequestAsync(
            new CopilotHumanInputRequest(
                source.Request.Context,
                "permission",
                "Allow this Copilot operation?",
                ["approve", "deny"],
                false,
                DescribePermission(request)),
            CancellationToken.None);
        return response.Accepted && string.Equals(response.Answer, "approve", StringComparison.OrdinalIgnoreCase)
            ? PermissionDecision.ApproveOnce()
            : PermissionDecision.Reject("The user denied the operation.");
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

    private static string DescribePermission(PermissionRequest request)
        => request switch
        {
            PermissionRequestRead read => $"Read path: {read.Path}",
            PermissionRequestWrite write => $"Write file: {write.FileName}",
            PermissionRequestShell shell => $"Shell command requested: {shell.Intention ?? "unspecified intention"}",
            PermissionRequestMcp mcp => $"MCP tool: {mcp.ServerName}/{mcp.ToolName} (readOnly={mcp.ReadOnly})",
            PermissionRequestUrl url => $"URL access: {url.Url}",
            PermissionRequestCustomTool tool => $"Custom tool: {tool.ToolName}",
            _ => $"Permission kind: {request.Kind}"
        };

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
