using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static partial class RuntimeAdmissionDiagnostic
{
    // Readiness evidence only: no model transport, journal, budget mutation or
    // quote injection into the independently created live budget is available here.
    internal static async Task<JsonObject> CheckExchangeRateAsync(ModelUsageCostEstimate? pricing,
        string? budgetCurrency, IExchangeRateProvider rates, CancellationToken ct)
    {
        var report = new JsonObject { ["status"] = "blocked", ["checkedAtUtc"] = DateTimeOffset.UtcNow,
            ["sourceCurrency"] = pricing?.Currency, ["targetCurrency"] = budgetCurrency,
            ["quoteChecks"] = 0, ["modelCalls"] = 0 };
        if (pricing is null || pricing.Amount < 0 || string.IsNullOrWhiteSpace(budgetCurrency))
        { report["reason"] = "pricing_or_budget_unavailable"; return report; }
        ct.ThrowIfCancellationRequested();
        report["quoteChecks"] = 1;
        CurrencyExchangeQuote? quote;
        try { quote = await rates.GetQuoteAsync(pricing.Currency, budgetCurrency, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        { report["reason"] = "exchange_rate_exception"; report["exceptionType"] = error.GetType().Name; return report; }
        report["quotePresent"] = quote is not null;
        if (quote is null || quote.Rate <= 0 || quote.AsOfUtc == default || quote.AsOfUtc > DateTimeOffset.UtcNow ||
            string.IsNullOrWhiteSpace(quote.Source) || quote.Source.Length > 160 ||
            !string.Equals(quote.SourceCurrency, pricing.Currency, StringComparison.Ordinal) ||
            !string.Equals(quote.TargetCurrency, budgetCurrency, StringComparison.Ordinal))
        { report["reason"] = "exchange_rate_unavailable_or_invalid"; return report; }
        report["status"] = "passed";
        report["quoteDateUtc"] = quote.AsOfUtc;
        return report;
    }
}
