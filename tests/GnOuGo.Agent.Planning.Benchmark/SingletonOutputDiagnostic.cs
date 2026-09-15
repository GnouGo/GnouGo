using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Mcp;
using GnOuGo.Agent.Mcp.Services;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Hosting;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.KeyVault.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Planning.Benchmark;

// Isolated, explicitly authorized experiment. Never creates or advances a planning session.
internal static class SingletonOutputDiagnostic
{
    internal const string Identity = "schema5-singleton-output-16384-low-1", Tenant = "planner-progressive";
    internal const string SourceSession = "7a27ef2963ae49589c9b6c4196644fe7", SourceCampaign = "schema5-recursive-partitions-rerun-1";
    private const string Collection = "agent-planning-diagnostics-v5", Author = "GnOuGo.Agent.Planning.Benchmark";
    private const string Decision = "declarations_r_f62567b1da3aa9193fdaad62";
    private const string SourceRequestHash = "2d289a0514c308703e35bae582087c247d2e1b66b8f81097fcd42c978b7abba7";
    private const string SourceReceiptHash = "c851b7af0a1be7d527a54a54d77901fad3cd1bde5525f55086f4e7079b741b3c";

    internal static LLMRequest CreateRequest(LLMRequest source)
    {
        if (source.MaxTokens != 8192 || source.Reasoning != "low" || !source.DisableTransportRetries || !source.RequireOutputTokenLimit ||
            source.StructuredOutputSchema?["properties"] is not JsonObject fields || fields.Count != 1)
            throw new InvalidOperationException("The diagnostic requires the captured low singleton request with transport retries disabled.");
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(source, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        copy.MaxTokens = 16384; copy.ClientRequestId = null;
        copy.ClientRequestId = Identity + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(copy, PlanningJsonContext.Default.LLMRequest));
        RequireOnlyOutputChange(source, copy);
        return copy;
    }

