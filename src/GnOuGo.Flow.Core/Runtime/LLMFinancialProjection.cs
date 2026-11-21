using System.Text.Json.Nodes;

namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Read-only accounting categories. Tariff estimates are never provider invoices or authority to release a reservation.</summary>
public static class LLMFinancialProjection
{
    public static JsonObject Describe(LLMUsageBudgetSnapshot? snapshot, MonetaryAmount? allowance = null, string forecastMode = "strict")
    {
        if (snapshot is null) return new() { ["historyComplete"] = false, ["unavailableReason"] = "historical_accounting_unavailable", ["availableForAdmission"] = null };
        var attempts = snapshot.JournaledAttempts?.Values.ToArray() ?? [];
        decimal Sum(IEnumerable<decimal> values) => LLMUsageBudgetScope.SumCostConservatively(values.ToArray());
        var settled = attempts.Where(a => a.VerifiedUsage is { CostUnavailableReason: null }).ToArray();
        var unpriced = attempts.Where(a => a.VerifiedUsage?.CostUnavailableReason is not null).ToArray();
        var pending = attempts.Where(a => a.VerifiedUsage is null).ToArray();
        var known = Sum(settled.Select(a => a.VerifiedUsage!.EstimatedCost));
        var held = Sum(pending.Select(a => a.ReservedEstimatedCost));
        var missing = !snapshot.CostEstimateAvailable || unpriced.Length > 0 || pending.Any(a => a.CostUnavailableReason is not null);
        var warnings = (snapshot.Warnings ?? []).Where(w => w.Kind == "financial_forecast").ToArray();
        return new() {
            ["currency"] = snapshot.EstimatedCostCurrency, ["forecastMode"] = forecastMode,
            ["trackedExposure"] = snapshot.EstimatedCost, ["measuredUsageTariffEstimate"] = known,
            ["uncertainReservations"] = held, ["unpricedCompletedNominalReservations"] = Sum(unpriced.Select(a => a.ReservedEstimatedCost)),
            ["unattributedHistoricalExposure"] = snapshot.EstimatedCost - known - held,
            ["providerReportedCost"] = null, ["providerCostBasis"] = "Unavailable in budget snapshots; inspect exact provider receipts. Tariff estimates are not invoices.",
            ["historyComplete"] = !missing && pending.Length == 0 && snapshot.Calls == attempts.Length,
            ["pricingEvidenceComplete"] = !missing,
            ["unavailableReason"] = snapshot.CostUnavailableReason ?? (missing ? "unpriced_usage" : pending.Length > 0 ? "unresolved_physical_attempts" : snapshot.Calls != attempts.Length ? "historical_attempt_details_unavailable" : null),
            ["availableForAdmission"] = null, // This local projection cannot establish global funding or external liabilities.
            ["allowance"] = allowance?.Amount, ["allowanceCurrency"] = allowance?.Currency,
            ["settledAttempts"] = settled.Length, ["unpricedCompletedAttempts"] = unpriced.Length, ["unresolvedAttempts"] = pending.Length,
            ["indexedAttempts"] = attempts.Length, ["calls"] = snapshot.Calls,
            ["measuredInputTokens"] = attempts.Where(a => a.VerifiedUsage is not null).Sum(a => a.VerifiedUsage!.InputTokens),
            ["measuredOutputTokensIncludingReasoning"] = attempts.Where(a => a.VerifiedUsage is not null).Sum(a => a.VerifiedUsage!.OutputTokens),
            ["forecastWarnings"] = new JsonArray(warnings.Select(w => (JsonNode)new JsonObject { ["requestId"] = w.RequestId, ["stage"] = w.Stage, ["details"] = w.Details?.DeepClone() }).ToArray()),
            ["basis"] = "Existing durable usage journal; unresolved reservations are retained. Empty or missing transport evidence alone does not release money." };
    }
}
