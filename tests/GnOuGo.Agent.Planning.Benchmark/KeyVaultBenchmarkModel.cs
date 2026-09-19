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
        var retryConfiguration = JsonSerializer.SerializeToNode(settings.RetryPolicy)!.AsObject();
        var pinnedRetry = await campaign.LoadAsync("planning-evaluation-configuration", "http-retry-policy", ct);
        if (pinnedRetry is not null && !JsonNode.DeepEquals(pinnedRetry, retryConfiguration))
            throw new InvalidOperationException("The campaign HTTP retry policy changed.");
        if (pinnedRetry is null) await campaign.SaveAsync("planning-evaluation-configuration", "http-retry-policy", retryConfiguration, ct);
        return new(campaign, options);
    }
    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        var dispatched = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        dispatched.Provider = Provider; dispatched.Model = Model;
        dispatched.DisableTransportRetries = false; // AI.Core owns the only retry loop.
        if (dispatched.Tools is { Count: > 0 } || dispatched.UseBackgroundMode)
            throw new InvalidOperationException("Benchmark recovery permits only side-effect-free synchronous generation.");
        BenchmarkHttpJournal? journal = null;
        return await _campaign.CallAsync(request, async token =>
        {
            var json = JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest);
            var input = Math.Max(96_000, Encoding.UTF8.GetByteCount(json));
            var output = dispatched.MaxTokens ?? 32_768;
            var ceiling = _estimator.EstimateCostWithCurrency(Model, input, output, Provider)
                ?? throw new InvalidOperationException("No model price metadata.");
            var quote = await _rates.GetQuoteAsync(ceiling.Currency, "EUR", token) ?? throw new InvalidOperationException("No currency quote.");
            journal = new(_campaign, request.ClientRequestId!, input, output, ceiling.Amount * quote.Rate);
            await journal.PrepareAsync(token);
        }, async token =>
        {
            using var context = new LLMHttpRetryContext(request.ClientRequestId!, journal!).Activate();
            var response = await _client.CallAsync(dispatched, token);
            long ReadTokens(params string[] keys)
            {
                foreach (var key in keys)
                    if (long.TryParse(response.Usage?[key]?.ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0) return value;
                throw new InvalidOperationException("Provider usage is missing; the reservation remains conservative.");
            }
            var input = ReadTokens("input_tokens", "prompt_tokens"); var output = ReadTokens("output_tokens", "completion_tokens");
            var estimate = _estimator.EstimateCostWithCurrency(Model, input, output, Provider) ?? throw new InvalidOperationException("No model price metadata.");
            var quote = await _rates.GetQuoteAsync(estimate.Currency, "EUR", CancellationToken.None) ?? throw new InvalidOperationException("No currency quote.");
            response.Usage = await journal!.CompleteAsync(new() { ["input_tokens"] = input, ["output_tokens"] = output,
                ["total_tokens"] = checked(input + output), ["benchmark_cost_eur"] = estimate.Amount * quote.Rate }, CancellationToken.None);
            return response;
        }, ct, allowHttpRecovery: true);
    }

    internal async Task<JsonObject?> PartialUsageAsync(string id, CancellationToken ct)
    {
        var record = await _campaign.LoadAsync(BenchmarkHttpJournal.Collection, id, ct);
        if (record?["usage"] is JsonObject known) return known.DeepClone().AsObject();
        if (record?["transport"]?["Attempts"] is not JsonArray attempts) return null;
        var pending = attempts.Count(a => a!["Status"] is null || a["Status"]!.GetValue<int>() is >= 200 and < 300);
        return new() { ["transport_attempts"] = attempts.Count, ["uncertain_attempts"] = pending,
            ["reserved_input_tokens"] = pending * record["input_ceiling"]!.GetValue<long>(),
            ["reserved_output_tokens"] = pending * record["output_ceiling"]!.GetValue<long>(),
            ["reserved_cost_eur"] = pending * record["cost_ceiling_eur"]!.GetValue<decimal>() };
    }
    public void Dispose() => _http.Dispose();
}
