using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Data;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>One configured live model and one EUR 50 ledger; workflow effects never leave the fake integrations.</summary>
internal sealed class KeyVaultBenchmarkModel : ILLMClient, IDisposable
{
    private readonly BenchmarkCampaign _campaign;
    private readonly LLMOptions _options;
    private readonly HttpClient _http;
    private readonly ILLMClient _client;
    private readonly EcbExchangeRateProvider _rates;
    private readonly ModelMetadataUsageCostEstimator _estimator;
    private KeyVaultBenchmarkModel(BenchmarkCampaign campaign, LLMOptions options)
    {
        _campaign = campaign; _options = options;
        _http = LLMHttpClientFactory.Create(options.DangerousAcceptAnyServerCertificate, TimeSpan.FromMinutes(10));
        _client = new RoutingLLMClientAdapter(new RoutingLLMClient(options, RoutingLLMClient.CreateDefaultProviders(_http)));
        _rates = new(_http); _estimator = new(options);
    }
    internal string Model => _options.DefaultModel;
    internal string Provider => _options.DefaultProvider;
    internal static async Task<KeyVaultBenchmarkModel> CreateAsync(string providerName, string? expectedModel, BenchmarkCampaign campaign, string root, CancellationToken ct)
    {
        var services = new ServiceCollection(); services.AddLogging();
        var vault = KeyVaultDatabasePathResolver.Resolve(null, root);
        services.AddDbContext<KeyVaultDbContext>(o => o.UseSqlite("Data Source=" + vault)); services.AddScoped<KeyVaultService>();
        await using var provider = services.BuildServiceProvider();
        var config = new KeyVaultRuntimeConfigStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<KeyVaultRuntimeConfigStore>.Instance);
        var options = await config.BuildEffectiveOptionsAsync(new LLMOptions { DefaultProvider = providerName, DefaultModel = expectedModel ?? "" }, ct);
        if (string.IsNullOrWhiteSpace(options.DefaultModel) || expectedModel is not null && options.DefaultModel != expectedModel)
            throw new InvalidOperationException("The configured model does not match the evaluation model.");
        var settings = options.ResolveProvider(options.DefaultProvider) ?? throw new InvalidOperationException("No configured provider.");
        await campaign.PinAsync(new() { ["provider"] = options.DefaultProvider, ["model"] = options.DefaultModel, ["endpoint"] = settings.Url,
            ["request_policy"] = JsonSerializer.SerializeToNode(settings.RequestPolicy), ["reasoning"] = "medium", ["max_input_tokens"] = 96_000,
            ["max_output_tokens"] = 32_768, ["max_calls"] = 8, ["max_repairs"] = 2, ["cost_ceiling_eur"] = 50 }, ct);
        return new(campaign, options);
    }
    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        LLMUsageBudgetScope? budget = null;
        var dispatched = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        dispatched.Provider = Provider; dispatched.Model = Model;
        return await _campaign.CallAsync(request, async token =>
        {
            var saved = await _campaign.Records.GetAsync("planning-evaluation-budgets", "benchmark", _campaign.Id, BenchmarkCampaign.Author, token);
            var initial = saved is null ? null : JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
            budget = new(new() { MaxEstimatedCost = new(50m, "EUR") }, initial, sink: new Sink(_campaign), exchangeRateProvider: _rates);
            var json = JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest);
            var ceiling = _estimator.EstimateCostWithCurrency(Model, Math.Max(96_000, Encoding.UTF8.GetByteCount(json)), dispatched.MaxTokens ?? 32_768, Provider)
                ?? throw new InvalidOperationException("No model price metadata.");
            var quote = await _rates.GetQuoteAsync(ceiling.Currency, "EUR", token) ?? throw new InvalidOperationException("No currency quote.");
            if (budget.Snapshot.EstimatedCost + ceiling.Amount * quote.Rate > 50m)
            { _campaign.BudgetExceeded(); throw new InvalidOperationException("The EUR 50 campaign cannot cover this request's conservative upper bound."); }
        }, async token =>
        {
            var before = budget!.Snapshot.EstimatedCost;
            var response = await budget.CallAsync(_client, _estimator, dispatched, "benchmark", token);
            response.Usage ??= new JsonObject(); response.Usage["benchmark_cost_eur"] = budget.Snapshot.EstimatedCost - before;
            return response;
        }, ct);
    }
    public void Dispose() => _http.Dispose();
    private sealed class Sink(BenchmarkCampaign campaign) : ILLMUsageBudgetSink
    {
        public async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct) => await campaign.Records.UpsertAsync("planning-evaluation-budgets", "benchmark", campaign.Id,
            JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), BenchmarkCampaign.Author, ct);
    }
}
