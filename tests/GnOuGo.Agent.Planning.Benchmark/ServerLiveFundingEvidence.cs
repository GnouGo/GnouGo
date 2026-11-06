using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.Planning.CompiledModels;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Read-only evidence for an independently started operator session. No host,
// provider client, receipt recovery or financial settlement is constructed here.
internal static class ServerLiveFundingEvidence
{
    internal static async Task RunAsync(string[] args, BenchmarkCampaign campaign, IKeyVaultRecordStore records, string workspace)
    {
        var database = Path.Combine(workspace, ".GnOuGo/data/gnougo-planning-v9.db");
        if (!File.Exists(database)) throw new InvalidOperationException("Existing Server planning storage is unavailable.");
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false };
        var contexts = new PooledDbContextFactory<PlanningDbContext>(new DbContextOptionsBuilder<PlanningDbContext>()
            .UseSqlite(connection.ToString()).UseModel(PlanningDbContextModel.Instance).Options);
        var evidence = await ReadAsync(records, contexts, SchemaPortabilityCampaign.RequiredOption(args, "--tenant"),
            SchemaPortabilityCampaign.RequiredOption(args, "--session"), CancellationToken.None);
        var before = await campaign.InspectAsync();
        var ceiling = before["cost_ceiling_eur"]!.GetValue<decimal>();
        var exposure = before["transport_accounting"]!["cost_upper_bound_eur"]!.GetValue<decimal>();
        evidence["campaign_ceiling_eur"] = ceiling;
        evidence["campaign_exposure_eur"] = exposure;
        evidence["campaign_available_before_external_sessions_eur"] = ceiling - exposure;
        evidence["available_global_budget_eur"] = null; // External exposure requires complete tariff/FX and reservation evidence.
        evidence["paid_dispatch_admitted"] = false;
        evidence["campaign_evidence_hash"] = before["evidence_hash"]!.DeepClone();
        evidence["campaign_unchanged"] = before["evidence_hash"]!.ToString() == (await campaign.InspectAsync())["evidence_hash"]!.ToString();
        Console.WriteLine(evidence.ToJsonString());
    }

    internal static async Task<JsonObject> ReadAsync(IKeyVaultRecordStore records, IDbContextFactory<PlanningDbContext> contexts,
        string tenant, string session, CancellationToken ct)
    {
        var state = await new EfPlanningSessionStore(contexts, records).LoadAsync(tenant, session, ct)
            ?? throw new PlanningConflictException("The exact owned operator session is unavailable.");
        var capture = state.Request.Options["captured_model_metadata"]?.AsObject()
            ?? throw new PlanningConflictException("Historical pricing evidence is unavailable.");
        var provider = capture["provider"]!.ToString(); var model = capture["model"]!.ToString(); var type = capture["providerType"]!.ToString();
        var metadata = capture["metadata"]!.Deserialize(ModelMetadataJsonContext.Default.LLMModelMetadata)!;
        var options = new LLMOptions { DefaultProvider = provider, DefaultModel = model,
            Models = { [provider] = new() { Type = type, RequestPolicy = capture["requestPolicy"]!.Deserialize(ModelMetadataJsonContext.Default.LLMProviderRequestPolicyOptions)! } },
            CapturedModelMetadata = new(StringComparer.OrdinalIgnoreCase) { [ModelMetadataCatalog.ModelIdentityKey(type, model)] = metadata } };
        var estimator = new ModelMetadataUsageCostEstimator(options);
        var budgetRow = await records.GetAsync("agent-planning-budgets-v11", tenant, session, EfPlanningSessionStore.Author, ct);
        var budget = budgetRow is null ? state.Usage : JsonSerializer.Deserialize(budgetRow.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var indexes = await db.Calls.AsNoTracking().Where(c => c.TenantId == tenant && c.SessionId == session).ToArrayAsync(ct);
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal); var completed = 0; var unresolved = 0;
        long input = 0, output = 0, cacheWrites = 0; decimal partialCost = 0;
        var journalHashes = new List<string>();
        foreach (var index in indexes)
        {
            ct.ThrowIfCancellationRequested();
            var requestRow = await records.GetAsync("agent-planning-model-requests-v11", tenant, index.PayloadKey, EfPlanningSessionStore.Author, ct);
            var receiptRow = await records.GetAsync("agent-planning-model-receipts-v11", tenant, index.PayloadKey, EfPlanningSessionStore.Author, ct);
            if (requestRow is null || index.PayloadKey != session + ":" + index.RequestHash) { Missing("owned_request_unavailable"); continue; }
            var request = JsonSerializer.Deserialize(requestRow.Value, PlanningJsonContext.Default.LLMRequest)!;
            if (request.ClientRequestId != index.RequestHash || request.Provider != provider || request.Model != model)
                throw new PlanningConflictException("The retained request does not match this session's captured provider/model identity.");
            journalHashes.Add(PlanningGraphCompiler.Fingerprint(requestRow.Value));
            if (receiptRow is null)
            {
                unresolved++;
                var http = await records.GetAsync("agent-planning-model-http-v11", tenant, index.RequestHash, EfPlanningSessionStore.Author, ct);
                if (http is not null) journalHashes.Add(PlanningGraphCompiler.Fingerprint(http.Value));
                Missing("unresolved_dispatch_retained"); continue;
            }
            journalHashes.Add(PlanningGraphCompiler.Fingerprint(receiptRow.Value));
            var receipt = JsonSerializer.Deserialize(receiptRow.Value, PlanningJsonContext.Default.LLMResponse)!;
            var usage = receipt.Usage;
            long? Count(string name, string alternate) => (usage?[name] ?? usage?[alternate]) is JsonValue value && value.TryGetValue<long>(out var count) && count >= 0 ? count : null;
            if (Count("input_tokens", "prompt_tokens") is not { } usedInput || Count("output_tokens", "completion_tokens") is not { } usedOutput)
            { Missing("usage_unavailable"); continue; }
            completed++; input += usedInput; output += usedOutput;
            var details = usage?["input_tokens_details"] ?? usage?["prompt_tokens_details"];
            var writes = (details?["cache_write_tokens"] ?? usage?["cache_write_tokens"])?.GetValue<long>() ?? 0;
            cacheWrites += writes;
            try
            {
                var price = estimator.EstimateReportedCostWithCurrency(model, usedInput, usedOutput, usage, provider);
                if (price is null) Missing("pricing_unavailable");
                else partialCost += price.Amount;
            }
            catch (InvalidOperationException) when (writes > 0 && metadata.Pricing?.CacheWriteInputPer1MTokens is null)
            { Missing("cache_write_tariff_unavailable"); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { Missing("pricing_condition_unavailable"); }
        }
        if (indexes.Length < state.ModelCalls) Missing("model_index_incomplete");
        return new()
        {
            ["evidence"] = "read-only owned Server request/receipt/HTTP journals; captured tariffs only",
            ["inspected_at_utc"] = DateTimeOffset.UtcNow, ["tenant"] = tenant, ["session"] = session, ["revision"] = state.Revision,
            ["status"] = state.Status, ["provider"] = provider, ["model"] = model,
            ["indexed_calls"] = indexes.Length, ["completed_usage_receipts"] = completed, ["unresolved_requests"] = unresolved,
            ["measured_input_tokens"] = input, ["measured_output_tokens_including_reasoning"] = output, ["reported_cache_write_tokens"] = cacheWrites,
            ["partially_priced_cost"] = partialCost, ["partially_priced_currency"] = metadata.Pricing?.Currency,
            ["cost_complete"] = reasons.Count == 0, ["unavailable_reasons"] = new JsonObject(reasons.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value)))),
            ["captured_input_price_per_million"] = metadata.Pricing?.InputPer1MTokens,
            ["captured_output_price_per_million"] = metadata.Pricing?.OutputPer1MTokens,
            ["captured_cache_write_price_per_million"] = metadata.Pricing?.CacheWriteInputPer1MTokens,
            ["budget_uncertain_calls"] = budget?.UncertainCalls, ["budget_cost_available"] = budget?.CostEstimateAvailable,
            ["retained_uncertain_reservation"] = budget?.JournaledAttempts?.Values.Where(a => a.VerifiedUsage is null).Sum(a => a.ReservedEstimatedCost),
            ["budget_currency"] = budget?.EstimatedCostCurrency,
            ["journal_evidence_hash"] = PlanningGraphCompiler.Fingerprint(string.Join('\n', journalHashes.Order(StringComparer.Ordinal))),
            ["model_dispatches_by_inspection"] = 0, ["historical_accounting_modified"] = false
        };
        void Missing(string reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
    }
}
