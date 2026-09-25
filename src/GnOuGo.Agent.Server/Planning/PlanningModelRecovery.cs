using System.Text.Json;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

namespace GnOuGo.Agent.Server.Planning;

/// <summary>Explicit operator retry. Retains the old request and budgets its unknown usage.</summary>
internal static class PlanningModelRecovery
{
    internal static async Task PrepareAsync(PlanningSession state, IKeyVaultRecordStore records,
        LLMUsageBudgetLimits limits, IModelUsageCostEstimator estimator, IExchangeRateProvider exchangeRates, CancellationToken ct)
    {
        if (state.Status != PlanningStatus.Stopped || state.PendingCall is not { } pending)
            throw new PlanningConflictException("Only a stopped pending model request can be retried.");
        if (state.Diagnostics.Any(d => d.Code == ErrorCodes.ModelRequestRejected))
            throw new PlanningConflictException("The provider rejected this request. Correct the request or provider configuration, then start a new planning session.");
        var tenant = state.Request.TenantId; var session = state.Request.SessionId;
        var key = session + ":" + pending.Id;
        var author = EfPlanningSessionStore.Author;
        if (await records.GetAsync(PlanningModelJournal.Collection, tenant, key, author, ct) is not null)
        {
            // A late durable receipt is replayed under the original identity without another charge.
            state.Status = PlanningStatus.Generating; state.Diagnostics.Clear(); return;
        }
        var saved = await records.GetAsync(PlanningBudgetSink.Collection, tenant, session, author, ct)
            ?? throw new PlanningConflictException("The dispatch budget is unavailable; recovery cannot reset it.");
        var current = JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        if (current.Calls != state.ModelCalls)
            throw new PlanningConflictException("The dispatch accounting changed; reconcile it before retrying.");
        var correctionKey = key + ":unreported-usage";
        var recorded = await records.GetAsync(PlanningBudgetSink.Collection, tenant, correctionKey, author, ct);
        LLMUsageBudgetSnapshot corrected;
        if (recorded is null)
        {
            var scope = new LLMUsageBudgetScope(limits, current, exchangeRateProvider: exchangeRates);
            // Treat every serialized request byte as an input token, at least the configured
            // input allowance, plus the entire enforced output allowance. This is an estimate,
            // never a fabricated provider receipt or a claim that the failed call was free.
            var input = Math.Max(state.Request.Generation.MaxInputTokensPerRequest,
                JsonSerializer.SerializeToUtf8Bytes(pending.Request, PlanningJsonContext.Default.LLMRequest).LongLength);
            var output = pending.Request.RequireOutputTokenLimit && pending.Request.MaxTokens is > 0
                ? pending.Request.MaxTokens.Value : throw new PlanningConflictException("The failed request has no enforced output bound.");
            try { await scope.AccountUnreportedUsageAsync(pending.Request, estimator, input, output, ct); }
            catch (WorkflowRuntimeException ex) when (ex.Code == ErrorCodes.LlmBudgetExceeded) { /* Persist the charge even when it exhausts the allowance. */ }
            corrected = scope.Snapshot;
            // Write the absolute correction first. A restart before the session checkpoint
            // reuses it instead of adding the estimate twice.
            await records.UpsertAsync(PlanningBudgetSink.Collection, tenant, correctionKey,
                JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), author, ct);
        }
        else corrected = JsonSerializer.Deserialize(recorded.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        if (corrected.Calls != current.Calls || corrected.InputTokens < current.InputTokens || corrected.OutputTokens < current.OutputTokens ||
            corrected.EstimatedCostCurrency != current.EstimatedCostCurrency || corrected.EstimatedCost < current.EstimatedCost)
            throw new PlanningConflictException("A recovery correction cannot replace newer usage.");
        await records.UpsertAsync(PlanningBudgetSink.Collection, tenant, session,
            JsonSerializer.Serialize(corrected, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), author, ct);
        state.Usage = corrected;
        if (state.ModelCalls >= Math.Min(state.Request.MaxModelCalls, limits.MaxCalls ?? int.MaxValue) ||
            limits.MaxTotalTokens is { } tokens && corrected.TotalTokens >= tokens ||
            limits.MaxEstimatedCost is { } cost && corrected.EstimatedCost >= cost.Amount ||
            pending.Purpose == "replan" && state.ReplanAttempts >= state.Request.MaxReplanAttempts)
        {
            state.Diagnostics = [new(ErrorCodes.LlmBudgetExceeded, "$", "Recovery retained the failed dispatch and its estimated usage; no retry allowance remains.")];
            return;
        }
        var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(pending.Request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        request.ClientRequestId = null;
        var hash = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        request.ClientRequestId = session + ":" + (++state.ModelCalls) + ":" + hash;
        if (pending.Purpose == "replan") state.ReplanAttempts++;
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = pending.Purpose, Request = request };
        state.Status = PlanningStatus.Generating; state.Diagnostics.Clear(); state.Yaml = null; state.ApprovedHash = null;
    }
}
