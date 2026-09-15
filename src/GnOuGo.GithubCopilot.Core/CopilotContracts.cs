using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

[JsonConverter(typeof(JsonStringEnumConverter<CopilotSessionKind>))]
public enum CopilotSessionKind
{
    OneShot,
    Managed
}

[JsonConverter(typeof(JsonStringEnumConverter<CopilotPermissionMode>))]
public enum CopilotPermissionMode
{
    Interactive,
    AutoApproveAllowlist,
    Deny,
    ApproveAll
}

public sealed record CopilotRequestContext(
    string TenantId,
    string? CorrelationId = null,
    string? RunId = null,
    string? StepId = null,
    string? Repository = null,
    int? PullRequestNumber = null,
    string? HeadSha = null,
    string? ExecutionId = null,
    string? AgentId = null,
    string? AgentName = null);

public sealed record CopilotRuntimeConfiguration(
    string WorkingDirectory,
    string Model,
    string? ReasoningEffort = null,
    string? ProviderName = null,
    string? GitHubToken = null,
    bool UseLoggedInUser = false,
    int RequestTimeoutSeconds = 120,
    int ManagedSessionTtlSeconds = 1800,
    bool EnableApproveAll = false,
    IReadOnlyList<string>? PermissionAllowlist = null,
    IReadOnlyList<string>? AvailableTools = null,
    IReadOnlyList<string>? ExcludedTools = null,
    IReadOnlyList<string>? SkillDirectories = null,
    IReadOnlyList<string>? DisabledSkills = null,
    IReadOnlyDictionary<string, McpServerConfig>? McpServers = null,
    bool EnableConfigDiscovery = false,
    string? SystemMessage = null,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    /// <summary>
    /// Host-policy gate for remembering explicitly approved sandbox-bypass operations.
    /// This remains independent from <see cref="EnableApproveAll"/> so hosts must opt in
    /// to the higher-risk behavior deliberately.
    /// </summary>
    public bool EnableSandboxBypassGrants { get; init; }
}

public sealed record CopilotProviderResolution(string ProviderName, string Model, ProviderConfig Provider);

public interface ICopilotProviderResolver
{
    Task<CopilotProviderResolution?> ResolveAsync(
        string? providerName,
        string fallbackModel,
        string? fallbackBearerToken,
        CancellationToken cancellationToken);
}

public interface ICopilotHumanInputProvider
{
    Task<CopilotHumanInputResponse> RequestAsync(CopilotHumanInputRequest request, CancellationToken cancellationToken);
}

public sealed record CopilotHumanInputRequest(
    CopilotRequestContext Context,
    string Kind,
    string Prompt,
    IReadOnlyList<string> Choices,
    bool AllowFreeform,
    string? Details = null,
    JsonElement? RequestedSchema = null);

public sealed record CopilotHumanInputResponse(
    bool Accepted,
    string? Answer = null,
    bool WasFreeform = false,
    JsonElement? Content = null);

public sealed record CopilotCapability(string Name, string SdkMember, string Stability = "ga");

public sealed record CopilotCapabilityCatalogResult(
    string SdkPackageVersion,
    string McpPackageVersion,
    string RequiredMcpRevision,
    string FallbackMcpRevision,
    IReadOnlyList<CopilotCapability> Capabilities,
    IReadOnlyList<string> ExplicitlyExcluded);

public sealed record CopilotConnectivityResult(string Message, string Timestamp, string ProtocolVersion);

public sealed record CopilotStatusResult(string Version, string ProtocolVersion, string ConnectionState);

public sealed record CopilotAuthResult(bool IsAuthenticated, string? AuthType, string? Host, string? Login, string? StatusMessage);

public sealed record CopilotModelResult(
    string Id,
    string Name,
    bool SupportsVision,
    bool SupportsReasoningEffort,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort,
    string? PolicyState);

public sealed record CopilotSessionCreateRequest(
    CopilotRequestContext Context,
    CopilotRuntimeConfiguration Configuration,
    CopilotSessionKind SessionKind = CopilotSessionKind.Managed,
    CopilotPermissionMode PermissionMode = CopilotPermissionMode.Interactive,
    bool Streaming = false,
    string? RequestedSessionId = null);

