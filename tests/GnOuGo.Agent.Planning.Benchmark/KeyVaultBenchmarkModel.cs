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
using GnOuGo.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Live model only; no MCP connections. One encrypted EUR 50 ledger spans baseline and candidate runs.</summary>
internal sealed class KeyVaultBenchmarkModel(string providerName, string model, string campaign, string root) : ILLMClient
{
    private const string Author = "GnOuGo.Planning.Benchmark";
    public async Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
    {
        if (campaign.Length is < 1 or > 80 || campaign.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid campaign identifier.");
        var leasePath = GnOuGoWorkspace.ResolveDatabasePath(null, root, ".GnOuGo/data/planning-evaluation/" + campaign + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        await using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, root);
        var requestKey = campaign + ":" + (request.ClientRequestId ?? throw new InvalidOperationException("A reserved request identity is required."));
        var completed = await records.GetAsync("planning-evaluation-receipts", "benchmark", requestKey, Author, ct);
        if (completed is not null) return JsonSerializer.Deserialize(completed.Value, PlanningJsonContext.Default.LLMResponse)!;
        foreach (var pending in (await records.ListAsync("planning-evaluation-requests", "benchmark", Author, ct)).Where(r => r.Key.StartsWith(campaign + ":", StringComparison.Ordinal)))
            if (await records.GetAsync("planning-evaluation-receipts", "benchmark", pending.Key, Author, ct) is null)
                throw new InvalidOperationException("The campaign has uncertain usage; no request was dispatched.");

        var services = new ServiceCollection(); services.AddLogging();
        var vault = KeyVaultDatabasePathResolver.Resolve(null, root);
        services.AddDbContext<KeyVaultDbContext>(o => o.UseSqlite("Data Source=" + vault)); services.AddScoped<KeyVaultService>();
        await using var provider = services.BuildServiceProvider();
        var config = new KeyVaultRuntimeConfigStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<KeyVaultRuntimeConfigStore>.Instance);
        var options = await config.BuildEffectiveOptionsAsync(new LLMOptions { DefaultProvider = providerName, DefaultModel = model }, ct);
        if (options.DefaultModel != model) throw new InvalidOperationException("Configured model changed; baseline and candidate must use the same model.");
        using var http = LLMHttpClientFactory.Create(options.DangerousAcceptAnyServerCertificate, TimeSpan.FromMinutes(10));
        var client = new RoutingLLMClientAdapter(new RoutingLLMClient(options, RoutingLLMClient.CreateDefaultProviders(http)));
        var rates = new EcbExchangeRateProvider(http); var estimator = new ModelMetadataUsageCostEstimator(options);
        var saved = await records.GetAsync("planning-evaluation-budgets", "benchmark", campaign, Author, ct);
        var initial = saved is null ? null : JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var budget = new LLMUsageBudgetScope(new() { MaxEstimatedCost = new(50m, "EUR") }, initial, sink: new Sink(records, campaign), exchangeRateProvider: rates);
        // Clone: accounting/provider defaults cannot mutate the planner's reserved request.
        var dispatched = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        dispatched.Provider = options.DefaultProvider; dispatched.Model = options.DefaultModel;
        var json = JsonSerializer.Serialize(dispatched, PlanningJsonContext.Default.LLMRequest);
        var ceiling = estimator.EstimateCostWithCurrency(dispatched.Model, Math.Max(96_000, Encoding.UTF8.GetByteCount(json)), dispatched.MaxTokens ?? 32_768, dispatched.Provider)
            ?? throw new InvalidOperationException("No model price metadata.");
        var quote = await rates.GetQuoteAsync(ceiling.Currency, "EUR", ct) ?? throw new InvalidOperationException("No currency quote.");
        if (budget.Snapshot.EstimatedCost + ceiling.Amount * quote.Rate > 50m)
            throw new InvalidOperationException("The EUR 50 campaign cannot cover this request's conservative upper bound.");
        await records.UpsertAsync("planning-evaluation-requests", "benchmark", requestKey, json, Author, ct);
        var before = budget.Snapshot.EstimatedCost;
        var response = await budget.CallAsync(client, estimator, dispatched, "benchmark", ct);
        response.Usage ??= new JsonObject(); response.Usage["benchmark_cost_eur"] = budget.Snapshot.EstimatedCost - before;
        await records.UpsertAsync("planning-evaluation-receipts", "benchmark", requestKey, JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse), Author, ct);
        return response;
    }
    private sealed class Sink(IKeyVaultRecordStore records, string campaign) : ILLMUsageBudgetSink
    {
        public async ValueTask PersistAsync(LLMUsageBudgetSnapshot snapshot, CancellationToken ct) => await records.UpsertAsync("planning-evaluation-budgets", "benchmark", campaign,
            JsonSerializer.Serialize(snapshot, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), Author, ct);
    }
}
