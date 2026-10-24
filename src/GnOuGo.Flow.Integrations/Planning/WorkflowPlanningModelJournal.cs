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
    PlanningRequest planning, LLMUsageBudgetScope budget, DateTimeOffset? planningDeadlineUtc = null) : ILLMClient, IModelAdmissionOwner, IPlanningConstructionAdmission
{
    internal const string Requests = "flow-planning-model-requests-v10";
    internal const string Receipts = "flow-planning-model-receipts-v10";
    internal const string Http = "flow-planning-model-http-v10";
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public Task AdmitConstructionAsync(LLMRequest request, int inputTokenCeiling, IReadOnlyList<int> outputTokenCeilings, CancellationToken ct)
        => budget.EnsureAllowanceAsync(request, context.Engine.ModelUsageCostEstimator, inputTokenCeiling,
            outputTokenCeilings, "workflow.plan.construction", ct);

    public async Task RecoverSavedResponseAsync(LLMRequest request, CancellationToken ct)
    {
        if (PlanningTransportPolicies.Read(planning) is null ||
            await records.GetAsync(Receipts, planning.TenantId, request.ClientRequestId!, WorkflowPlanningRuntimeFactory.Author, ct) is not null ||
            !await HttpJournal(request).HasSuccessfulResponseAsync(ct)) return;
        // Called under the session lease before planner deadline admission.
        // Only an exact saved success can reach receipt parsing here.
        await CallAsync(request, ct);
    }

    private PlanningHttpModelJournal HttpJournal(LLMRequest request) => new(records, budget, planning, request,
        context.Engine.ModelUsageCostEstimator, Http, WorkflowPlanningRuntimeFactory.Author,
        planningDeadlineUtc ?? DateTimeOffset.UtcNow + (PlanningBudgetOptions.Parse(planning.Options)?.MaxElapsed ?? TimeSpan.FromHours(5)));

    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        if (PlanningConstructionPhasePolicies.IsFresh(planning))
            PlanningConstructionPhasePolicies.ValidateIssued(request, planning);
        else PlanningGenerationPolicy.Apply(request, planning.Generation);
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
                Activity.Current?.SetTag("gnougo.llm.status", "replayed");
                Event("replayed");
                return JsonSerializer.Deserialize(completed.Value, PlanningJsonContext.Default.LLMResponse)
                    ?? throw new PlanningConflictException("The encrypted model receipt is invalid.");
            }
            var reserved = await records.GetAsync(Requests, planning.TenantId, key, WorkflowPlanningRuntimeFactory.Author, ct);
            var journaled = PlanningTransportPolicies.Read(planning) is not null;
            if (reserved is not null && !journaled)
            {
                Event("unverifiable");
                throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetUnverifiable,
                    "The previous model dispatch has no completion receipt. It cannot be dispatched again or reset its budget.");
            }
            if (reserved is not null && reserved.Value != JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest))
                throw new PlanningConflictException("The retained planning request changed before HTTP recovery.");
            if (!journaled || !await HttpJournal(request).HasSuccessfulResponseAsync(ct))
                await ModelRequestAdmission.ValidateAsync(request, context.Engine.LLMCapabilities ?? context.Engine.LLMClient as ILLMCapabilityResolver,
                    PlanningConstructionInputPolicy.EffectiveLimit(planning, request), "planning", ct);
            if (reserved is null)
                await records.UpsertAsync(Requests, planning.TenantId, key, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), WorkflowPlanningRuntimeFactory.Author, ct);
            Event("reserved");
            var client = context.Engine.LLMClient ?? throw new InvalidOperationException("No planning model is configured.");
            var response = journaled
                ? await HttpJournal(request)
                    .CallAsync(client, allowInitialize: reserved is null, ct)
                : await budget.CallAsync(client, context.Engine.ModelUsageCostEstimator, request, "workflow.plan.model", ct);
            using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await records.UpsertAsync(Receipts, planning.TenantId, key, JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse), WorkflowPlanningRuntimeFactory.Author, flush.Token);
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
            new KeyValuePair<string, object?>("gnougo-flow.plan.request.output_ceiling", request.MaxTokens),
            new KeyValuePair<string, object?>("gnougo-flow.plan.receipt.elapsed_ms", stopwatch.Elapsed.TotalMilliseconds),
            new KeyValuePair<string, object?>("gnougo-flow.plan.budget.calls", budget.Snapshot.Calls)
        });
    }
}

internal sealed class WorkflowPlanningBudgetSink(IKeyVaultRecordStore records, string tenant, string session) : ILLMUsageBudgetSink
{
    internal const string Collection = "flow-planning-budgets-v10";
    public async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct)
        => await records.UpsertAsync(Collection, tenant, session, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), WorkflowPlanningRuntimeFactory.Author, ct);
}
