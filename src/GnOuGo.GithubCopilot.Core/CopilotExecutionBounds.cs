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
    private readonly SemaphoreSlim _gate = new(1, 1);
    public IReadOnlySet<string> Tools { get; } = tools;
    internal bool RequiresSandbox => Tools.Contains("bash") || Tools.Contains("powershell");
    public DateTimeOffset Deadline { get; } = deadline;
    public int ModelCalls { get; private set; }
    /// <summary>Conservative charged token ceiling, not measured model usage.</summary>
    public long ChargedTokens { get; private set; }
    public bool Exhausted => AdmissionStop is not null;
    public CopilotAdmissionStop? AdmissionStop { get; private set; }
    private volatile bool _transportFailed;
    internal bool TransportFailed => _transportFailed;
    internal void RecordTransportFailure() => _transportFailed = true;
    public string? ApprovedPrompt { get; init; }
    public IReadOnlyList<string> DeniedPaths { get; init; } = [];
    private int _turnStarted;
    private volatile bool _stopped;
    private readonly object _operationsGate = new();
    private int _activeOperations;
    private int _sdkAdmissions;
    private bool _unpairedSdkCompletion;
    private readonly Dictionary<string, int> _sdkActive = new(StringComparer.Ordinal);
    private TaskCompletionSource _drained = CompletedDrain();
    public bool Stopped => _stopped;
    /// <summary>Close admissions and drain actual host file operations and durable reservations.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        Task drained;
        lock (_operationsGate) { _stopped = true; drained = _drained.Task; }
        await _gate.WaitAsync(ct);
        _gate.Release();
        await drained.WaitAsync(ct);
    }

    internal bool TryAdmitSdkTool(string tool)
    {
        lock (_operationsGate)
        {
            if (_stopped || DateTimeOffset.UtcNow >= Deadline || !Tools.Contains(tool)) return false;
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

    internal IDisposable EnterFileOperation(string tool)
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

    internal async Task ReserveAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_stopped) throw new InvalidOperationException("The bounded task no longer admits inference.");
            if (DateTimeOffset.UtcNow >= Deadline) throw ExhaustedBudget(CopilotAdmissionStopKind.Deadline);
            if (ModelCalls >= maxModelCalls) throw ExhaustedBudget(CopilotAdmissionStopKind.Calls);
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method != HttpMethod.Post || request.Content is null ||
                !(path.EndsWith("/chat/completions", StringComparison.Ordinal) || path.EndsWith("/responses", StringComparison.Ordinal)))
                throw new InvalidOperationException("Bounded Copilot inference requires a supported text request with an explicit output token ceiling.");
            var body = JsonNode.Parse(await request.Content.ReadAsStringAsync(ct)) as JsonObject
                ?? throw new InvalidOperationException("Invalid bounded inference request.");
            if (body["previous_response_id"] is not null || body["conversation"] is not null || !TextOnly(body))
                throw new InvalidOperationException("Opaque conversation state and non-text inference cannot be charged safely.");
            // The supported byte-BPE text protocols consume at most one token per UTF-8 byte.
            // Charge all serialized metadata as well, plus an explicit framing allowance.
            var inputCeiling = checked(Encoding.UTF8.GetByteCount(body.ToJsonString()) + 4096L);
            var remaining = maxTotalTokens - ChargedTokens - inputCeiling;
            if (remaining < 1) throw ExhaustedBudget(CopilotAdmissionStopKind.Tokens, inputCeiling);
            var field = path.EndsWith("/responses", StringComparison.Ordinal) ? "max_output_tokens" : "max_completion_tokens";
            var requested = body[field]?.GetValue<long>() ?? body["max_tokens"]?.GetValue<long>() ?? 8192;
            if (requested < 1) throw new InvalidOperationException("Invalid inference output ceiling.");
            var outputCeiling = Math.Min(requested, Math.Min(remaining, 32768));
            body.Remove("max_tokens"); body[field] = outputCeiling;
            // Multiple completions would multiply the reserved output allowance.
            if (body["n"] is { } count && count.GetValue<int>() != 1)
                throw new InvalidOperationException("Bounded inference permits one completion per request.");
            ModelCalls++; ChargedTokens = checked(ChargedTokens + inputCeiling + outputCeiling);
            await persistReservation(ModelCalls, ChargedTokens, ct);
            var original = request.Content;
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            original.Dispose();
        }
        finally { _gate.Release(); }
    }

    private InvalidOperationException ExhaustedBudget(CopilotAdmissionStopKind kind, long? inputCeiling = null)
    {
        AdmissionStop ??= new(kind, maxModelCalls, maxTotalTokens, Deadline, ModelCalls, ChargedTokens, inputCeiling);
        lock (_operationsGate) _stopped = true;
        return new InvalidOperationException("The approved Copilot inference allowance cannot admit this request.");
    }

    private static bool TextOnly(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue kind && kind.TryGetValue<string>(out var type) &&
                type is "image_url" or "input_image" or "input_audio" or "input_file" or "file" or "computer_screenshot") return false;
            return obj.All(p => TextOnly(p.Value));
        }
        return node is not JsonArray array || array.All(TextOnly);
    }
}

internal sealed class CopilotBoundedInferenceHandler(CopilotExecutionBounds bounds, CopilotInferenceProxyHandler? proxy) : CopilotRequestHandler
{
    private static readonly HttpClient Direct = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    protected override async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, GitHub.Copilot.CopilotRequestContext context)
    {
        await bounds.ReserveAsync(request, context.CancellationToken);
        try
        {
            return proxy is null
                ? await Direct.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken)
                : await proxy.DispatchAsync(request, context);
        }
        catch { bounds.RecordTransportFailure(); throw; }
    }
    protected override Task<CopilotWebSocketHandler> OpenWebSocketAsync(GitHub.Copilot.CopilotRequestContext context)
        => Task.FromException<CopilotWebSocketHandler>(new InvalidOperationException("Unmetered WebSocket inference is unavailable for bounded tasks."));
}
