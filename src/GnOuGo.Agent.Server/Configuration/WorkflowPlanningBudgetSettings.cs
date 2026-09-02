using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Agent.Server.Configuration;

public sealed class WorkflowPlanningBudgetSettings
{
    public const string SectionName = "WorkflowPlanningBudget";

    public decimal Amount { get; set; } = 50m;
    public string Currency { get; set; } = "EUR";
    public string EcbEndpoint { get; set; } = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
    public int ExchangeRateTimeoutSeconds { get; set; } = 10;
    public int MaxQuoteAgeDays { get; set; } = 7;
    public DateTimeOffset? StaticRatesAsOfUtc { get; set; }
    public Dictionary<string, decimal> StaticRates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (Amount <= 0)
            throw new InvalidOperationException($"{SectionName}:Amount must be positive.");
        Currency = NormalizeCurrency(Currency, $"{SectionName}:Currency");
        if (!Uri.TryCreate(EcbEndpoint, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{SectionName}:EcbEndpoint must be an absolute HTTPS URI.");
        }
        if (ExchangeRateTimeoutSeconds <= 0)
            throw new InvalidOperationException($"{SectionName}:ExchangeRateTimeoutSeconds must be positive.");
        if (MaxQuoteAgeDays <= 0)
            throw new InvalidOperationException($"{SectionName}:MaxQuoteAgeDays must be positive.");
        if (StaticRates.Count > 0 && StaticRatesAsOfUtc is null)
            throw new InvalidOperationException($"{SectionName}:StaticRatesAsOfUtc is required when static rates are configured.");
        if (StaticRatesAsOfUtc > DateTimeOffset.UtcNow)
            throw new InvalidOperationException($"{SectionName}:StaticRatesAsOfUtc cannot be in the future.");
        foreach (var (pair, rate) in StaticRates)
        {
            _ = ParseCurrencyPair(pair);
            if (rate <= 0)
                throw new InvalidOperationException($"{SectionName}:StaticRates:{pair} must be positive.");
        }
    }

    public MonetaryAmount CreateLimit() => new(Amount, Currency);

    public IReadOnlyList<CurrencyExchangeQuote> CreateStaticQuotes()
    {
        if (StaticRates.Count == 0)
            return Array.Empty<CurrencyExchangeQuote>();
        var asOfUtc = StaticRatesAsOfUtc!.Value;
        return StaticRates.Select(pair =>
        {
            var currencies = ParseCurrencyPair(pair.Key);
            return new CurrencyExchangeQuote(
                currencies.Source,
                currencies.Target,
                pair.Value,
                asOfUtc,
                "operator_override");
        }).ToArray();
    }

    private static (string Source, string Target) ParseCurrencyPair(string value)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new InvalidOperationException($"{SectionName}:StaticRates keys must use SOURCE/TARGET format.");
        return (
            NormalizeCurrency(parts[0], $"{SectionName}:StaticRates"),
            NormalizeCurrency(parts[1], $"{SectionName}:StaticRates"));
    }

    private static string NormalizeCurrency(string? value, string setting)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 3 || normalized.Any(static character => character is < 'A' or > 'Z'))
            throw new InvalidOperationException($"{setting} must contain exactly three ASCII letters.");
        return normalized;
    }
}
