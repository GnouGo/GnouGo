using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Flow.Integrations.Planning;

/// <summary>Reservations and completed receipts share the session's exclusive host lease.</summary>
internal sealed class WorkflowPlanningModelJournal(StepExecutionContext context, IKeyVaultRecordStore records,
    PlanningRequest planning, LLMUsageBudgetScope budget) : ILLMClient
{
    internal const string Requests = "flow-planning-model-requests-v3";
    internal const string Receipts = "flow-planning-model-receipts-v3";
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        var key = request.ClientRequestId;
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        copy.ClientRequestId = null;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(planning.SessionId + ":", StringComparison.Ordinal) ||
            !key.EndsWith(":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(copy, PlanningJsonContext.Default.LLMRequest)), StringComparison.Ordinal))
            throw new PlanningConflictException("A session-owned immutable model request must be reserved before dispatch.");
        if (!_active.TryAdd(key, 0)) throw new PlanningConflictException("This planning request is already active.");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var completed = await records.GetAsync(Receipts, planning.TenantId, key, WorkflowPlanningRuntimeFactory.Author, ct);
            if (completed is not null)
            {
                Event("replayed");
                return JsonSerializer.Deserialize(completed.Value, PlanningJsonContext.Default.LLMResponse)
                    ?? throw new PlanningConflictException("The encrypted model receipt is invalid.");
            }
            var reserved = await records.GetAsync(Requests, planning.TenantId, key, WorkflowPlanningRuntimeFactory.Author, ct);
            if (reserved is not null)
            {
                Event("unverifiable");
                throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetUnverifiable,
                    "The previous model dispatch has no completion receipt. It cannot be dispatched again or reset its budget.");
            }
            await records.UpsertAsync(Requests, planning.TenantId, key, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), WorkflowPlanningRuntimeFactory.Author, ct);
            Event("reserved");
            var response = await budget.CallAsync(context.Engine.LLMClient ?? throw new InvalidOperationException("No planning model is configured."),
                context.Engine.ModelUsageCostEstimator, request, "workflow.plan.model", ct);
            await records.UpsertAsync(Receipts, planning.TenantId, key, JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse), WorkflowPlanningRuntimeFactory.Author, ct);
            Event("completed");
            return response;
        }
        finally { _active.TryRemove(key, out _); }

        void Event(string outcome) => context.AddTelemetryEvent("gnougo-flow.plan.receipt", new[]
        {
            new KeyValuePair<string, object?>("tenant.id", planning.TenantId),
            new KeyValuePair<string, object?>("gnougo-flow.plan.session_id", planning.SessionId),
            new KeyValuePair<string, object?>("gnougo-flow.plan.request_id", key),
            new KeyValuePair<string, object?>("gnougo-flow.plan.receipt.outcome", outcome),
            new KeyValuePair<string, object?>("gnougo-flow.plan.receipt.elapsed_ms", stopwatch.Elapsed.TotalMilliseconds),
            new KeyValuePair<string, object?>("gnougo-flow.plan.budget.calls", budget.Snapshot.Calls)
        });
    }
}

internal sealed class WorkflowPlanningBudgetSink(IKeyVaultRecordStore records, string tenant, string session) : ILLMUsageBudgetSink
{
    internal const string Collection = "flow-planning-budgets-v3";
    public async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct)
        => await records.UpsertAsync(Collection, tenant, session, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), WorkflowPlanningRuntimeFactory.Author, ct);
}
