using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Integrations;

/// <summary>
/// Configuration for the ECB euro reference-rate integration.
/// </summary>
public sealed record EcbExchangeRateProviderOptions
{
    public Uri Endpoint { get; init; } = new("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml");
    public TimeSpan MaxQuoteAge { get; init; } = TimeSpan.FromDays(7);
    public IReadOnlyList<CurrencyExchangeQuote> StaticQuotes { get; init; } = Array.Empty<CurrencyExchangeQuote>();
}

/// <summary>
/// Resolves currency conversions from operator overrides or the ECB's daily
/// EUR-based reference-rate table. No workflow or provider data is sent.
/// </summary>
public sealed class EcbExchangeRateProvider : IExchangeRateProvider
{
    private const int MaximumResponseCharacters = 256_000;
    private static readonly XNamespace ReferenceRateNamespace = "http://www.ecb.int/vocabulary/2002-08-01/eurofxref";
    private readonly HttpClient _httpClient;
    private readonly EcbExchangeRateProviderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private EcbRateTable? _cached;

    public EcbExchangeRateProvider(
        HttpClient httpClient,
        EcbExchangeRateProviderOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? new EcbExchangeRateProviderOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(_options);
    }

    public async ValueTask<CurrencyExchangeQuote?> GetQuoteAsync(
        string sourceCurrency,
        string targetCurrency,
        CancellationToken ct)
    {
        var source = NormalizeCurrency(sourceCurrency);
        var target = NormalizeCurrency(targetCurrency);
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return new CurrencyExchangeQuote(
                source,
                target,
                1m,
                _timeProvider.GetUtcNow(),
                "identity");
        }

        var staticQuote = FindStaticQuote(source, target);
        if (staticQuote is not null)
            return staticQuote;

        var table = _cached;
        if (table is null || IsStale(table.AsOfUtc))
            table = await RefreshAsync(ct).ConfigureAwait(false);
        if (table is null || IsStale(table.AsOfUtc))
            return null;

        var sourceRate = string.Equals(source, "EUR", StringComparison.Ordinal)
            ? 1m
            : table.Rates.GetValueOrDefault(source);
        var targetRate = string.Equals(target, "EUR", StringComparison.Ordinal)
            ? 1m
            : table.Rates.GetValueOrDefault(target);
        if (sourceRate <= 0 || targetRate <= 0)
            return null;

        decimal rate;
        try
        {
            rate = checked(targetRate / sourceRate);
        }
        catch (OverflowException)
        {
            return null;
        }

        return new CurrencyExchangeQuote(
            source,
            target,
            rate,
            table.AsOfUtc,
            "ecb_reference_rates");
    }

    private CurrencyExchangeQuote? FindStaticQuote(string source, string target)
    {
        var direct = _options.StaticQuotes.FirstOrDefault(quote =>
            string.Equals(NormalizeCurrency(quote.SourceCurrency), source, StringComparison.Ordinal)
            && string.Equals(NormalizeCurrency(quote.TargetCurrency), target, StringComparison.Ordinal));
        if (direct is not null)
            return IsUsableStaticQuote(direct) ? direct with { SourceCurrency = source, TargetCurrency = target } : null;

        var inverse = _options.StaticQuotes.FirstOrDefault(quote =>
            string.Equals(NormalizeCurrency(quote.SourceCurrency), target, StringComparison.Ordinal)
            && string.Equals(NormalizeCurrency(quote.TargetCurrency), source, StringComparison.Ordinal));
        if (!IsUsableStaticQuote(inverse))
            return null;

        try
        {
            return new CurrencyExchangeQuote(
                source,
                target,
                checked(1m / inverse!.Rate),
                inverse.AsOfUtc,
                inverse.Source);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private bool IsUsableStaticQuote(CurrencyExchangeQuote? quote)
        => quote is { Rate: > 0 }
           && quote.AsOfUtc != default
           && quote.AsOfUtc <= _timeProvider.GetUtcNow()
           && !IsStale(quote.AsOfUtc)
           && !string.IsNullOrWhiteSpace(quote.Source);

    private bool IsStale(DateTimeOffset asOfUtc)
        => _timeProvider.GetUtcNow() - asOfUtc > _options.MaxQuoteAge;

    private async Task<EcbRateTable?> RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is { } current && !IsStale(current.AsOfUtc))
                return current;

            using var response = await _httpClient.GetAsync(
                _options.Endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength is > MaximumResponseCharacters)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumResponseCharacters
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var datedCube = document.Descendants(ReferenceRateNamespace + "Cube")
                .FirstOrDefault(element => element.Attribute("time") is not null);
            if (datedCube?.Attribute("time")?.Value is not { } dateText
                || !DateOnly.TryParseExact(
                    dateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                return null;
            }

            var asOfUtc = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            if (asOfUtc > _timeProvider.GetUtcNow() || IsStale(asOfUtc))
                return null;

            var rates = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var cube in datedCube.Elements(ReferenceRateNamespace + "Cube"))
            {
                var currency = cube.Attribute("currency")?.Value;
                var rateText = cube.Attribute("rate")?.Value;
                if (currency is null || rateText is null
                    || !decimal.TryParse(rateText, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
                    || rate <= 0)
                {
                    return null;
                }

                rates[NormalizeCurrency(currency)] = rate;
            }
            if (rates.Count == 0)
                return null;

            _cached = new EcbRateTable(asOfUtc, rates);
            return _cached;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or FormatException or ArgumentException)
        {
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static string NormalizeCurrency(string? currency)
    {
        var normalized = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 3 || normalized.Any(static character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Currency codes must contain exactly three ASCII letters.", nameof(currency));
        return normalized;
    }

    private static void ValidateOptions(EcbExchangeRateProviderOptions options)
    {
        if (!options.Endpoint.IsAbsoluteUri
            || !string.Equals(options.Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The ECB exchange-rate endpoint must be an absolute HTTPS URI.", nameof(options));
        }
        if (options.MaxQuoteAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum exchange-rate quote age must be positive.");
        foreach (var quote in options.StaticQuotes)
        {
            _ = NormalizeCurrency(quote.SourceCurrency);
            _ = NormalizeCurrency(quote.TargetCurrency);
            if (quote.Rate <= 0 || quote.AsOfUtc == default || string.IsNullOrWhiteSpace(quote.Source))
                throw new ArgumentException("Every static exchange-rate quote must be complete and positive.", nameof(options));
        }
    }

    private sealed record EcbRateTable(
        DateTimeOffset AsOfUtc,
        IReadOnlyDictionary<string, decimal> Rates);
}
