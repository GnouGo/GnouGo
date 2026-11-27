using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core;

public sealed class CopilotSandboxRequiredException(CopilotSandboxReadiness readiness = CopilotSandboxReadiness.NotConfigured) : InvalidOperationException(readiness switch
{
    CopilotSandboxReadiness.NotConfigured => "Bounded commands require mandatory host isolation. Configure managed sandbox.enabled=true and sandbox.failIfUnavailable=true before approval.",
    CopilotSandboxReadiness.Invalid => "Mandatory host isolation policy could not be validated. Correct the managed policy before approval.",
    _ => "Mandatory host isolation is unavailable. Check host dependencies and platform support before execution."
})
{
    public string Code => readiness switch
    {
        CopilotSandboxReadiness.NotConfigured => "AGENT_ISOLATION_REQUIRED",
        CopilotSandboxReadiness.Invalid => "AGENT_ISOLATION_POLICY_INVALID",
        _ => "AGENT_ISOLATION_UNAVAILABLE"
    };
}

/// <summary>One invocation's non-renewable ceilings. Reservations precede inference and are never refunded.</summary>
public sealed class CopilotExecutionBounds(int maxModelCalls, long maxTotalTokens, DateTimeOffset deadline,
    IReadOnlySet<string> tools, Func<int, long, CancellationToken, Task> persistReservation)
{
    private readonly CopilotInferenceBudget _inference = new(maxModelCalls, maxTotalTokens, deadline, persistReservation);
    public IReadOnlySet<string> Tools { get; } = tools;
    public IReadOnlyList<string>? SessionTools { get; internal set; }
    public Func<int, string, int, int, CancellationToken, Task>? ReserveHostAttemptAsync
    {
        init => _inference.ReserveHostAttemptAsync = value;
    }
    internal bool InspectsSandboxPolicy => RequiresDeviceIsolation(usesSessionFileSystem: false);
    // Native view reads through the registered session filesystem. Shell and search can start local processes.
    internal bool RequiresDeviceIsolation(bool usesSessionFileSystem) =>
        Tools.Contains("bash") || Tools.Contains("powershell") || Tools.Contains("rg") ||
        (Tools.Contains("view") || Tools.Contains("apply_patch")) && !usesSessionFileSystem;
    public DateTimeOffset Deadline { get; } = deadline;
    public int ModelCalls => _inference.ModelCalls;
    /// <summary>Conservative charged token ceiling, not measured model usage.</summary>
    public long ChargedTokens => _inference.ChargedTokens;
    public bool Exhausted => AdmissionStop is not null;
    public CopilotAdmissionStop? AdmissionStop => _inference.AdmissionStop;
    private volatile bool _transportFailed;
    public Func<int, string, HttpContent, string, CancellationToken, Task>? SettleHostAttemptAsync
    {
        init => _inference.SettleHostAttemptAsync = value;
    }
    internal CopilotInferenceFailure? ProviderFailure => _inference.ProviderFailure;
    internal CancellationToken ProviderRejected => _inference.ProviderRejected;
    internal void RecordProviderFailure(CopilotInferenceFailure failure)
    {
        _stopped = true;
        _inference.RecordProviderFailure(failure);
    }
    internal bool TransportFailed => _transportFailed;
    internal void RecordTransportFailure() => _transportFailed = true;
    public string? ApprovedPrompt { get; init; }
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];
    private int _turnStarted;
    private volatile bool _stopped;
    private readonly object _operationsGate = new();
    private int _activeOperations;
    private int _sdkAdmissions;
    public int? MaxActions { get; init; }
    public CopilotAdaptiveActions? AdaptiveActions { get; init; }
    public bool HasUnresolvedActions => AdaptiveActions?.Unresolved == true;
    public int Actions { get { lock (_operationsGate) return _sdkAdmissions; } }
    private bool _unpairedSdkCompletion;
    private readonly Dictionary<string, int> _sdkActive = new(StringComparer.Ordinal);
    private TaskCompletionSource _drained = CompletedDrain();
    public bool Stopped => _stopped;
    /// <summary>Close admissions and drain actual host file operations and durable reservations.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        Task drained;
        lock (_operationsGate) { _stopped = true; drained = _drained.Task; }
        await _inference.StopAsync(ct);
        await drained.WaitAsync(ct);
    }

    internal bool TryAdmitSdkTool(string tool)
    {
        lock (_operationsGate)
        {
            if (_stopped || DateTimeOffset.UtcNow >= Deadline || !Tools.Contains(tool) ||
                MaxActions is { } limit && _sdkAdmissions >= limit) return false;
            _sdkAdmissions++; _sdkActive[tool] = _sdkActive.GetValueOrDefault(tool) + 1;
            return true;
        }
    }
    internal void CompleteSdkTool(string tool)
    {
        lock (_operationsGate)
        {
            var active = _sdkActive.GetValueOrDefault(tool);
            if (active == 0) _unpairedSdkCompletion = true;
            else _sdkActive[tool] = active - 1;
        }
    }
    /// <summary>Every pre-tool admission must have a matching post-tool observation before closure.</summary>
    public bool ToolAdmissionsObserved(int observedCalls)
    {
        lock (_operationsGate) return _stopped && !_unpairedSdkCompletion && _sdkAdmissions == observedCalls && _sdkActive.Values.All(n => n == 0);
    }

    internal IDisposable EnterHostOperation(string tool)
    {
        lock (_operationsGate)
        {
            if (_stopped || DateTimeOffset.UtcNow >= Deadline || !Tools.Contains(tool))
                throw new InvalidOperationException("The operation exceeds the approved task scope or admission window.");
            if (_activeOperations++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new OperationLease(this);
        }
    }

    private sealed class OperationLease(CopilotExecutionBounds owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._operationsGate) { if (--owner._activeOperations == 0) owner._drained.TrySetResult(); }
        }
    }
    private static TaskCompletionSource CompletedDrain()
    { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }
    internal void BeginTurn(string prompt)
    {
        if (ApprovedPrompt != prompt || Interlocked.Exchange(ref _turnStarted, 1) != 0)
            throw new InvalidOperationException("A bounded task permits one turn with its approved objective and inputs. Inspect the original receipt.");
    }

    internal Task ReserveAsync(HttpRequestMessage request, CancellationToken ct) => ReserveAsync(request, null, ct);

    internal async Task ReserveAsync(HttpRequestMessage request, CopilotPreparedInference? prepared, CancellationToken ct)
    {
        if (_stopped) throw new InvalidOperationException("The bounded task no longer admits inference.");
        try { await _inference.ReserveAsync(request, prepared, ct); }
        finally { if (_inference.AdmissionStop is not null) lock (_operationsGate) _stopped = true; }
    }

}

internal sealed class CopilotBoundedInferenceHandler(CopilotExecutionBounds bounds, CopilotInferenceProxyHandler? proxy,
    CopilotInferenceAdapter? adapter = null) : CopilotRequestHandler
{
    protected override Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context)
        => CopilotInferenceDispatch.SendAsync(request, context, adapter, proxy, bounds.ReserveAsync, bounds.RecordTransportFailure, rejected: bounds.RecordProviderFailure);
    protected override Task<CopilotWebSocketHandler> OpenWebSocketAsync(GitHub.Copilot.CopilotRequestContext context)
        => Task.FromException<CopilotWebSocketHandler>(new InvalidOperationException("Unmetered WebSocket inference is unavailable for bounded tasks."));
}
