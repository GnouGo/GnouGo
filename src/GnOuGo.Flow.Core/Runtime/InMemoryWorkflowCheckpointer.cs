using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>
/// In-memory implementation of <see cref="IWorkflowCheckpointer"/>
/// for testing and CLI usage (non-durable).
/// </summary>
public sealed class InMemoryWorkflowCheckpointer : IWorkflowCheckpointer
{
    private readonly ConcurrentDictionary<string, WorkflowCheckpoint> _store = new();

    public Task SaveAsync(WorkflowCheckpoint checkpoint, CancellationToken ct)
    {
        _store[checkpoint.RunId] = checkpoint;
        return Task.CompletedTask;
    }

    public Task<WorkflowCheckpoint?> LoadAsync(string runId, CancellationToken ct)
    {
        _store.TryGetValue(runId, out var cp);
        return Task.FromResult(cp);
    }

    public Task DeleteAsync(string runId, CancellationToken ct)
    {
        _store.TryRemove(runId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkflowCheckpoint>> ListAsync(string? tenantId = null, string? status = null, CancellationToken ct = default)
    {
        var results = _store.Values.AsEnumerable();
        if (tenantId != null)
            results = results.Where(c => c.TenantId == tenantId);
        if (status != null)
            results = results.Where(c => c.Status == status);
        return Task.FromResult<IReadOnlyList<WorkflowCheckpoint>>(results.OrderByDescending(c => c.Timestamp).ToList());
    }
}

