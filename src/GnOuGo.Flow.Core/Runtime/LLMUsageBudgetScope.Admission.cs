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
        using var warnings = ObserveTokenWarnings(request.ClientRequestId);
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

    private async Task<(decimal Total, decimal Current)> EnsureLocalAllowanceAsync(LLMRequest request, IModelUsageCostEstimator? estimator,
        int inputTokens, IReadOnlyList<int> outputs, string stage, CancellationToken ct)
    {
        decimal cost = 0, currentCost = 0;
        var first = true;
        if (_costLimit is not null)
            foreach (var outputTokens in outputs)
            {
                ModelUsageCostEstimate? estimate;
                try { estimate = estimator?.EstimateCostWithCurrency(request.Model, inputTokens, outputTokens, request.Provider); }
                catch { throw CreateUnverifiable(stage, "pricing", Snapshot); }
                if (estimate is null) throw CreateUnverifiable(stage, "pricing", Snapshot);
                var converted = await ConvertToBudgetCurrencyAsync(estimate, stage, ct, pinQuote: false).ConfigureAwait(false);
                if (first) { currentCost = converted.Amount; first = false; }
                try { cost = SumCostConservatively(cost, converted.Amount); }
                catch (OverflowException) { throw CreateUnverifiable(stage, "pricing", Snapshot); }
            }
        lock (_gate)
            EnsureLocalAllowanceCore(_snapshot, inputTokens, outputs, cost, stage, currentCost);
        return (cost, currentCost);
    }

    private void EnsureLocalAllowanceCore(LLMUsageBudgetSnapshot snapshot, int inputTokens,
        IReadOnlyList<int> outputs, decimal cost, string stage, decimal currentCost)
    {
        if (AdvisoryFinancialForecasts && _costLimit is not null && (!snapshot.CostEstimateAvailable ||
            snapshot.UncertainCalls > (snapshot.JournaledAttempts?.Values.Count(a => a.VerifiedUsage is null) ?? 0)))
            throw CreateUnverifiable(stage, "historical_cost_evidence", snapshot);
        EnsurePreCallLimits(snapshot, stage);
        if (Limits.MaxCalls is { } calls && outputs.Count > calls - snapshot.Calls &&
            !WarnOptimizationThreshold(stage, "calls", calls, checked(snapshot.Calls + outputs.Count)))
            throw CreateExceeded(stage, "calls", calls, snapshot.Calls + outputs.Count, snapshot);
        var tokens = (long)inputTokens * outputs.Count + outputs.Sum(v => (long)v);
        if (Limits.MaxTotalTokens is { } tokenLimit && tokens > tokenLimit - snapshot.TotalTokens &&
            !WarnTokenThreshold(stage, "total_tokens", tokenLimit, checked(snapshot.TotalTokens + tokens)))
            throw CreateExceeded(stage, "total_tokens", tokenLimit, (decimal)snapshot.TotalTokens + tokens, snapshot);
        if (_costLimit is { } currentLimit && CostSumExceeds(currentLimit.Amount, snapshot.EstimatedCost, currentCost))
            throw CreateExceeded(stage, ResolveEstimatedCostLimitKind(), currentLimit.Amount, currentCost, snapshot);
        if (_costLimit is { } money && CostSumExceeds(money.Amount, snapshot.EstimatedCost, cost))
        {
            if (!AdvisoryFinancialForecasts || outputs.Count < 2 || CostSumExceeds(money.Amount, snapshot.EstimatedCost, currentCost))
                throw CreateExceeded(stage, ResolveEstimatedCostLimitKind(), money.Amount, cost, snapshot);
            var warning = new GnOuGo.Flow.Core.Planning.PlanningThresholdWarning("financial_forecast", outputs.Count, 1,
                "planned_calls; monetary amounts in details", stage) { RequestId = WarningRequestId.Value,
                Details = new System.Text.Json.Nodes.JsonObject { ["currency"] = money.Currency, ["allowance"] = money.Amount,
                    ["trackedExposure"] = snapshot.EstimatedCost, ["forecastAdditionalCost"] = cost, ["currentRequestReservation"] = currentCost,
                    ["origin"] = PolicyOrigin, ["policy"] = "experimental_warning", ["currentReservationEnforced"] = true } };
            lock (_warningGate)
            {
                var warnings = _warnings ??= [];
                if (!warnings.Any(w => w.Kind == warning.Kind && w.RequestId == warning.RequestId && w.Stage == warning.Stage &&
                    System.Text.Json.Nodes.JsonNode.DeepEquals(w.Details, warning.Details))) warnings.Add(warning);
            }
        }
    }
}