public sealed record CopilotSessionResumeRequest(
    CopilotRequestContext Context,
    string Handle);

public sealed record CopilotSessionDescriptor(
    string Handle,
    string CopilotSessionId,
    string TenantId,
    string Model,
    CopilotSessionKind SessionKind,
    CopilotPermissionMode PermissionMode,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    DateTimeOffset ExpiresAt,
    bool Connected);

public sealed record CopilotStoredSession(
    string CopilotSessionId,
    DateTimeOffset StartTime,
    DateTimeOffset ModifiedTime,
    string? Summary,
    string? WorkingDirectory,
    string? GitRoot,
    string? Repository);

public sealed record CopilotAttachment(string Type, string? Path = null, string? Content = null, string? MimeType = null);

public sealed record CopilotSendRequest(
    CopilotRequestContext Context,
    string Handle,
    string Prompt,
    string DeliveryMode = "enqueue",
    string? AgentMode = null,
    IReadOnlyList<CopilotAttachment>? Attachments = null,
    int? TimeoutSeconds = null);

public sealed record CopilotSendResult(
    string Handle,
    string CopilotSessionId,
    [property: Description("Assistant response text. Claims about requested work require validation against execution observations; this text is not a machine-validated task outcome.")]
    string Content,
    string? Model,
    [property: JsonPropertyName("progressEvents")]
    IReadOnlyList<CopilotStreamEvent> Events,
    [property: Description("Whether the assistant turn completed. This does not certify that the requested work succeeded; use verified execution observations to establish its outcome.")]
    bool Completed = true)
{
    private readonly IReadOnlyList<CopilotToolExecutionObservation> _toolExecutions = [];
    [Description("Execution observations captured directly from SDK tool events during this invocation, separate from assistant claims. Consume these existing results to verify commanded work; no separate observation tool is needed. Empty observations, missing completion, missing exit codes, or conflicting completions cannot establish successful work.")]
    public IReadOnlyList<CopilotToolExecutionObservation> ToolExecutions { get => _toolExecutions; init => _toolExecutions = value ?? []; }
}

public sealed record CopilotToolExecutionObservation(
    string ToolCallId,
    string? ParentToolCallId,
    string? ToolName,
    [property: Description("Exact JSON arguments from the tool-start event; null when that event was not observed. Use these to identify the work actually requested, not the assistant's summary.")]
    string? ArgumentsJson,
    bool CompletionObserved,
    [property: Description("Whether the SDK reported successful tool invocation. This is distinct from a process exit code and does not alone establish task success.")]
    bool? ToolSucceeded,
    bool ConflictingCompletion,
    IReadOnlyList<CopilotTerminalObservation> Terminals,
    string? ErrorCode);

public sealed record CopilotTerminalObservation(
    string? WorkingDirectory,
    [property: Description("Process exit code supplied by the SDK terminal result, not inferred from text. Null means process completion is not established. Match the invocation's arguments to the required work and check every required command separately.")]
    long? ExitCode,
    string? Text);

public sealed record CopilotStreamEvent(string Kind, string Level, string Message, DateTimeOffset Timestamp);

public sealed record CopilotHistoryEvent(string Type, string? Content);

public sealed record CopilotOperationResult(bool Success, string? Handle = null, string? CopilotSessionId = null, string? Message = null);

public sealed record CopilotPlanResult(bool Exists, string? Content);

public sealed record CopilotWorkspaceFileResult(string Path, string? Content, bool Exists);

public sealed record CopilotForegroundResult(
    bool HasForeground,
    string? Handle,
    string? CopilotSessionId,
    string? Message = null);

public sealed record CopilotSessionConfigurationResult(
    string Handle,
    string Model,
    string? ReasoningEffort,
    CopilotPermissionMode PermissionMode,
    IReadOnlyList<string> PermissionAllowlist,
    IReadOnlyList<string> AvailableTools,
    IReadOnlyList<string> ExcludedTools,
    IReadOnlyList<string> SkillDirectories,
    IReadOnlyList<string> DisabledSkills,
    IReadOnlyList<string> McpServerNames,
    bool ConfigDiscoveryEnabled,
    bool StableAuditHooksEnabled,
    bool ElicitationEnabled);