    internal static void RequireOnlyOutputChange(LLMRequest source, LLMRequest request)
    {
        var a = JsonSerializer.SerializeToNode(source, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        var b = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        a.Remove("clientRequestId"); b.Remove("clientRequestId");
        if (request.MaxTokens != 16384) throw new InvalidOperationException("The diagnostic output ceiling was changed or clamped.");
        b["maxTokens"] = a["maxTokens"]?.DeepClone();
        if (!JsonNode.DeepEquals(a, b)) throw new InvalidOperationException("A generation field other than the output ceiling changed.");
    }

    internal static async Task<LLMResponse> ExecuteOnceAsync(LLMRequest source, LLMUsageBudgetSnapshot seed, LLMUsageBudgetLimits limits,
        ILLMClient client, IModelUsageCostEstimator estimator, IExchangeRateProvider? rates,
        IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records, CancellationToken ct)
    {
        var request = CreateRequest(source);
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Sessions.AnyAsync(ct) || await db.Calls.AnyAsync(c => c.TenantId != Tenant || c.SessionId != Identity || c.RequestHash != request.ClientRequestId, ct))
            throw new InvalidOperationException("The diagnostic index must contain only this one request and no planning session.");
        var saved = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, Identity, EfPlanningSessionStore.Author, ct);
        var snapshot = saved is null ? seed : JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        if (saved is null) await new PlanningBudgetSink(records, Tenant, Identity).PersistAsync(seed, ct);
        // Agent.Server accounts active time separately; archive/human-wait time
        // must not become model execution time. RunAsync supplies the remaining active deadline.
        var allowance = limits with { MaxCalls = Math.Min(limits.MaxCalls ?? int.MaxValue, checked((int)seed.Calls + 1)), MaxElapsed = null };
        var budget = new LLMUsageBudgetScope(allowance, snapshot, sink: new PlanningBudgetSink(records, Tenant, Identity), exchangeRateProvider: rates);
        var generation = new PlanningGenerationOptions { MaxOutputTokens = 16384 };
        PlanningGenerationPolicy.Apply(request, generation);
        RequireOnlyOutputChange(source, request);
        return await new PlanningModelJournal(client, contexts, records, Tenant, Identity, budget, estimator, generation).CallAsync(request, ct);
    }

    internal static JsonObject Assess(LLMResponse response, JsonNode schema)
    {
        long? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var number) ? number : null;
        var output = ProgressiveReport.Usage(response, true);
        var reasoning = Number(response.Usage?["completion_tokens_details"]?["reasoning_tokens"] ?? response.Usage?["output_tokens_details"]?["reasoning_tokens"]);
        var details = response.Usage?["completion_tokens_details"] ?? response.Usage?["output_tokens_details"];
        var canSubtract = output is not null && reasoning is not null && reasoning <= output &&
            new[] { "audio_tokens", "accepted_prediction_tokens", "rejected_prediction_tokens" }.All(k => details?[k] is null || Number(details[k]) == 0);
        var findings = response.Json is null ? null : PlanningContractValidation.ValidateInstance(response.Json, schema);
        return new JsonObject
        {
            ["completion"] = response.CompletionStatus ?? (response.Json is not null ? "completed" : "unknown"),
            ["inputTokens"] = ProgressiveReport.Usage(response, false), ["outputTokens"] = output, ["reasoningTokens"] = reasoning,
            ["finalAnswerTokens"] = canSubtract ? output - reasoning : null,
            ["answerTokenAttribution"] = canSubtract ? "derived: output tokens minus provider-reported reasoning tokens" : "unknown",
            ["schemaValid"] = findings is null ? null : findings.Count == 0,
            ["schemaFindings"] = findings is null ? null : new JsonArray(findings.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["assignment"] = response.Json?.DeepClone()
        };
    }

    internal static async Task RunAsync(string root, IKeyVaultRecordStore records,
        IDbContextFactory<PlanningDbContext> sourceContexts, IDbContextFactory<PlanningDbContext> contexts)
    {
        var source = await new EfPlanningSessionStore(sourceContexts, records).LoadAsync(Tenant, SourceSession, default)
            ?? throw new InvalidOperationException("The captured source is missing.");
        if (source.Revision != 21 || source.Status != PlanningStatus.Stopped || source.TechnicalStop?.Code != "DECISION_OUTPUT_LIMIT")
            throw new InvalidOperationException("The archived singleton stop changed.");
        var page = source.DecisionPages.Single(p => p.Decisions.SequenceEqual(new[] { Decision }));
        var key = SourceSession + ":" + page.RequestId;
        var issued = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, key, EfPlanningSessionStore.Author);
        var receipt = await records.GetAsync(PlanningModelJournal.Collection, Tenant, key, EfPlanningSessionStore.Author);
        var request = JsonSerializer.Deserialize(issued!.Value, PlanningJsonContext.Default.LLMRequest)!;
        var originalId = request.ClientRequestId; request.ClientRequestId = null;
        if (PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)) != SourceRequestHash ||
            receipt is null || PlanningGraphCompiler.Fingerprint(receipt.Value) != SourceReceiptHash)
            throw new InvalidOperationException("The captured request or receipt fingerprint differs.");
        request.ClientRequestId = originalId;
        if (request.Model != "gpt-5.5-2026-04-24" || JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse)!.CompletionStatus != "output_limit")
            throw new InvalidOperationException("The captured model or completion differs.");
        var campaign = JsonNode.Parse((await records.GetAsync("agent-planning-progressive-campaigns-v5", Tenant, SourceCampaign, Author))!.Value)!;
        foreach (var binary in campaign["binaries"]!.AsObject().Where(b => b.Key != "GnOuGo.Agent.Planning.Benchmark.dll"))
            if (Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, binary.Key)))) != binary.Value!.ToString())
                throw new InvalidOperationException("A frozen production binary changed.");
        var before = await ArchiveFingerprintAsync(records, sourceContexts);
        var originalBudget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, SourceSession, EfPlanningSessionStore.Author)
            ?? throw new InvalidOperationException("The cumulative source budget is missing.");
        var seed = JsonSerializer.Deserialize(originalBudget.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        var limits = PlanningBudgetOptions.Parse(source.Request.Options) ?? throw new InvalidOperationException("Captured global budgets are required.");
        var remaining = campaign["limits"]!["activeMilliseconds"]!.GetValue<long>() - source.ActiveMilliseconds;
        if (remaining <= 0) throw new InvalidOperationException("The source active-time budget is exhausted.");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var ct = cancel.Token;
        var services = new ServiceCollection().AddLogging();
        services.AddKeyVaultMcpPersistence(KeyVaultDatabasePathResolver.Resolve(null, root));
        services.AddAgentMcpPersistence(AgentMcpHostingExtensions.ResolveDatabasePath(null, root));
        var options = new LLMRuntimeOptionsStore(Options.Create(new LLMOptions { DefaultProvider = request.Provider!, DefaultModel = request.Model }), NullLogger<LLMRuntimeOptionsStore>.Instance);
        services.AddSingleton(options).AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>().AddSingleton<ILLMCapabilityResolver, FlowLlmCapabilityResolver>();
        await using var provider = services.BuildServiceProvider();
        var vault = provider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
        await using (var scope = provider.CreateAsyncScope())
            await ProgressiveRules.HydrateModelAsync(options, vault, scope.ServiceProvider.GetRequiredService<IUserConfigRepository>(), ct);
        var capabilities = provider.GetRequiredService<ILLMCapabilityResolver>();
        await using var runtime = await new SecureWorkflowRuntimeFactory(options, vault, mcpClientFactoryOverride: new InMemoryMcpClientFactory(), llmCapabilityResolver: capabilities).CreateAsync(ct);
        if (runtime.Options.DefaultProvider != request.Provider || runtime.Options.DefaultModel != request.Model ||
            (await capabilities.SupportedReasoningLevelsAsync(request.Provider, request.Model, ct))?.Contains("low", StringComparer.Ordinal) != true ||
            await capabilities.SupportsStructuredOutputAsync(request.Provider, request.Model, ct) != true)
            throw new InvalidOperationException("The configured model or declared capabilities changed.");
        var diagnostic = CreateRequest(request);
        var manifest = new JsonObject { ["identity"] = Identity, ["sourceSession"] = SourceSession, ["sourceRequest"] = originalId,
            ["sourceRequestHash"] = SourceRequestHash, ["sourceReceiptHash"] = SourceReceiptHash, ["request"] = diagnostic.ClientRequestId,
            ["sourceArchiveFingerprint"] = before, ["sourceBudgetFingerprint"] = PlanningGraphCompiler.Fingerprint(originalBudget.Value),
            ["contextFingerprint"] = PlanningGraphCompiler.Fingerprint(request.Prompt), ["schemaFingerprint"] = PlanningGraphCompiler.Fingerprint(request.StructuredOutputSchema!.ToJsonString()),
            ["outputLimit"] = diagnostic.MaxTokens, ["reasoning"] = diagnostic.Reasoning, ["model"] = diagnostic.Model,
            ["harnessHash"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "GnOuGo.Agent.Planning.Benchmark.dll"), ct))) };
        var existing = await records.GetAsync(Collection, Tenant, Identity, Author, ct);
        if (existing is not null && !JsonNode.DeepEquals(JsonNode.Parse(existing.Value), manifest))
            throw new InvalidOperationException("This diagnostic identity was already bound to different evidence.");
        await using (var db = await contexts.CreateDbContextAsync(ct)) await db.Database.EnsureCreatedAsync(ct);
        if (existing is null) await records.UpsertAsync(Collection, Tenant, Identity, manifest.ToJsonString(), Author, ct);
        JsonObject report;
        using var ratesHttp = new HttpClient();
        try
        {
            var response = await ExecuteOnceAsync(request, seed, limits, runtime.LlmClient, new ModelMetadataUsageCostEstimator(runtime.Options),
                new EcbExchangeRateProvider(ratesHttp), contexts, records, ct);
            report = Assess(response, request.StructuredOutputSchema!);
            report["receiptFingerprint"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse));
        }
        catch (Exception error)
        {
            await records.UpsertAsync(Collection, Tenant, Identity + ":failure", error.ToString(), Author, CancellationToken.None);
            report = new JsonObject { ["completion"] = "technical_failure", ["failureType"] = error.GetType().Name,
                ["code"] = error is GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException workflow ? workflow.Code : null,
                ["reasoningTokens"] = null, ["finalAnswerTokens"] = null, ["assignment"] = null, ["schemaValid"] = null };
        }
        var after = await ArchiveFingerprintAsync(records, sourceContexts);
        report["archiveUnchanged"] = before == after; report["manifest"] = manifest;
        await records.UpsertAsync(Collection, Tenant, Identity + ":report", report.ToJsonString(), Author, CancellationToken.None);
        if (before != after) throw new InvalidOperationException("The archived state changed during the diagnostic.");
        Console.WriteLine(report.ToJsonString());
    }

    private static async Task<string> ArchiveFingerprintAsync(IKeyVaultRecordStore records, IDbContextFactory<PlanningDbContext> contexts)
    {
        var proof = new JsonArray();
        foreach (var collection in new[] { EfPlanningSessionStore.Collection, PlanningBudgetSink.Collection, PlanningModelJournal.RequestCollection, PlanningModelJournal.Collection, "agent-planning-progressive-campaigns-v5" })
            foreach (var row in (await records.ListAsync(collection, Tenant, collection.Contains("campaigns", StringComparison.Ordinal) ? Author : EfPlanningSessionStore.Author))
                .Where(r => r.Key == SourceSession || r.Key.StartsWith(SourceSession + ":", StringComparison.Ordinal) || r.Key == SourceCampaign).OrderBy(r => r.Key, StringComparer.Ordinal))
                proof.Add(new JsonObject { ["collection"] = collection, ["key"] = row.Key, ["hash"] = PlanningGraphCompiler.Fingerprint(row.Value), ["created"] = row.CreatedAt, ["updated"] = row.UpdatedAt });
        await using var db = await contexts.CreateDbContextAsync();
        foreach (var row in await db.Calls.AsNoTracking().Where(c => c.TenantId == Tenant && c.SessionId == SourceSession).OrderBy(c => c.RequestHash).ToListAsync())
            proof.Add(new JsonObject { ["request"] = row.RequestHash, ["status"] = row.Status, ["payloadKey"] = row.PayloadKey });
        return PlanningGraphCompiler.Fingerprint(proof.ToJsonString());
    }
}
