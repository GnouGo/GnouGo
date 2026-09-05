using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Durable request receipts prevent replaying completed model work after a crash.</summary>
internal sealed class PlanningModelJournal(
    ILLMClient inner, IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records,
    string tenantId, string sessionId, long revision, LLMUsageBudgetScope budget, IModelUsageCostEstimator estimator) : ILLMClient
{
    internal const string Collection = "agent-planning-model-receipts-v2";
    private readonly ConcurrentDictionary<string, int> _occurrences = new(StringComparer.Ordinal);
    private static readonly Meter Metrics = new("GnOuGo.Agent.Planning");
    private static readonly Histogram<double> ProviderDuration = Metrics.CreateHistogram<double>("gen_ai.client.operation.duration", "s");
    private static readonly Histogram<long> TokenUsage = Metrics.CreateHistogram<long>("gen_ai.client.token.usage", "{token}");

    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        var requestHash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        var occurrence = _occurrences.AddOrUpdate(requestHash, 0, (_, prior) => prior + 1);
        var key = revision + ":" + requestHash + ":" + occurrence;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.Calls.AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenantId && c.SessionId == sessionId && c.RequestHash == key, ct);
        if (existing is not null)
        {
            if (existing.Status != "completed")
                throw new WorkflowRuntimeException(ErrorCodes.LlmBudgetUnverifiable, "A previous planning request has no durable completion receipt. Its usage cannot be verified; it was not dispatched again.");
            var record = await records.GetAsync(Collection, tenantId, existing.PayloadKey, EfPlanningSessionStore.Author, ct)
                ?? throw new InvalidOperationException("The encrypted model receipt is unavailable.");
            return JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.LLMResponse)
                ?? throw new InvalidOperationException("The encrypted model receipt is invalid.");
        }
        var row = new PlanningCallIndex { TenantId = tenantId, SessionId = sessionId, RequestHash = key, PayloadKey = sessionId + ":" + key };
        db.Calls.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new PlanningConflictException("This model request was reserved by another session worker."); }

        var response = await budget.CallAsync(new MeasuredClient(inner, tenantId), estimator, request, "workflow.plan.typed.model", ct);
        RecordTokens(response.Usage, request.Model);
        await records.UpsertAsync(Collection, tenantId, row.PayloadKey, JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse), EfPlanningSessionStore.Author, ct);
        row.Status = "completed";
        await db.SaveChangesAsync(ct);
        return response;
    }

    private void RecordTokens(JsonNode? usage, string model)
    {
        // Each durable dispatch emits usage once. Replayed receipts return before this point.
        foreach (var (kind, names) in new[] { ("input", new[] { "input_tokens", "prompt_tokens", "inputTokens" }), ("output", new[] { "output_tokens", "completion_tokens", "outputTokens" }) })
            foreach (var name in names)
                if (usage?[name] is JsonValue value && value.TryGetValue<long>(out var tokens) && tokens >= 0)
                {
                    TokenUsage.Record(tokens, new KeyValuePair<string, object?>("gen_ai.token.type", kind), new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                        new KeyValuePair<string, object?>("gen_ai.request.model", model), new KeyValuePair<string, object?>("tenant.id", tenantId));
                    break;
                }
    }

    private sealed class MeasuredClient(ILLMClient client, string tenant) : ILLMClient
    {
        public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            try { return await client.CallAsync(request, ct); }
            finally
            {
                ProviderDuration.Record(started.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("gen_ai.operation.name", "chat"),
                    new KeyValuePair<string, object?>("gen_ai.request.model", request.Model), new KeyValuePair<string, object?>("tenant.id", tenant));
            }
        }
    }
}

internal sealed class PlanningBudgetSink(IKeyVaultRecordStore records, string tenantId, string sessionId) : ILLMUsageBudgetSink
{
    internal const string Collection = "agent-planning-budgets-v2";
    public async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct)
        => await records.UpsertAsync(Collection, tenantId, sessionId, JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), EfPlanningSessionStore.Author, ct);
}
