using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime;

public sealed partial class LLMUsageBudgetScope
{
    /// <summary>
    /// Checks complete request ceilings against this scope and its parents without
    /// charging calls, tokens, money, exchange quotes or durable receipts. This is
    /// an admission check, not a reservation; each real dispatch still uses CallAsync.
    /// </summary>
    public async Task EnsureAllowanceAsync(LLMRequest request, IModelUsageCostEstimator? estimator,
        int inputTokenCeiling, IReadOnlyList<int> outputTokenCeilings, string stage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outputTokenCeilings);
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokenCeiling);
        if (outputTokenCeilings.Count == 0 || outputTokenCeilings.Any(v => v < 0))
            throw new ArgumentException("Supply one nonnegative output ceiling per planned call.", nameof(outputTokenCeilings));
        ct.ThrowIfCancellationRequested();
        var safeStage = SanitizeStage(stage);
        var scopes = new List<LLMUsageBudgetScope>();
        for (var scope = this; scope is not null; scope = scope._parent) scopes.Add(scope);
        scopes.Reverse();
        var acquired = new List<SemaphoreSlim>();
        try
        {
            // Match dispatch's parent-first lock order and wait for outstanding
            // monetary calls to finish accounting before inspecting their ledger.
            foreach (var scope in scopes)
                if (scope._unaccountedCostGate is { } gate)
                { await gate.WaitAsync(ct).ConfigureAwait(false); acquired.Add(gate); }
            foreach (var scope in scopes)
                await scope.EnsureLocalAllowanceAsync(request, estimator, inputTokenCeiling, outputTokenCeilings, safeStage, ct).ConfigureAwait(false);
        }
        catch (WorkflowRuntimeException ex) when (ex.Code is ErrorCodes.LlmBudgetExceeded or ErrorCodes.LlmBudgetUnverifiable)
        {
            if (ex.Details is { } details) details["dispatch_status"] = "not_started";
            throw;
        }
        finally
        {
            for (var i = acquired.Count - 1; i >= 0; i--) acquired[i].Release();
        }
    }

    private async Task<decimal> EnsureLocalAllowanceAsync(LLMRequest request, IModelUsageCostEstimator? estimator,
        int inputTokens, IReadOnlyList<int> outputs, string stage, CancellationToken ct)
    {
        decimal cost = 0;
        if (_costLimit is not null)
            foreach (var outputTokens in outputs)
            {
                ModelUsageCostEstimate? estimate;
                try { estimate = estimator?.EstimateCostWithCurrency(request.Model, inputTokens, outputTokens, request.Provider); }
                catch { throw CreateUnverifiable(stage, "pricing", Snapshot); }
                if (estimate is null) throw CreateUnverifiable(stage, "pricing", Snapshot);
                var converted = await ConvertToBudgetCurrencyAsync(estimate, stage, ct, pinQuote: false).ConfigureAwait(false);
                try { cost = checked(cost + converted.Amount); }
                catch (OverflowException) { throw CreateUnverifiable(stage, "pricing", Snapshot); }
            }
        lock (_gate)
            EnsureLocalAllowanceCore(_snapshot, inputTokens, outputs, cost, stage);
        return cost;
    }

    private void EnsureLocalAllowanceCore(LLMUsageBudgetSnapshot snapshot, int inputTokens,
        IReadOnlyList<int> outputs, decimal cost, string stage)
    {
        EnsurePreCallLimits(snapshot, stage);
        if (Limits.MaxCalls is { } calls && outputs.Count > calls - snapshot.Calls)
            throw CreateExceeded(stage, "calls", calls, snapshot.Calls + outputs.Count, snapshot);
        var tokens = (long)inputTokens * outputs.Count + outputs.Sum(v => (long)v);
        if (Limits.MaxTotalTokens is { } tokenLimit && tokens > tokenLimit - snapshot.TotalTokens)
            throw CreateExceeded(stage, "total_tokens", tokenLimit, (decimal)snapshot.TotalTokens + tokens, snapshot);
        if (_costLimit is { } money && cost > money.Amount - snapshot.EstimatedCost)
            throw CreateExceeded(stage, ResolveEstimatedCostLimitKind(), money.Amount, cost, snapshot);
    }
}
