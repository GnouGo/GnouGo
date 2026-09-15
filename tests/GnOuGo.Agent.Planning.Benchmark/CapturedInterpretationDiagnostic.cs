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

// One separately authorized transport diagnostic. It cannot create or advance a planner session.
internal static class CapturedInterpretationDiagnostic
{
    internal const string Identity = "schema5-effect-interpretation-singleton-16384-low-1";
    internal const string Tenant = "runtime-admission-diagnostics";
    internal const string SourceCampaign = "schema5-effect-grounded-admission-diagnostics-1";
    private const string SourceCase = SourceCampaign + ":local";
    private const string Collection = "agent-planning-diagnostics-v5", Author = "GnOuGo.Agent.Planning.Benchmark";
    private const string Decision = "interpret_18819469387f8281c01c9120";
    private const string SourceRequest = SourceCase + ":0:3:intent:response_contract:93beceaf3191e878:page_1a567eef6002e05aa7093ded0fd524dd6cbfdcacfbed0b0f34a1994217263a50:e001c096a7b4f9aa037dc6be6be9141a217af58445a743ed2bb8bd639b4bfa85";
    private const string SourceRequestHash = "a681c93b81c4911ee1d87929732f2fb64b118d4d45078edbc2425961e7e58653";
    private const string ParentReceiptHash = "e584a011c6a2c70a92d712b9520aa8e56cd3cd862347614fd81d3daf06a1d5fd";

