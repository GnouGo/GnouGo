using System.Diagnostics;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Tests;

public sealed partial class LocalProductOutcomeExecutionTests
{
    // Opt-in measurement of the existing public store/lease boundary. Delegating
    // snapshot hooks is essential: the probe must not change storage behavior.
    private sealed class RunMetrics(IWorkflowRunStore store) : IWorkflowRunStore
    {
        internal int Checkpoints, Captures, Restores;
        internal double CheckpointMilliseconds, CaptureMilliseconds, RestoreMilliseconds;
        public Task<WorkflowRun?> ReadAsync(string tenantId, string runId, CancellationToken ct = default) => store.ReadAsync(tenantId, runId, ct);
        public Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, CancellationToken ct = default) => store.ListAsync(tenantId, ct);
        public Task CreateAsync(WorkflowRun run, CancellationToken ct = default) => store.CreateAsync(run, ct);
        public async Task<IWorkflowRunLease> AcquireAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default)
            => new Lease(await store.AcquireAsync(tenantId, runId, expectedRevision, ct), this);
        public Task<WorkflowRun> CancelAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default) => store.CancelAsync(tenantId, runId, expectedRevision, ct);
        public Task<WorkflowRun> RequestInputAsync(string tenantId, string runId, long expectedRevision, HumanInputRequest request, CancellationToken ct = default)
            => store.RequestInputAsync(tenantId, runId, expectedRevision, request, ct);
        public Task<WorkflowRun> AnswerAsync(string tenantId, string runId, long expectedRevision, string invocationId, JsonNode? response, CancellationToken ct = default)
            => store.AnswerAsync(tenantId, runId, expectedRevision, invocationId, response, ct);

        private sealed class Lease(IWorkflowRunLease lease, RunMetrics metrics) : IWorkflowRunLease
        {
            public WorkflowRun Run => lease.Run;
            public ValueTask DisposeAsync() => lease.DisposeAsync();
            public Task<bool> IsCancellationRequestedAsync(CancellationToken ct = default) => lease.IsCancellationRequestedAsync(ct);
            public Task SaveAsync(CancellationToken ct = default) => Measure(() => lease.SaveAsync(ct), 0);
            public Task SaveAsync(IReadOnlyCollection<string> changedInvocationIds, CancellationToken ct = default) => Measure(() => lease.SaveAsync(changedInvocationIds, ct), 0);
            public Task CaptureSnapshotAsync(WorkflowInvocation invocation, JsonObject data, bool after, CancellationToken ct = default)
                => Measure(() => lease.CaptureSnapshotAsync(invocation, data, after, ct), 1);
            public Task RestoreSnapshotAsync(WorkflowInvocation invocation, JsonObject data, bool after, CancellationToken ct = default)
                => Measure(() => lease.RestoreSnapshotAsync(invocation, data, after, ct), 2);
            private async Task Measure(Func<Task> action, int kind)
            {
                var timer = Stopwatch.StartNew();
                try { await action(); }
                finally
                {
                    switch (kind)
                    {
                        case 0: metrics.Checkpoints++; metrics.CheckpointMilliseconds += timer.Elapsed.TotalMilliseconds; break;
                        case 1: metrics.Captures++; metrics.CaptureMilliseconds += timer.Elapsed.TotalMilliseconds; break;
                        case 2: metrics.Restores++; metrics.RestoreMilliseconds += timer.Elapsed.TotalMilliseconds; break;
                    }
                }
            }
        }
    }
}
