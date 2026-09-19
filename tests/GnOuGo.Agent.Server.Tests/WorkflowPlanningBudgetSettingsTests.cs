using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Flow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Server.Tests;

public sealed class WorkflowPlanningBudgetSettingsTests
{
    [Fact]
    public void Defaults_AreFiftyEuros()
    {
        var settings = new WorkflowPlanningBudgetSettings();

        settings.Validate();

        Assert.Equal(50m, settings.Amount);
        Assert.Equal("EUR", settings.Currency);
        Assert.Equal(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(settings.ExchangeRateTimeoutSeconds));
        Assert.Equal(7, settings.MaxQuoteAgeDays);
    }

    [Fact]
    public void Validate_NormalizesOverridesAndBuildsStaticQuote()
    {
        var asOfUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var settings = new WorkflowPlanningBudgetSettings
        {
            Amount = 25m,
            Currency = " usd ",
            StaticRatesAsOfUtc = asOfUtc,
            StaticRates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["usd/eur"] = 0.9m
            }
        };

        settings.Validate();
        var quote = Assert.Single(settings.CreateStaticQuotes());

        Assert.Equal("USD", settings.Currency);
        Assert.Equal(25m, settings.CreateLimit().Amount);
        Assert.Equal("USD", quote.SourceCurrency);
        Assert.Equal("EUR", quote.TargetCurrency);
        Assert.Equal(0.9m, quote.Rate);
        Assert.Equal("operator_override", quote.Source);
    }

    [Theory]
    [InlineData(0, "EUR")]
    [InlineData(50, "EU")]
    [InlineData(50, "EU1")]
    public void Validate_RejectsInvalidLimit(decimal amount, string currency)
    {
        var settings = new WorkflowPlanningBudgetSettings
        {
            Amount = amount,
            Currency = currency
        };

        Assert.Throws<InvalidOperationException>(settings.Validate);
    }

    [Fact]
    public void WebHost_UsesNormalConfigurationOverridesAndRegistersExchangeProvider()
    {
        var contentRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src",
            "GnOuGo.Agent.Server"));
        var args = TelemetryTestHostArgs.Create()
            .Concat([
                "--WorkflowPlanningBudget:Amount=12.5",
                "--WorkflowPlanningBudget:Currency=GBP"
            ])
            .ToArray();

        using var app = GnOuGoAgentWebHost.Build(
            args,
            urls: "http://127.0.0.1:0",
            contentRoot: contentRoot,
            enableHttpsRedirection: false);

        var settings = app.Services
            .GetRequiredService<IOptions<WorkflowPlanningBudgetSettings>>()
            .Value;
        Assert.Equal(12.5m, settings.Amount);
        Assert.Equal("GBP", settings.Currency);
        Assert.NotNull(app.Services.GetRequiredService<IExchangeRateProvider>());
    }
}
