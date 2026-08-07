namespace GnOuGo.GithubCopilot.Core;

public interface ICopilotSdkClientFactory
{
    ICopilotSdkClient Create(CopilotRuntimeConfiguration configuration);
}

public interface ICopilotSdkClient : IAsyncDisposable
{
    string ConnectionState { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task<CopilotConnectivityResult> PingAsync(CancellationToken cancellationToken);
    Task<CopilotStatusResult> GetStatusAsync(CancellationToken cancellationToken);
    Task<CopilotAuthResult> GetAuthStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotModelResult>> ListModelsAsync(CancellationToken cancellationToken);
    Task<ICopilotSdkSession> CreateSessionAsync(CopilotSdkSessionConfiguration configuration, CancellationToken cancellationToken);
    Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, CopilotSdkSessionConfiguration configuration, CancellationToken cancellationToken);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<string?> GetForegroundSessionIdAsync(CancellationToken cancellationToken);
    Task SetForegroundSessionIdAsync(string sessionId, CancellationToken cancellationToken);
}

public interface ICopilotSdkSession : IAsyncDisposable
{
    string SessionId { get; }
    Task<CopilotSendResult> SendAsync(string handle, CopilotSendRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotHistoryEvent>> GetHistoryAsync(CancellationToken cancellationToken);
    Task AbortAsync(CancellationToken cancellationToken);
    Task SetModelAsync(string model, string? reasoningEffort, CancellationToken cancellationToken);
    Task<string> GetModeAsync(CancellationToken cancellationToken);
    Task SetModeAsync(string mode, CancellationToken cancellationToken);
    Task<CopilotPlanResult> ReadPlanAsync(CancellationToken cancellationToken);
    Task UpdatePlanAsync(string content, CancellationToken cancellationToken);
    Task DeletePlanAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(CancellationToken cancellationToken);
    Task<CopilotWorkspaceFileResult> ReadWorkspaceFileAsync(string path, CancellationToken cancellationToken);
    Task CreateWorkspaceFileAsync(string path, string content, CancellationToken cancellationToken);
}

public sealed record CopilotSdkSessionConfiguration(
    CopilotSessionCreateRequest Request,
    CopilotProviderResolution? Provider,
    ICopilotHumanInputProvider? HumanInputProvider,
    ICopilotPermissionGrantStore? PermissionGrantStore = null,
    ICopilotPermissionEventSink? PermissionEventSink = null);
