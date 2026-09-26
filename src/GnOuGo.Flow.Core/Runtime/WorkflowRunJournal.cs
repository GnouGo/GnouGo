using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Serializes journal writes while independent branches execute outside the write gate.</summary>
internal sealed class WorkflowRunJournal(IWorkflowRunLease lease) : ILLMUsageBudgetSink
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _storageFailed;
    public WorkflowEffectGate Effects { get; } = new();
    public WorkflowRun Run => lease.Run;
    public bool HasPendingHumanInput => !Run.CancelRequested && Run.Invocations.Values.Any(i => i.Status == "waiting_for_human");
    public bool HasUnresolvedWork => HasUnresolvedOutsideAncestors(null);
    public bool HasUnresolvedOutsideAncestors(string? path) => _storageFailed || Run.Invocations.Values.Any(i =>
        i.Recovery == StepRecovery.External && i.DispatchedAt is not null && i.CompletedAt is null &&
        (path is null || !path.StartsWith(i.Id + "/", StringComparison.Ordinal)));

    public async Task<WorkflowInvocation> PrepareAsync(string id, CompiledStep step, StepRecovery recovery,
        bool finalization, JsonObject data, Func<(bool Run, JsonNode? Input)> resolve, CancellationToken ct)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (Run.Invocations.TryGetValue(id, out var existing))
            {
                if (existing.StepType != step.Type || existing.Recovery != recovery)
                    throw new WorkflowRunConflictException("The executor contract changed since this invocation was approved.");
                return existing;
            }
            ct.ThrowIfCancellationRequested();
            if (!finalization && await lease.IsCancellationRequestedAsync(ct)) throw new OperationCanceledException(ct);
            var count = finalization ? Run.FinalizationStepsStarted : Run.StepsStarted;
            var maximum = finalization ? Run.Limits.MaxFinalizationSteps : Run.Limits.MaxTotalStepsExecuted;
            if (count >= maximum) throw new WorkflowRuntimeException(ErrorCodes.LoopLimit, "The persisted execution step budget is exhausted.");
            var resolved = resolve();
            var invocation = new WorkflowInvocation
            {
                Id = id, StepType = step.Type, Recovery = recovery, IsFinalization = finalization,
                DataBefore = (JsonObject)data.DeepClone(), ResolvedInput = resolved.Input?.DeepClone(),
                Status = resolved.Run ? "prepared" : "skipped",
                CompletedAt = resolved.Run ? null : DateTimeOffset.UtcNow
            };
            if (!Run.Invocations.TryAdd(id, invocation)) throw new WorkflowRunConflictException("Duplicate invocation identity.");
            if (finalization) Run.FinalizationStepsStarted++; else Run.StepsStarted++;
            await SaveAsync(resolved.Run ? "intent" : "skipped", id, ct);
            return invocation;
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonNode?> InvokeAsync(WorkflowInvocation invocation, JsonObject data,
        Func<Task<JsonNode?>> execute, bool holdsLeafEffect, CancellationToken ct)
    {
        if (invocation.Status == "completed")
        {
            Restore(data, invocation.DataAfter ?? invocation.DataBefore);
            return invocation.Output?.DeepClone();
        }
        if (invocation.Status == "failed") throw FromError(invocation.Error!);
        if (invocation.Recovery == StepRecovery.External && invocation.DispatchedAt is not null)
            throw Uncertain(invocation.Id);
        Restore(data, invocation.DataBefore);
        ct.ThrowIfCancellationRequested();
        using var effect = holdsLeafEffect && !invocation.IsFinalization ? await Effects.EnterEffectAsync(ct) : null;
        await ChangeAsync(() =>
        {
            invocation.DispatchedAt ??= DateTimeOffset.UtcNow;
            invocation.Status = invocation.Recovery == StepRecovery.HumanInput ? "waiting_for_human" : "dispatched";
            if (invocation.Recovery == StepRecovery.HumanInput) Run.Status = WorkflowRunStatus.WaitingForHuman;
        }, "dispatch", invocation.Id, ct);
        try
        {
            var output = await execute();
            await ChangeAsync(() =>
            {
                invocation.Status = "completed";
                invocation.Output = output?.DeepClone();
                invocation.DataAfter = (JsonObject)data.DeepClone();
                invocation.CompletedAt = DateTimeOffset.UtcNow;
                invocation.ExternalCompletionObserved = true;
                Run.Status = WorkflowRunStatus.Running;
            }, "receipt", invocation.Id, CancellationToken.None);
            return output;
        }
        catch (Exception ex) when (!_storageFailed)
        {
            if (invocation.Recovery == StepRecovery.External && !invocation.ExternalCompletionObserved)
            {
                await ChangeAsync(() => { invocation.Status = "needs_reconciliation"; Run.Status = WorkflowRunStatus.NeedsReconciliation; },
                    "outcome_uncertain", invocation.Id, CancellationToken.None);
                throw Uncertain(invocation.Id, ex);
            }
            // An incomplete composite resumes its children. A waiting human is re-presented with the same invocation ID.
            if (ex is OperationCanceledException || ex is WorkflowRuntimeException { Code: "RUN_NEEDS_RECONCILIATION" } ||
                invocation.Recovery == StepRecovery.HumanInput)
                throw;
            var error = ex is WorkflowRuntimeException runtime ? runtime.ToWorkflowError() : new WorkflowError
                { Code = "INTERNAL_ERROR", Type = ex.GetType().Name, Message = ex.Message };
            await ChangeAsync(() =>
            {
                invocation.Status = "failed"; invocation.Error = error;
                invocation.CompletedAt = DateTimeOffset.UtcNow;
            }, "failure_receipt", invocation.Id, CancellationToken.None);
            throw;
        }
    }

    public async Task<JsonNode?> ControlAsync(string invocationId, string key, Func<JsonNode?> resolve, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var invocation = Run.Invocations[invocationId];
            if (invocation.Control.TryGetValue(key, out var value)) return value?.DeepClone();
            value = resolve();
            invocation.Control.Add(key, value?.DeepClone());
            await SaveAsync("control", invocationId, ct);
            return value;
        }
        finally { _gate.Release(); }
    }

    public Task ObserveAsync(string id, JsonNode? observation, CancellationToken ct) => ChangeAsync(() =>
    {
        var invocation = Run.Invocations[id];
        invocation.ExternalCompletionObserved = true;
        invocation.Observation = observation?.DeepClone();
    }, "external_completion", id, ct);

    public Task StartFinalizationAsync(CancellationToken ct) => ChangeAsync(() => Run.FinalizationStarted = true, "finalization_started", null, ct);

    public Task FinishAsync(RunResult result, CancellationToken ct) => ChangeAsync(() =>
    {
        Run.Result = result;
        Run.FinalizationCompleted = !HasUnresolvedWork && !HasPendingHumanInput;
        Run.Status = HasUnresolvedWork ? WorkflowRunStatus.NeedsReconciliation :
            Run.Invocations.Values.Any(i => i.Status == "waiting_for_human") && !Run.CancelRequested ? WorkflowRunStatus.WaitingForHuman :
            Run.CancelRequested || result.Error?.Code == "CANCELLED" ? WorkflowRunStatus.Cancelled :
            result.Success ? WorkflowRunStatus.Completed : WorkflowRunStatus.Failed;
    }, "execution_stopped", null, ct);

    public Task ResolveAsFailedAsync(string id, string reason, CancellationToken ct) => ChangeAsync(() =>
    {
        var invocation = Run.Invocations[id];
        if (invocation.Recovery != StepRecovery.External || invocation.CompletedAt is not null)
            throw new WorkflowRunConflictException("Only an unresolved external invocation can be reconciled.");
        invocation.Error = new WorkflowError { Code = "RECONCILED_FAILURE", Message = reason };
        invocation.ExternalCompletionObserved = true;
        invocation.CompletedAt = DateTimeOffset.UtcNow;
        invocation.Status = "failed";
        Run.Status = HasUnresolvedWork ? WorkflowRunStatus.NeedsReconciliation : WorkflowRunStatus.Running;
    }, "reconciled_failure", id, ct);

    public Task ResolveOutputAsync(string id, JsonNode? output, CancellationToken ct) => ChangeAsync(() =>
    {
        var invocation = Run.Invocations[id];
        invocation.Output = output?.DeepClone();
        invocation.DataAfter = (JsonObject)invocation.DataBefore.DeepClone();
        invocation.ExternalCompletionObserved = true;
        invocation.CompletedAt = DateTimeOffset.UtcNow;
        invocation.Status = "completed";
        Run.Status = HasUnresolvedWork ? WorkflowRunStatus.NeedsReconciliation : WorkflowRunStatus.Running;
    }, "reconciled_receipt", id, ct);

    public async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct) =>
        await ChangeAsync(() => Run.ModelUsage = snapshot, "model_usage", null, ct);

    private async Task ChangeAsync(Action mutate, string kind, string? id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { mutate(); await SaveAsync(kind, id, ct); }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync(string kind, string? id, CancellationToken ct)
    {
        if (_storageFailed) throw new WorkflowRunConflictException("Journal persistence failed. Stop execution and inspect the durable run.");
        Run.Events.Add(new(DateTimeOffset.UtcNow, kind, id));
        try { await lease.SaveAsync(ct); }
        catch { _storageFailed = true; throw; }
    }

    internal static void Restore(JsonObject target, JsonObject source)
    {
        target.Clear();
        foreach (var pair in source) target[pair.Key] = pair.Value?.DeepClone();
    }
    private static WorkflowRuntimeException FromError(WorkflowError error) => new(error.Code, error.Message, error.Retryable, details: error.Details);
    internal static WorkflowRuntimeException Uncertain(string id, Exception? inner = null) =>
        new("RUN_NEEDS_RECONCILIATION", "An external invocation has no verified completion receipt. Reconcile it before resuming or running cleanup.",
            inner: inner, details: new JsonObject { ["invocation_id"] = id });
}
