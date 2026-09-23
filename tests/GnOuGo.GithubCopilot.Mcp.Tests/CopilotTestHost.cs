using GnOuGo.GithubCopilot.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.GithubCopilot.Mcp.Tests;

internal sealed class CopilotTestHost : ICopilotSdkClientFactory
{
    public CopilotSdkSessionConfiguration? Configuration { get; private set; }
    public CopilotSendRequest? LastRequest { get; private set; }
    public string? ProjectRoot => Configuration?.Request.Configuration.WorkingDirectory;
    public string? ProviderName => Configuration?.Request.Configuration.ProviderName;
    public bool AgentEditCalled => Configuration?.Request.PermissionMode == CopilotPermissionMode.Interactive;
    public int DeleteCount { get; private set; }
    public CopilotCodeService Service { get; }
    public McpCopilotHumanInputProvider Human { get; }
    public CopilotTestHost(CodeServerSettings settings, string root, CodePolicy? policy = null)
    {
        policy ??= new(settings, root);
        var trace = new CodeMcpTraceContextAccessor();
        var reporter = new CodeProgressReporter(trace);
        Human = new(reporter, trace);
        var options = Options.Create(settings);
        var manager = new CopilotSessionManager(this, humanInputProvider: Human,
            fileSystems: new LocalProjectSessionFsFactory(policy, options, NullLoggerFactory.Instance));
        Service = new(manager, new(policy, options, trace), policy, options, trace, reporter);
    }
    public ICopilotSdkClient Create(CopilotRuntimeConfiguration configuration) => new Client(this);
    private sealed class Client(CopilotTestHost owner) : ICopilotSdkClient
    {
        public string ConnectionState => "connected";
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<CopilotConnectivityResult> PingAsync(CancellationToken ct) => Task.FromResult(new CopilotConnectivityResult("ok", "now", "1"));
        public Task<CopilotStatusResult> GetStatusAsync(CancellationToken ct) => Task.FromResult(new CopilotStatusResult("1", "1", "connected"));
        public Task<CopilotAuthResult> GetAuthStatusAsync(CancellationToken ct) => Task.FromResult(new CopilotAuthResult(true, "test", null, null, null));
        public Task<IReadOnlyList<CopilotModelResult>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CopilotModelResult>>([]);
        public Task<ICopilotSdkSession> CreateSessionAsync(CopilotSdkSessionConfiguration configuration, CancellationToken ct)
        { owner.Configuration = configuration; return Task.FromResult<ICopilotSdkSession>(new Session(owner)); }
        public Task<ICopilotSdkSession> ResumeSessionAsync(string id, CopilotSdkSessionConfiguration configuration, CancellationToken ct) => CreateSessionAsync(configuration, ct);
        public Task DeleteSessionAsync(string id, CancellationToken ct) { owner.DeleteCount++; return Task.CompletedTask; }
        public Task<string?> GetForegroundSessionIdAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task SetForegroundSessionIdAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Session(CopilotTestHost owner) : ICopilotSdkSession
    {
        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public async Task<CopilotSendResult> SendAsync(string handle, CopilotSendRequest request, CancellationToken ct)
        {
            owner.LastRequest = request;
            if (owner.AgentEditCalled) await owner.Configuration!.FileSystem!.WriteFileAsync("src/Program.cs", "// edited\n", null, ct);
            request.Progress?.Invoke(new("completed", "info", "fake suggestion completed", DateTimeOffset.UtcNow));
            return new(handle, SessionId, owner.AgentEditCalled ? "fake edit summary" : "fake suggestion", "fake-model", []);
        }
        public Task<IReadOnlyList<CopilotHistoryEvent>> GetHistoryAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CopilotHistoryEvent>>([]);
        public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SetModelAsync(string model, string? effort, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetModeAsync(CancellationToken ct) => Task.FromResult("interactive");
        public Task SetModeAsync(string mode, CancellationToken ct) => Task.CompletedTask;
        public Task<CopilotPlanResult> ReadPlanAsync(CancellationToken ct) => Task.FromResult(new CopilotPlanResult(false, null));
        public Task UpdatePlanAsync(string content, CancellationToken ct) => Task.CompletedTask;
        public Task DeletePlanAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<CopilotWorkspaceFileResult> ReadWorkspaceFileAsync(string path, CancellationToken ct) => Task.FromResult(new CopilotWorkspaceFileResult(path, null, false));
        public Task CreateWorkspaceFileAsync(string path, string content, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
