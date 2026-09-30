namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Isolated, ownership-enforcing store for embedded applications and component tests.</summary>
public sealed class InMemoryWorkflowRunStore : IWorkflowRunStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Tenant, string Run), WorkflowRun> _runs = [];
    private readonly HashSet<(string Tenant, string Run)> _owners = [];

    public Task<WorkflowRun?> ReadAsync(string tenantId, string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (_gate) return Task.FromResult(_runs.TryGetValue((tenantId, runId), out var run) ? WorkflowRunStorage.Clone(run) : null);
    }

    public Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        lock (_gate) return Task.FromResult<IReadOnlyList<WorkflowRun>>(_runs.Where(p => p.Key.Tenant == tenantId)
            .Select(p => WorkflowRunStorage.Clone(p.Value)).OrderByDescending(r => r.UpdatedAt).ToArray());
    }

    public Task CreateAsync(WorkflowRun run, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        WorkflowRunStorage.ValidateOwnership(run, run.TenantId, run.RunId);
        if (run.Revision != 0) throw new WorkflowRunConflictException("A new run must start at revision zero.");
        lock (_gate)
        {
            if (!_runs.TryAdd((run.TenantId, run.RunId), WorkflowRunStorage.Clone(run)))
                throw new WorkflowRunConflictException("This run already exists. Inspect it and resume using its current revision.");
        }
        return Task.CompletedTask;
    }

    public Task<IWorkflowRunLease> AcquireAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var run = Get(tenantId, runId, expectedRevision);
            if (!_owners.Add((tenantId, runId))) throw new WorkflowRunConflictException("This run already has an active owner.");
            return Task.FromResult<IWorkflowRunLease>(new Lease(this, WorkflowRunStorage.Clone(run)));
        }
    }

    public Task<WorkflowRun> CancelAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var run = Get(tenantId, runId, expectedRevision);
            run.CancelRequested = true;
            run.Revision++;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            run.Events.Add(new(run.UpdatedAt, "cancel_requested", null));
            return Task.FromResult(WorkflowRunStorage.Clone(run));
        }
    }

    private WorkflowRun Get(string tenantId, string runId, long revision)
    {
        if (!_runs.TryGetValue((tenantId, runId), out var run)) throw new WorkflowRunConflictException("Run not found for this tenant.");
        if (run.Revision != revision) throw new WorkflowRunConflictException("The run changed. Inspect its current revision before issuing a command.");
        return run;
    }

    public Task<WorkflowRun> RequestInputAsync(string tenantId, string runId, long expectedRevision, HumanInputRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var run = Get(tenantId, runId, expectedRevision); WorkflowRunStorage.RequestInput(run, request);
            run.Revision++; run.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(WorkflowRunStorage.Clone(run));
        }
    }

    public Task<WorkflowRun> AnswerAsync(string tenantId, string runId, long expectedRevision, string invocationId,
        System.Text.Json.Nodes.JsonNode? response, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var run = Get(tenantId, runId, expectedRevision);
            WorkflowRunStorage.Answer(run, invocationId, response);
            run.Revision++;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(WorkflowRunStorage.Clone(run));
        }
    }

    private sealed class Lease(InMemoryWorkflowRunStore owner, WorkflowRun run) : IWorkflowRunLease
    {
        private bool _disposed;
        public WorkflowRun Run { get; } = run;
        public Task SaveAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var stored = owner._runs[(Run.TenantId, Run.RunId)];
                WorkflowRunStorage.MergeCommands(Run, stored);
                Run.Revision = stored.Revision + 1;
                Run.UpdatedAt = DateTimeOffset.UtcNow;
                owner._runs[(Run.TenantId, Run.RunId)] = WorkflowRunStorage.Clone(Run);
            }
            return Task.CompletedTask;
        }
        public Task<bool> IsCancellationRequestedAsync(CancellationToken ct = default)
        {
            lock (owner._gate) return Task.FromResult(owner._runs[(Run.TenantId, Run.RunId)].CancelRequested);
        }
        public ValueTask DisposeAsync()
        {
            lock (owner._gate) { _disposed = true; owner._owners.Remove((Run.TenantId, Run.RunId)); }
            return ValueTask.CompletedTask;
        }
    }
}