    internal static LLMRequest CreateRequest(LLMRequest source)
    {
        if (source.MaxTokens != 16384 || source.Reasoning != "low" || !source.DisableTransportRetries || !source.RequireOutputTokenLimit ||
            source.OutputBudgetEscalation is not { Level: 1 } ||
            source.StructuredOutputSchema?["properties"] is not JsonObject fields || fields.Count != 1)
            throw new InvalidOperationException("The diagnostic requires an exact captured low singleton escalation.");
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(source, PlanningJsonContext.Default.LLMRequest), PlanningJsonContext.Default.LLMRequest)!;
        // Archive-owned authorization is not transferable. The isolated journal has its own
        // explicitly authorized 16k ceiling; the exact original proof remains encrypted in the archive.
        copy.OutputBudgetEscalation = null;
        copy.ClientRequestId = null;
        copy.ClientRequestId = Identity + ":" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(copy, PlanningJsonContext.Default.LLMRequest));
        RequireIdenticalGeneration(source, copy);
        return copy;
    }

    internal static void RequireIdenticalGeneration(LLMRequest source, LLMRequest request)
    {
        var a = JsonSerializer.SerializeToNode(source, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        var b = JsonSerializer.SerializeToNode(request, PlanningJsonContext.Default.LLMRequest)!.AsObject();
        foreach (var name in new[] { "clientRequestId", "outputBudgetEscalation" }) { a.Remove(name); b.Remove(name); }
        if (request.OutputBudgetEscalation is not null || request.MaxTokens != 16384 || !JsonNode.DeepEquals(a, b))
            throw new InvalidOperationException("A captured generation field changed, or archive authority was reused.");
    }

    internal static async Task<LLMResponse> ExecuteOnceAsync(LLMRequest source, LLMUsageBudgetSnapshot seed, LLMUsageBudgetLimits limits,
        ILLMClient client, IModelUsageCostEstimator estimator, IExchangeRateProvider? rates,
        IDbContextFactory<PlanningDbContext> contexts, IKeyVaultRecordStore records, CancellationToken ct)
    {
        var request = CreateRequest(source);
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Sessions.AnyAsync(ct) || await db.Calls.AnyAsync(c => c.TenantId != Tenant || c.SessionId != Identity || c.RequestHash != request.ClientRequestId, ct))
            throw new InvalidOperationException("This isolated index may contain only the authorized request, and no planning session.");
        var sink = new PlanningBudgetSink(records, Tenant, Identity);
        var saved = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, Identity, EfPlanningSessionStore.Author, ct);
        var snapshot = saved is null ? seed : JsonSerializer.Deserialize(saved.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        if (saved is null) await sink.PersistAsync(seed, ct);
        // Retain cumulative calls/tokens/cost. The enclosing diagnostic deadline excludes archive wait time.
        var allowance = limits with { MaxCalls = Math.Min(limits.MaxCalls ?? int.MaxValue, checked((int)seed.Calls + 1)), MaxElapsed = null };
        var budget = new LLMUsageBudgetScope(allowance, snapshot, sink: sink, exchangeRateProvider: rates);
        var generation = new PlanningGenerationOptions { MaxOutputTokens = 16384 };
        RequireIdenticalGeneration(source, PlanningGenerationPolicy.Apply(request, generation));
        return await new PlanningModelJournal(client, contexts, records, Tenant, Identity, budget, estimator, generation).CallAsync(request, ct);
    }

    internal static async Task RunAsync(string root, IKeyVaultRecordStore records,
        IDbContextFactory<PlanningDbContext> sourceContexts, IDbContextFactory<PlanningDbContext> contexts)
    {
        var archived = await records.GetAsync(Collection, Tenant, SourceCase + ":checkpoint", Author)
            ?? throw new InvalidOperationException("Missing archived diagnostic checkpoint.");
        var envelope = JsonNode.Parse(archived.Value)!;
        var state = JsonSerializer.Deserialize(envelope["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        if (envelope["phase"]?.ToString() != "stopped") throw new InvalidOperationException("The source diagnostic must remain stopped.");
        var issued = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, SourceCase + ":" + SourceRequest, EfPlanningSessionStore.Author);
        if (issued is null || PlanningGraphCompiler.Fingerprint(issued.Value) != SourceRequestHash ||
            await records.GetAsync(PlanningModelJournal.Collection, Tenant, SourceCase + ":" + SourceRequest, EfPlanningSessionStore.Author) is not null)
            throw new InvalidOperationException("The captured request or its unverifiable state differs.");
        var request = JsonSerializer.Deserialize(issued.Value, PlanningJsonContext.Default.LLMRequest)!;
        if (request.ClientRequestId != SourceRequest || request.Model != "gpt-5.5-2026-04-24" ||
            request.StructuredOutputSchema?["properties"] is not JsonObject fields || fields.Count != 1 || !fields.ContainsKey(Decision))
            throw new InvalidOperationException("The captured request does not contain the authorized decision/model.");
        var parentKey = SourceCase + ":" + request.OutputBudgetEscalation!.ParentRequestId;
        var parentRequest = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, parentKey, EfPlanningSessionStore.Author);
        var parentReceipt = await records.GetAsync(PlanningModelJournal.Collection, Tenant, parentKey, EfPlanningSessionStore.Author);
        if (parentRequest is null || parentReceipt is null || PlanningGraphCompiler.Fingerprint(parentReceipt.Value) != ParentReceiptHash)
            throw new InvalidOperationException("The verified parent evidence differs.");
        PlanningGenerationPolicy.ValidateOutputEscalation(request,
            JsonSerializer.Deserialize(parentRequest.Value, PlanningJsonContext.Default.LLMRequest)!,
            JsonSerializer.Deserialize(parentReceipt.Value, PlanningJsonContext.Default.LLMResponse)!, SourceCase);
        var campaign = JsonNode.Parse((await records.GetAsync(Collection, Tenant, SourceCampaign, Author))!.Value)!;
        if (campaign["commit"]?.ToString() != "107cbcdd1789cbefc6b67d53f3ab570499854245")
            throw new InvalidOperationException("The authorized frozen production commit is required.");
        void RequireProductionHashes()
        {
            foreach (var binary in campaign["binaries"]!.AsObject().Where(b => b.Key != "GnOuGo.Agent.Planning.Benchmark.dll"))
                if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, binary.Key)))) != binary.Value!.ToString())
                    throw new InvalidOperationException("A frozen production binary changed.");
        }
        RequireProductionHashes();
        var before = await ArchiveFingerprintAsync(records, sourceContexts);
        var sourceBudget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, SourceCase, EfPlanningSessionStore.Author)
            ?? throw new InvalidOperationException("Missing archived cumulative budget.");
        var seed = JsonSerializer.Deserialize(sourceBudget.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot)!;
        var limits = PlanningBudgetOptions.Parse(state.Request.Options) ?? throw new InvalidOperationException("Missing source limits.");
        using var cancel = new CancellationTokenSource(limits.MaxElapsed ?? TimeSpan.FromHours(5));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var ct = cancel.Token;
        var services = new ServiceCollection().AddLogging();
        services.AddKeyVaultMcpPersistence(KeyVaultDatabasePathResolver.Resolve(null, root));
        services.AddAgentMcpPersistence(AgentMcpHostingExtensions.ResolveDatabasePath(null, root));
        var options = new LLMRuntimeOptionsStore(Options.Create(new LLMOptions()), NullLogger<LLMRuntimeOptionsStore>.Instance);
        services.AddSingleton(options).AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>().AddSingleton<ILLMCapabilityResolver, FlowLlmCapabilityResolver>();
        await using var provider = services.BuildServiceProvider();
        var vault = provider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
        await using (var scope = provider.CreateAsyncScope())
            await ProgressiveRules.HydrateModelAsync(options, vault, scope.ServiceProvider.GetRequiredService<IUserConfigRepository>(), ct);
        var capabilities = provider.GetRequiredService<ILLMCapabilityResolver>();
        await using var runtime = await new SecureWorkflowRuntimeFactory(options, vault, mcpClientFactoryOverride: new InMemoryMcpClientFactory(), llmCapabilityResolver: capabilities).CreateAsync(ct);
        if (runtime.Options.DefaultProvider != request.Provider || runtime.Options.DefaultModel != request.Model ||
            PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(runtime.Options)) != campaign["transportConfigurationFingerprint"]!.ToString() ||
            (await capabilities.SupportedReasoningLevelsAsync(request.Provider, request.Model, ct))?.Contains("low", StringComparer.Ordinal) != true ||
            await capabilities.SupportsStructuredOutputAsync(request.Provider, request.Model, ct) != true)
            throw new InvalidOperationException("The configured transport/model or declared capabilities changed.");
        var diagnostic = CreateRequest(request);
        var manifest = new JsonObject
        {
            ["identity"] = Identity, ["sourceCase"] = SourceCase, ["sourceRequest"] = SourceRequest, ["decision"] = Decision,
            ["sourceRequestFingerprint"] = SourceRequestHash, ["parentReceiptFingerprint"] = ParentReceiptHash,
            ["request"] = diagnostic.ClientRequestId, ["productionCommit"] = campaign["commit"]!.DeepClone(),
            ["sourceArchiveFingerprint"] = before, ["sourceBudgetFingerprint"] = PlanningGraphCompiler.Fingerprint(sourceBudget.Value),
            ["contextFingerprint"] = PlanningGraphCompiler.Fingerprint(request.Prompt),
            ["schemaFingerprint"] = PlanningGraphCompiler.Fingerprint(request.StructuredOutputSchema!.ToJsonString()),
            ["model"] = request.Model, ["reasoning"] = request.Reasoning, ["outputLimit"] = request.MaxTokens,
            ["identicalGeneration"] = true, ["identityChange"] = "Isolated request ID and authorization; archived escalation metadata retained only as source evidence.",
            ["harnessHash"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "GnOuGo.Agent.Planning.Benchmark.dll"))))
        };
        var existing = await records.GetAsync(Collection, Tenant, Identity, Author, ct);
        if (existing is not null && !JsonNode.DeepEquals(JsonNode.Parse(existing.Value), manifest))
            throw new InvalidOperationException("The isolated identity is already bound to different evidence.");
        await using (var db = await contexts.CreateDbContextAsync(ct)) await db.Database.EnsureCreatedAsync(ct);
        if (existing is null) await records.UpsertAsync(Collection, Tenant, Identity, manifest.ToJsonString(), Author, ct);
        Console.WriteLine(new JsonObject { ["preflight"] = "passed", ["manifest"] = manifest.DeepClone() }.ToJsonString());
        JsonObject report;
        using var ratesHttp = new HttpClient();
        try
        {
            var response = await ExecuteOnceAsync(request, seed, limits, runtime.LlmClient, new ModelMetadataUsageCostEstimator(runtime.Options),
                new EcbExchangeRateProvider(ratesHttp), contexts, records, ct);
            report = SingletonOutputDiagnostic.Assess(response, request.StructuredOutputSchema!);
            report["receiptFingerprint"] = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(response, PlanningJsonContext.Default.LLMResponse));
        }
        catch (Exception error)
        {
            await records.UpsertAsync(Collection, Tenant, Identity + ":failure", error.ToString(), Author, CancellationToken.None);
            var failure = error as LLMClientException;
            report = new JsonObject
            {
                ["completion"] = failure is not null ? "provider_failure" : "technical_failure", ["failureType"] = error.GetType().Name,
                ["providerFailureKind"] = failure?.Kind.ToString(), ["providerStatusCode"] = failure?.StatusCode,
                ["providerAttempts"] = failure?.AttemptCount, ["inputTokens"] = null, ["outputTokens"] = null,
                ["reasoningTokens"] = null, ["finalAnswerTokens"] = null, ["schemaValid"] = null, ["assignment"] = null
            };
        }
        report["archiveUnchanged"] = before == await ArchiveFingerprintAsync(records, sourceContexts);
        RequireProductionHashes(); report["productionBinariesUnchanged"] = true; report["manifest"] = manifest;
        await using (var db = await contexts.CreateDbContextAsync())
        {
            report["reservations"] = await db.Calls.CountAsync(); report["verifiedReceipts"] = await db.Calls.CountAsync(c => c.Status == "completed");
            if (await db.Sessions.AnyAsync()) throw new InvalidOperationException("A diagnostic unexpectedly created a planning session.");
        }
        await records.UpsertAsync(Collection, Tenant, Identity + ":report", report.ToJsonString(), Author, CancellationToken.None);
        if (report["archiveUnchanged"]!.GetValue<bool>() != true) throw new InvalidOperationException("The archived diagnostic changed.");
        Console.WriteLine(report.ToJsonString());
    }

    private static async Task<string> ArchiveFingerprintAsync(IKeyVaultRecordStore records, IDbContextFactory<PlanningDbContext> contexts)
    {
        var proof = new JsonArray();
        foreach (var collection in new[] { Collection, EfPlanningSessionStore.Collection, PlanningBudgetSink.Collection, PlanningModelJournal.RequestCollection, PlanningModelJournal.Collection })
            foreach (var row in (await records.ListAsync(collection, Tenant, collection == Collection ? Author : EfPlanningSessionStore.Author))
                .Where(r => r.Key == SourceCampaign || r.Key.StartsWith(SourceCampaign + ":", StringComparison.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal))
                proof.Add(new JsonObject { ["collection"] = collection, ["key"] = row.Key, ["hash"] = PlanningGraphCompiler.Fingerprint(row.Value), ["created"] = row.CreatedAt, ["updated"] = row.UpdatedAt });
        await using var db = await contexts.CreateDbContextAsync();
        foreach (var row in await db.Calls.AsNoTracking().Where(c => c.TenantId == Tenant && c.SessionId == SourceCase).OrderBy(c => c.RequestHash).ToListAsync())
            proof.Add(new JsonObject { ["request"] = row.RequestHash, ["status"] = row.Status, ["payloadKey"] = row.PayloadKey });
        return PlanningGraphCompiler.Fingerprint(proof.ToJsonString());
    }
}
