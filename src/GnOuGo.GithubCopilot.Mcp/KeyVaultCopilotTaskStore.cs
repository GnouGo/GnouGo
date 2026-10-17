using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GnOuGo.KeyVault.Core.Services;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace GnOuGo.GithubCopilot.Mcp;

internal sealed class CopilotTaskRecord
{
    public string Tenant { get; set; } = "";
    public required McpTaskInfo Task { get; set; }
    public Dictionary<string, InputResponse> Answers { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> Issued { get; set; } = new(StringComparer.Ordinal);
    public JsonObject? Operation { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CopilotTaskRecord))]
internal partial class CopilotTaskJsonContext : JsonSerializerContext;

/// <summary>Encrypted, tenant-owned task receipts. File ownership never expires active work.</summary>
internal sealed class KeyVaultCopilotTaskStore(IKeyVaultRecordStore records, CodeMcpTraceContextAccessor trace, string leaseDirectory, CopilotLogicalLimits? limits = null) : IMcpTaskStore, IAsyncDisposable
{
    internal const string Collection = "github-copilot.logical-tasks-v1";
    private const string Author = "GnOuGo.GithubCopilot.Mcp";
    private readonly ConcurrentDictionary<string, FileStream> _owners = new(StringComparer.Ordinal);
    private readonly AsyncLocal<Invocation?> _invocation = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, Task> _watchers = new(StringComparer.Ordinal);
    internal sealed class Invocation { internal string? TaskId; }
    internal string? CurrentTaskId => _invocation.Value?.TaskId;
    private string Tenant => !string.IsNullOrWhiteSpace(trace.Current?.TenantId) ? trace.Current.TenantId
        : throw new McpException("Tenant identity is required for MCP task access.");
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    internal IDisposable BeginInvocation()
    {
        var previous = _invocation.Value;
        _invocation.Value = new();
        return new Restore(() => _invocation.Value = previous);
    }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }

    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        (limits ?? new()).Validate();
        var tenant = Tenant;
        if (_invocation.Value is not { } invocation) throw new McpException("A tool invocation is required before task creation.");
        var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow;
        var owner = OpenLease(tenant, id, "owner") ?? throw new IOException("Cannot acquire new task ownership.");
        try
        {
            var state = new CopilotTaskRecord { Tenant = tenant, Task = new(id, McpTaskStatus.Working, now, now, TimeSpan.FromDays(7), 100) };
            await SaveAsync(state, cancellationToken);
            _owners[id] = owner;
            invocation.TaskId = id;
            _watchers[id] = WatchAnswersAsync(tenant, id, _lifetime.Token);
            return state.Task;
        }
        catch { await owner.DisposeAsync(); throw; }
    }

    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        var tenant = Tenant;
        var state = await ReadAsync(tenant, taskId, cancellationToken);
        if (state is null || Terminal(state.Task.Status)) return state?.Task;
        await using var abandoned = OpenLease(tenant, taskId, "owner");
        if (abandoned is null) return state.Task;
        // No process owns the unfinished external work. Keep its checkpoints and
        // reservations, but never resume an unknown SDK dispatch automatically.
        await ChangeAsync(taskId, record =>
        {
            if (!Terminal(record.Task.Status)) record.Task = record.Task with { Status = McpTaskStatus.Failed,
                Error = Element(new JsonObject { ["code"] = -32603, ["message"] = "COPILOT_NEEDS_RECONCILIATION: the task owner stopped without a verified completion receipt." }) };
        }, cancellationToken);
        return (await ReadAsync(tenant, taskId, cancellationToken))?.Task;
    }

    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
        => FinishAsync(taskId, McpTaskStatus.Completed, result, cancellationToken);
    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
        => FinishAsync(taskId, McpTaskStatus.Failed, error, cancellationToken);
    private async Task FinishAsync(string id, McpTaskStatus status, JsonElement payload, CancellationToken ct)
    {
        await ChangeAsync(id, state =>
        {
            if (!Terminal(state.Task.Status)) state.Task = status == McpTaskStatus.Completed
                ? state.Task with { Status = status, Result = payload.Clone(), InputRequests = null }
                : state.Task with { Status = status, Error = payload.Clone(), InputRequests = null };
        }, ct);
        // A failed write leaves ownership intact. Other hosts must not mistake the
        // still-running finalizer for abandoned work before a receipt is durable.
        if (_owners.TryRemove(id, out var owner)) await owner.DisposeAsync();
    }
    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (await ReadAsync(Tenant, taskId, cancellationToken) is null) return false;
        var changed = false;
        await ChangeAsync(taskId, state =>
        {
            if (Terminal(state.Task.Status)) return;
            changed = true;
            state.Task = state.Task with { Status = McpTaskStatus.Cancelled, InputRequests = null };
        }, cancellationToken);
        return changed;
    }

    public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken = default)
        => ChangeAsync(taskId, state =>
        {
            if (Terminal(state.Task.Status)) throw new McpException("The logical operation no longer accepts interaction.");
            if (DateTimeOffset.UtcNow - state.Task.CreatedAt >= TimeSpan.FromSeconds(limits?.Seconds ?? 1800) || state.Issued.Count + inputRequests.Keys.Count(k => !state.Issued.Contains(k)) > (limits?.Interactions ?? 64))
                throw new McpException("The logical operation interaction or elapsed-time ceiling was reached.");
            var pending = state.Task.InputRequests?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? new();
            foreach (var entry in inputRequests)
            {
                if (!state.Issued.Add(entry.Key)) throw new McpException("A task input identity cannot be reused.");
                pending.Add(entry.Key, entry.Value);
            }
            state.Task = state.Task with { Status = McpTaskStatus.InputRequired, InputRequests = pending };
        }, cancellationToken);

    public async Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
    {
        await ChangeAsync(taskId, state =>
        {
            if (Terminal(state.Task.Status)) return;
            var pending = state.Task.InputRequests?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? new();
            foreach (var answer in inputResponses)
                if (pending.Remove(answer.Key)) state.Answers.TryAdd(answer.Key, answer.Value);
            state.Task = state.Task with { Status = pending.Count == 0 ? McpTaskStatus.Working : McpTaskStatus.InputRequired, InputRequests = pending };
        }, cancellationToken);
        // The owning watcher emits durable answers, including ones written by another
        // host. Unknown/repeated answer IDs are ignored and never trigger callbacks.
    }

    internal async Task<JsonNode> AnswersAsync(string id, CancellationToken ct)
    {
        var record = await ReadAsync(Tenant, id, ct) ?? throw new McpException("Task unavailable.");
        var serialized = JsonSerializer.SerializeToNode(record, CopilotTaskJsonContext.Default.CopilotTaskRecord)!;
        return serialized["answers"]!.DeepClone();
    }
    internal Task UpdateOperationAsync(string id, Action<JsonObject> update, CancellationToken ct) => ChangeAsync(id, state =>
    { state.Operation ??= new(); update(state.Operation); }, ct);
    internal Task UpdateOwnedOperationAsync(string tenant, string id, Action<JsonObject> update, CancellationToken ct)
    {
        if (!_owners.ContainsKey(id)) throw new McpException("The logical operation is not owned by this host.");
        return ChangeAsync(tenant, id, state => { state.Operation ??= new(); update(state.Operation); }, ct);
    }
    internal async Task<JsonObject?> ReadOperationAsync(string id, CancellationToken ct) => (await ReadAsync(Tenant, id, ct))?.Operation?.DeepClone().AsObject();

    private async Task WatchAnswersAsync(string tenant, string id, CancellationToken ct)
    {
        var delivered = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            do
            {
                var record = await ReadAsync(tenant, id, ct);
                if (record is null) return;
                foreach (var (key, answer) in record.Answers)
                    if (delivered.Add(key)) InputResponseReceived?.Invoke(new() { TaskId = id, RequestId = key, Response = answer });
                if (!_owners.ContainsKey(id)) return;
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private Task ChangeAsync(string id, Action<CopilotTaskRecord> change, CancellationToken ct) => ChangeAsync(Tenant, id, change, ct);
    private async Task ChangeAsync(string tenant, string id, Action<CopilotTaskRecord> change, CancellationToken ct)
    {
        FileStream? gate;
        while ((gate = OpenLease(tenant, id, "write")) is null) await Task.Delay(10, ct);
        await using var owned = gate;
        var state = await ReadAsync(tenant, id, ct) ?? throw new McpException("The task is unavailable for this tenant.");
        change(state);
        state.Task = state.Task with { LastUpdatedAt = DateTimeOffset.UtcNow };
        await SaveAsync(state, ct);
    }
    private async Task<CopilotTaskRecord?> ReadAsync(string tenant, string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        var saved = await records.GetAsync(Collection, tenant, id, Author, ct);
        if (saved is null) return null;
        var state = JsonSerializer.Deserialize(saved.Value, CopilotTaskJsonContext.Default.CopilotTaskRecord);
        return state?.Tenant == tenant && state.Task.TaskId == id ? state : throw new McpException("Invalid task ownership.");
    }
    private Task SaveAsync(CopilotTaskRecord state, CancellationToken ct) => SaveRecordAsync(state, ct);
    private async Task SaveRecordAsync(CopilotTaskRecord state, CancellationToken ct) =>
        await records.UpsertAsync(Collection, state.Tenant, state.Task.TaskId, JsonSerializer.Serialize(state, CopilotTaskJsonContext.Default.CopilotTaskRecord), Author, ct);
    private FileStream? OpenLease(string tenant, string id, string kind)
    {
        Directory.CreateDirectory(leaseDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tenant + "\n" + id)));
        var path = Path.Combine(leaseDirectory, key + "." + kind);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) when (File.Exists(path)) { return null; }
    }
    private static bool Terminal(McpTaskStatus status) => status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;
    private static JsonElement Element(JsonObject value) { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try { await Task.WhenAll(_watchers.Values); }
        finally
        {
            foreach (var owner in _owners.Values) await owner.DisposeAsync();
            _owners.Clear(); _lifetime.Dispose();
        }
    }
}
