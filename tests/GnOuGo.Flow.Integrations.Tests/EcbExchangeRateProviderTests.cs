using System.Net;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Integrations.Tests;

public sealed class EcbExchangeRateProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetQuoteAsync_DerivesCrossRateAndCachesFeed()
    {
        var handler = new RecordingHandler(EcbXml("2026-09-01", ("USD", "1.2000"), ("GBP", "0.8000")));
        var provider = CreateProvider(handler);

        var first = await provider.GetQuoteAsync("USD", "GBP", TestContext.Current.CancellationToken);
        var second = await provider.GetQuoteAsync("EUR", "USD", TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(0.8m / 1.2m, first.Rate);
        Assert.Equal("ecb_reference_rates", first.Source);
        Assert.NotNull(second);
        Assert.Equal(1.2m, second.Rate);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastRequestMethod);
        Assert.Empty(handler.LastRequestHeaders);
    }

    [Fact]
    public async Task GetQuoteAsync_StaticOverridePrecedesNetworkAndSupportsInverse()
    {
        var handler = new RecordingHandler("unused");
        var provider = CreateProvider(
            handler,
            new EcbExchangeRateProviderOptions
            {
                StaticQuotes =
                [
                    new CurrencyExchangeQuote("EUR", "USD", 1.25m, Now.AddHours(-1), "operator_override")
                ]
            });

        var quote = await provider.GetQuoteAsync("USD", "EUR", TestContext.Current.CancellationToken);

        Assert.NotNull(quote);
        Assert.Equal(0.8m, quote.Rate);
        Assert.Equal("operator_override", quote.Source);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetQuoteAsync_RejectsStaleOrMalformedFeed()
    {
        var stale = CreateProvider(new RecordingHandler(EcbXml("2026-08-20", ("USD", "1.2"))));
        var malformed = CreateProvider(new RecordingHandler("<not-ecb />"));

        Assert.Null(await stale.GetQuoteAsync("USD", "EUR", TestContext.Current.CancellationToken));
        Assert.Null(await malformed.GetQuoteAsync("USD", "EUR", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetQuoteAsync_RejectsUnsupportedCurrency()
    {
        var provider = CreateProvider(new RecordingHandler(EcbXml("2026-09-01", ("USD", "1.2"))));

        Assert.Null(await provider.GetQuoteAsync("CAD", "EUR", TestContext.Current.CancellationToken));
    }

    private static EcbExchangeRateProvider CreateProvider(
        RecordingHandler handler,
        EcbExchangeRateProviderOptions? options = null)
        => new(
            new HttpClient(handler),
            options,
            new FixedTimeProvider(Now));

    private static string EcbXml(string date, params (string Currency, string Rate)[] rates)
    {
        var cubes = string.Join(
            string.Empty,
            rates.Select(static item => $"<e:Cube currency=\"{item.Currency}\" rate=\"{item.Rate}\"/>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://www.gesmes.org/xml/2002-08-01"
                      xmlns:e="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
              <e:Cube><e:Cube time="{date}">{cubes}</e:Cube></e:Cube>
            </Envelope>
            """;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpMethod? LastRequestMethod { get; private set; }
        public IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> LastRequestHeaders { get; private set; }
            = Array.Empty<KeyValuePair<string, IEnumerable<string>>>();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestMethod = request.Method;
            LastRequestHeaders = request.Headers.ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
        }
    }
}
