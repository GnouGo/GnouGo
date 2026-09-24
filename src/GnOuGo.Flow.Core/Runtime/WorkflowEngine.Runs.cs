using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime.Executors;

namespace GnOuGo.Flow.Core.Runtime;

public sealed partial class WorkflowEngine
{
    public Task<RunResult> ResumeAsync(string tenantId, string runId, long expectedRevision, CompiledWorkflow workflow, CancellationToken ct)
    {
        if (RunStore is null) throw new InvalidOperationException("Resume requires an IWorkflowRunStore.");
        Limits.TenantId = tenantId;
        Limits.RunId = runId;
        return ExecuteDurableAsync(workflow, null, expectedRevision, ct);
    }

    private async Task<RunResult> ExecuteDurableAsync(CompiledWorkflow workflow, JsonNode? inputs, long? revision, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Limits.TenantId);
        Limits.RunId ??= Guid.NewGuid().ToString("N");
        var definition = JsonSerializer.Serialize(workflow.Document.Source, WorkflowRunJsonContext.Default.WorkflowDocument);
        var definitionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workflow.Name + "\n" + definition)));
        if (revision is null)
        {
            await RunStore!.CreateAsync(new WorkflowRun
            {
                TenantId = Limits.TenantId, RunId = Limits.RunId, WorkflowName = workflow.Name,
                WorkflowYaml = workflow.Document.Source.RawYaml ?? "", DefinitionHash = definitionHash,
                Inputs = inputs?.DeepClone(), Limits = Limits, ModelBudget = LLMUsageBudget?.Limits,
                ModelUsage = LLMUsageBudget?.Snapshot
            }, ct);
        }
        await using var lease = await RunStore!.AcquireAsync(Limits.TenantId, Limits.RunId, revision ?? 0, ct);
        var run = lease.Run;
        if (run.DefinitionHash != definitionHash)
            throw new WorkflowRunConflictException("The workflow definition changed. Regenerate and approve it as a new run.");
        if (run.Status is WorkflowRunStatus.Completed || run.FinalizationCompleted && run.Result is not null)
            return run.Result!;
        Limits = run.Limits;
        var inheritedBudget = LLMUsageBudget;
        Journal = new WorkflowRunJournal(lease);
        if (run.ModelBudget is { } budget)
            LLMUsageBudget = new LLMUsageBudgetScope(budget, run.ModelUsage, sink: Journal, exchangeRateProvider: ExchangeRateProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var polling = new CancellationTokenSource();
        var monitor = MonitorCancellationAsync(lease, linked, polling.Token);
        try
        {
            var result = await ExecuteCoreAsync(workflow, run.Inputs, linked.Token);
            await Journal.FinishAsync(result, CancellationToken.None);
            return result;
        }
        finally
        {
            await polling.CancelAsync();
            await monitor;
            Journal = null;
            LLMUsageBudget = inheritedBudget;
        }
    }

    private static async Task MonitorCancellationAsync(IWorkflowRunLease lease, CancellationTokenSource execution, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            do
            {
                if (await lease.IsCancellationRequestedAsync(ct)) { lease.Run.CancelRequested = true; await execution.CancelAsync(); return; }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { await execution.CancelAsync(); }
    }

    /// <summary>Observes an agent through its adapter; other effects require an operator's explicit confirmation of quiescence.</summary>
    public async Task<WorkflowRun> ReconcileAsync(string tenantId, string runId, long expectedRevision, string invocationId,
        string? confirmedStoppedReason, CancellationToken ct)
    {
        if (RunStore is null) throw new InvalidOperationException("Reconciliation requires an IWorkflowRunStore.");
        await using var lease = await RunStore.AcquireAsync(tenantId, runId, expectedRevision, ct);
        if (!lease.Run.Invocations.TryGetValue(invocationId, out var invocation) || invocation.CompletedAt is not null ||
            invocation.Recovery != StepRecovery.External || invocation.DispatchedAt is null)
            throw new WorkflowRunConflictException("The requested invocation does not require reconciliation.");
        var journal = new WorkflowRunJournal(lease);
        if (!string.IsNullOrWhiteSpace(confirmedStoppedReason))
            await journal.ResolveAsFailedAsync(invocationId, confirmedStoppedReason, ct);
        else if (invocation.StepType == "agent.run")
        {
            var task = JsonSerializer.Deserialize(invocation.ResolvedInput, AgentTaskJsonContext.Default.AgentTaskDefinition)
                ?? throw new WorkflowRunConflictException("The approved agent scope is missing.");
            if (!AgentTaskRunners.TryGetValue(task.Runner, out var runner))
                throw new WorkflowRunConflictException("The approved agent adapter is unavailable.");
            var context = new AgentTaskContext(tenantId, runId, invocationId, task);
            var observed = invocation.Observation is null ? await runner.ReconcileAsync(context, ct) :
                JsonSerializer.Deserialize(invocation.Observation, AgentTaskJsonContext.Default.AgentTaskResult)!;
            if (observed.Status == "needs_reconciliation") return WorkflowRunStorage.Clone(lease.Run);
            await journal.ObserveAsync(invocationId, JsonSerializer.SerializeToNode(observed, AgentTaskJsonContext.Default.AgentTaskResult), ct);
            try
            {
                var output = await AgentRunExecutor.ValidateResultAsync(context, observed, AgentTaskVerifier, ct);
                await journal.ResolveOutputAsync(invocationId, output, ct);
            }
            catch (WorkflowRuntimeException ex) { await journal.ResolveAsFailedAsync(invocationId, ex.Message, ct); }
        }
        else throw new WorkflowRunConflictException("This adapter cannot reconcile the effect. Confirm that the external operation has stopped, with an audit reason; it will remain a failed invocation.");
        return WorkflowRunStorage.Clone(lease.Run);
    }
}
