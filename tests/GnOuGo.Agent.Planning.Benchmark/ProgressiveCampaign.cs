using System.Diagnostics;
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
using GnOuGo.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GnOuGo.Agent.Planning.Benchmark;

internal static class ProgressiveCampaign
{
    internal const string Tenant = "planner-progressive", Author = "GnOuGo.Agent.Planning.Benchmark";
    internal const string EvidenceCollection = "agent-planning-progressive-evidence-v5", CampaignCollection = "agent-planning-progressive-campaigns-v5";
    internal const string CampaignId = "schema5-ee487c8";

    internal static async Task RunAsync(string[] args, IKeyVaultRecordStore records, string root)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromHours(5));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        var ct = cancel.Token;
        var frozen = await records.GetAsync(EvidenceCollection, Tenant, "frozen", Author, ct) ?? throw new InvalidOperationException("Export historical benchmark inputs through the audit tool first.");
        var evidence = JsonNode.Parse(frozen.Value)!.AsObject();
        if (args[0] == "describe")
        {
            Console.WriteLine(new JsonObject { ["referencePrompt"] = evidence["referencePrompt"]!.DeepClone(), ["referenceGraph"] = evidence["referenceGraph"]!.DeepClone(), ["referenceBehavior"] = evidence["referenceBehavior"]!.DeepClone() }.ToJsonString()); return;
        }
        var path = GnOuGoWorkspace.ResolveDatabasePath(null, root, ".GnOuGo/data/planner-progressive/gnougo-planning-v5.db");
        var readOnly = args[0] is "report" or "inspect" or "replay";
        if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = readOnly ? null : new FileStream(path + ".campaign.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var contexts = new Contexts(path, readOnly);
        var store = new EfPlanningSessionStore(contexts, records);
        var saved = await records.GetAsync(CampaignCollection, Tenant, CampaignId, Author, ct);
        var manifest = saved is null ? null : JsonNode.Parse(saved.Value)!.AsObject();
        if (args[0] == "report")
        {
            if (manifest is null) throw new InvalidOperationException("Campaign is not frozen.");
            Console.WriteLine(await ReportAsync(manifest, contexts, records, store, ct)); return;
        }
        if (args[0] is "inspect" or "replay")
        {
            var entry = manifest!["stages"]![int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) - 1]!;
            var session = entry["session"]!.ToString();
            if (args[0] == "inspect")
                Console.WriteLine(JsonSerializer.Serialize(await store.LoadAsync(Tenant, session, ct), PlanningJsonContext.Default.PlanningSnapshot));
            else await OfflineReplay.RunAsync(contexts, records, Tenant, session, long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), ct);
            return;
        }
        if (args[0] == "selfcheck")
        {
            await ProgressiveExecution.CheckReferenceAsync(evidence, ct);
            await CodeReviewExecutionFixture.VerifyAsync(evidence["discovery"]!.AsArray(), ct);
            return;
        }
        ProgressiveRules.RequirePreflight(manifest);
        RequireFrozenProduction();
        var services = new ServiceCollection().AddLogging();
        services.AddKeyVaultMcpPersistence(KeyVaultDatabasePathResolver.Resolve(null, root));
        services.AddAgentMcpPersistence(AgentMcpHostingExtensions.ResolveDatabasePath(null, root));
        var generator = evidence["generator"]!;
        var options = new LLMRuntimeOptionsStore(Options.Create(new LLMOptions { DefaultProvider = generator["provider"]!.ToString(), DefaultModel = generator["model"]!.ToString() }), NullLogger<LLMRuntimeOptionsStore>.Instance);
        services.AddSingleton(options);
        services.AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>();
        services.AddSingleton<ILLMCapabilityResolver, FlowLlmCapabilityResolver>();
        await using var provider = services.BuildServiceProvider();
        var vault = provider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
        await using (var scope = provider.CreateAsyncScope())
            await ProgressiveRules.HydrateModelAsync(options, vault, scope.ServiceProvider.GetRequiredService<IUserConfigRepository>(), ct);
        if (args[0] == "archive-preflight")
        {
            // Audit-only import of the already observed failure; never contact a provider.
            if (manifest is not null || args.Length != 3) throw new InvalidOperationException("Archive requires an unrecorded preflight failure and its captured binary hashes.");
            var failure = await File.ReadAllTextAsync(args[1], ct);
            var attemptedBinaries = JsonNode.Parse(await File.ReadAllTextAsync(args[2], ct))!.AsObject();
            var archivedPolicy = await File.ReadAllTextAsync(Path.Combine(root, "CodeReviewPolicy.txt"), ct);
            var archived = CreateManifest(evidence, frozen.Value, archivedPolicy, attemptedBinaries,
                new JsonObject { ["provider"] = options.Current.DefaultProvider, ["model"] = options.Current.DefaultModel,
                    ["reasoningLevels"] = null, ["structuredOutput"] = null, ["attribution"] = "KeyVault inspection after the captured preflight failure; no configuration changes" });
            archived["preflightStop"] = new JsonObject { ["code"] = "MODEL_CAPABILITY_DISCOVERY_FAILED", ["phase"] = "preflight",
                ["location"] = "/model/capabilities", ["classification"] = "provider_metadata_discovery", ["evidenceHash"] = PlanningGraphCompiler.Fingerprint(failure),
                ["sessionStarts"] = 0, ["modelCalls"] = 0, ["reservations"] = 0, ["unverifiableDispatches"] = 0 };
            var configured = options.Current.ResolveProvider(options.Current.DefaultProvider);
            await records.UpsertAsync(CampaignCollection, Tenant, CampaignId + ":preflight-evidence",
                new JsonObject { ["capturedException"] = failure, ["providerType"] = configured?.ResolvedType,
                    ["configuredEndpoint"] = configured?.Url, ["apiVersion"] = configured?.ApiVersion }.ToJsonString(), Author, ct);
            await using (var db = contexts.CreateDbContext()) await db.Database.EnsureCreatedAsync(ct);
            await SaveAsync(records, archived, ct);
            Console.WriteLine(await ReportAsync(archived, contexts, records, store, ct));
            return;
        }
        var capabilities = provider.GetRequiredService<ILLMCapabilityResolver>();
        var stage = args[0] == "freeze" ? 1 : int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var discovery = ProgressiveScenarios.Catalog(stage, evidence);
        var runtimeFactory = new SecureWorkflowRuntimeFactory(options, vault, mcpClientFactoryOverride: new FrozenCatalog(discovery), llmCapabilityResolver: capabilities);
        await using var runtime = await runtimeFactory.CreateAsync(ct);
        var levels = await capabilities.SupportedReasoningLevelsAsync(runtime.Options.DefaultProvider, runtime.Options.DefaultModel, ct);
        var structured = await capabilities.SupportsStructuredOutputAsync(runtime.Options.DefaultProvider, runtime.Options.DefaultModel, ct);
        if (levels?.Contains("low", StringComparer.Ordinal) != true || structured != true) throw new InvalidOperationException("Configured model metadata must prove low reasoning and Structured Outputs before a live start.");
        var model = new JsonObject { ["provider"] = runtime.Options.DefaultProvider, ["model"] = runtime.Options.DefaultModel,
            ["reasoningLevels"] = new JsonArray(levels.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), ["structuredOutput"] = structured };
        var binaries = BinaryHashes(root);
        var policy = await File.ReadAllTextAsync(Path.Combine(root, "CodeReviewPolicy.txt"), ct);
        if (args[0] == "freeze")
        {
            var candidate = CreateManifest(evidence, frozen.Value, policy, binaries, model);
            if (manifest is not null) { RequireManifest(manifest, candidate); Console.WriteLine("Campaign already frozen; no sessions started."); return; }
            await using (var db = contexts.CreateDbContext()) await db.Database.EnsureCreatedAsync(ct);
            await SaveAsync(records, candidate, ct); Console.WriteLine(candidate.ToJsonString()); return;
        }
        if (manifest is null) throw new InvalidOperationException("Freeze the campaign before any start.");
        if (!JsonNode.DeepEquals(manifest["binaries"], binaries) || !JsonNode.DeepEquals(manifest["model"], model) || manifest["evidenceHash"]!.ToString() != PlanningGraphCompiler.Fingerprint(frozen.Value) || manifest["policyHash"]!.ToString() != PlanningGraphCompiler.Fingerprint(policy))
            throw new InvalidOperationException("Frozen binaries, model or evidence changed; no dispatch authorized.");
        var stages = manifest["stages"]!.AsArray(); var stageEntry = stages[stage - 1]!.AsObject();
        if (stageEntry["catalogHash"]!.ToString() != PlanningGraphCompiler.Fingerprint(discovery.ToJsonString()) || stageEntry["fixtureHash"]!.ToString() != ProgressiveScenarios.FixtureHash(stage))
            throw new InvalidOperationException("Frozen scenario changed.");
        using var ratesHttp = new HttpClient(); var rates = new EcbExchangeRateProvider(ratesHttp);
        using var service = new PlanningSessionService(store, contexts, records, runtimeFactory, new TypedWorkflowPlanner(), rates,
            Options.Create(new WorkflowPlanningBudgetSettings()), Options.Create(ProgressiveRules.Settings()),
            Options.Create(new OpenTelemetrySettings { TenantId = Tenant }), NullLogger<PlanningSessionService>.Instance);
        PlanningSnapshot state;
        if (args[0] == "start")
        {
            ProgressiveRules.RequireStart(stages, stage);
            stageEntry["status"] = "starting"; stageEntry["name"] = "Schema5ProgressiveStage" + stage;
            await SaveAsync(records, manifest, ct); // A crash never grants a second start.
            state = await service.StartAsync(stageEntry["name"]!.ToString(), ProgressiveScenarios.Prompt(stage, evidence, policy), false, ct);
            stageEntry["session"] = state.Request.SessionId; stageEntry["status"] = "running";
            await SaveAsync(records, manifest, ct);
        }
        else
        {
            if (stageEntry["session"] is null)
            {
                var recovered = (await store.ListAsync(Tenant, ct)).Where(s => s.Request.Name == stageEntry["name"]?.ToString()).ToArray();
                if (stageEntry["status"]?.ToString() != "starting" || recovered.Length != 1) throw new InvalidOperationException("No verifiable owned session exists; creation will not be retried.");
                stageEntry["session"] = recovered[0].Request.SessionId; await SaveAsync(records, manifest, ct);
            }
            state = await service.GetAsync(stageEntry["session"]!.ToString(), ct) ?? throw new InvalidOperationException("Owned session missing.");
            if (stageEntry["status"]?.ToString() == "blocked" && args[0] != "ab") throw new InvalidOperationException("The campaign stopped at its first blocker; only inspection, replay or an eligible diagnostic is permitted.");
        }
        try
        {
            if (args[0] == "ab") await ProgressiveDiagnostic.RunAsync(manifest, state, args[2], contexts, records, runtime, rates, ct);
            else if (args[0] == "execute")
            {
                if (state.Status != PlanningStatus.FinalReview) throw new InvalidOperationException("Execution requires final review.");
                if (stage == 3)
                {
                    if (args.Length != 4) throw new ArgumentException("campaign execute 3 PR_INPUT_PORT INSTRUCTIONS_INPUT_PORT");
                    await CodeReviewExecution.RunAsync(state, discovery, options, vault, contexts, records, rates, args[2], args[3], ct);
                }
                else await ProgressiveExecution.RunAsync(state, stage, discovery, records, ct);
            }
            else if (args[0] is "accept" or "approve")
            {
                var revision = long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture); var hash = args[3];
                if (args[0] == "accept")
                {
                    ProgressiveRules.RequireReview(state, revision, hash, PlanningStatus.BehaviorReview);
                    if (stage == 3 && !new[] { "git_compare_refs", "copilot_review" }.All(m => state.Preparation!.Capabilities.Any(c => c.Method == m))) throw new InvalidOperationException("The benchmark implementation restriction is missing.");
                }
                else
                {
                    var proof = await records.GetAsync("agent-planning-benchmark-validation-v5", Tenant, state.Request.SessionId + ":" + hash, EfPlanningSessionStore.Author, ct);
                    ProgressiveRules.RequireApproval(state, revision, hash, proof is null ? null : JsonNode.Parse(proof.Value)!.AsArray(), stageEntry["fixtureHash"]!.ToString(), stageEntry["catalogHash"]!.ToString(), ProgressiveScenarios.Cases(stage));
                }
                state = await service.SubmitAsync(state.Request.SessionId, new() { Kind = args[0] == "accept" ? "accept_behavior" : "approve", ExpectedRevision = revision, ArtifactHash = hash }, ct);
            }
            else if (args[0] == "justify")
            {
                if (args.Length != 4 || state.Revision != long.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) || string.IsNullOrWhiteSpace(args[3])) throw new ArgumentException("campaign justify STAGE REVISION JUSTIFICATION");
                if (!ProgressiveRules.CanPass(stage, state, true)) throw new InvalidOperationException("This outcome cannot pass the stage.");
                stageEntry["justification"] = args[3];
            }
            else if (args[0] is not ("start" or "advance")) throw new ArgumentException("Unsupported campaign command.");
            if (args[0] is "start" or "advance" or "accept")
            {
                if (stageEntry["status"]?.ToString() == "blocked") throw new InvalidOperationException("The campaign stopped at its first blocker.");
                while (!PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status))
                {
                    Console.WriteLine($"stage={stage} session={state.Request.SessionId} revision={state.Revision} phase={state.CurrentPhase} calls={state.Usage?.Calls ?? 0}");
                    state = await service.SubmitAsync(state.Request.SessionId, new() { ExpectedRevision = state.Revision }, ct);
                }
            }
            stageEntry["outcome"] = state.Outcome?.Name;
            stageEntry["status"] = ProgressiveRules.CanPass(stage, state, stageEntry["justification"] is not null) ? "passed" : state.TechnicalStop is not null || PlanningStatus.IsTerminal(state.Status) && state.Status != PlanningStatus.Approved ? "blocked" : "waiting";
            stageEntry["revision"] = state.Revision;
            await SaveAsync(records, manifest, ct);
        }
        catch (Exception error)
        {
            stageEntry["status"] = "blocked"; stageEntry["harnessFailure"] = error.GetType().Name;
            stageEntry["failureCode"] = args[0] == "execute" ? "INDEPENDENT_FIXTURE_FAILURE" : "CAMPAIGN_CONTROL_FAILURE";
            await records.UpsertAsync(CampaignCollection, Tenant, CampaignId + ":failure:" + stage, error.ToString(), Author, CancellationToken.None);
            await SaveAsync(records, manifest, CancellationToken.None);
            Console.WriteLine("Campaign stopped: " + error.GetType().Name + "; private evidence encrypted.");
        }
        Console.WriteLine(await ReportAsync(manifest, contexts, records, store, CancellationToken.None));
    }

    private static async Task<JsonObject> ReportAsync(JsonObject manifest, Contexts contexts, IKeyVaultRecordStore records, EfPlanningSessionStore store, CancellationToken ct)
    {
        var reports = new JsonArray();
        await using var db = contexts.CreateDbContext();
        foreach (var entry in manifest["stages"]!.AsArray())
        {
            var summary = new JsonObject { ["stage"] = entry!["stage"]!.DeepClone(), ["campaignStatus"] = entry["status"]!.DeepClone(), ["harnessFailure"] = entry["harnessFailure"]?.DeepClone(), ["failureCode"] = entry["failureCode"]?.DeepClone() };
            if (entry["session"] is { } id)
            {
                var state = await store.LoadAsync(Tenant, id.ToString(), ct) ?? throw new InvalidOperationException("Owned session missing.");
                var receipts = new Dictionary<string, LLMResponse?>(StringComparer.Ordinal);
                foreach (var row in await db.Calls.AsNoTracking().Where(c => c.TenantId == Tenant && c.SessionId == id.ToString()).ToListAsync(ct))
                {
                    var receipt = await records.GetAsync(PlanningModelJournal.Collection, Tenant, row.PayloadKey, EfPlanningSessionStore.Author, ct);
                    receipts[row.RequestHash] = receipt is null ? null : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse);
                }
                summary["planning"] = ProgressiveReport.Build(state, receipts);
                var execution = await db.Calls.AsNoTracking().Where(c => c.TenantId == Tenant && c.SessionId.StartsWith(id.ToString() + ":execution:")).ToListAsync(ct);
                var executionRequests = new JsonArray();
                foreach (var row in execution)
                {
                    var record = await records.GetAsync(PlanningModelJournal.Collection, Tenant, row.PayloadKey, EfPlanningSessionStore.Author, ct);
                    var response = record is null ? null : JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.LLMResponse);
                    var issued = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, row.PayloadKey, EfPlanningSessionStore.Author, ct);
                    var request = issued is null ? null : JsonSerializer.Deserialize(issued.Value, PlanningJsonContext.Default.LLMRequest);
                    executionRequests.Add(new JsonObject { ["request"] = row.RequestHash, ["receipt"] = response is not null, ["inputTokens"] = ProgressiveReport.Usage(response, false),
                        ["outputTokens"] = ProgressiveReport.Usage(response, true), ["reasoning"] = request?.Reasoning,
                        ["estimatedInput"] = request is null ? null : CodeReviewExecution.EstimateInput(request) });
                }
                summary["execution"] = new JsonObject { ["reservations"] = execution.Count, ["completed"] = executionRequests.Count(r => r!["receipt"]!.GetValue<bool>()),
                    ["withoutReceipt"] = executionRequests.Count(r => !r!["receipt"]!.GetValue<bool>()), ["requests"] = executionRequests };
            }
            reports.Add(summary);
        }
        return new JsonObject { ["productionCommit"] = manifest["productionCommit"]!.DeepClone(), ["model"] = manifest["model"]!.DeepClone(), ["stages"] = reports,
            ["preflightStop"] = manifest["preflightStop"]?.DeepClone(), ["diagnostic"] = manifest["diagnostic"]?.DeepClone() };
    }
    private static JsonObject CreateManifest(JsonObject evidence, string frozen, string policy, JsonObject binaries, JsonObject model) => new()
    {
        ["productionCommit"] = ProgressiveRules.ProductionCommit, ["binaries"] = binaries,
        ["model"] = model, ["evidenceHash"] = PlanningGraphCompiler.Fingerprint(frozen),
        ["policyHash"] = PlanningGraphCompiler.Fingerprint(policy), ["abUsed"] = false,
        ["limits"] = new JsonObject { ["input"] = 12000, ["dispatchTarget"] = 9600, ["output"] = 8192, ["concurrency"] = 4, ["repairsPerGate"] = 5,
            ["calls"] = 100, ["totalTokens"] = 15000000, ["activeMilliseconds"] = 18000000, ["amount"] = 50, ["currency"] = "EUR", ["reasoning"] = "low" },
        ["stages"] = new JsonArray(Enumerable.Range(1, 3).Select(s => (JsonNode)new JsonObject
        {
            ["stage"] = s, ["status"] = "not_run", ["session"] = null,
            ["promptHash"] = PlanningGraphCompiler.Fingerprint(ProgressiveScenarios.Prompt(s, evidence, policy)),
            ["catalogHash"] = PlanningGraphCompiler.Fingerprint(ProgressiveScenarios.Catalog(s, evidence).ToJsonString()),
            ["fixtureHash"] = ProgressiveScenarios.FixtureHash(s)
        }).ToArray())
    };
    private static Task SaveAsync(IKeyVaultRecordStore records, JsonObject manifest, CancellationToken ct)
        => records.UpsertAsync(CampaignCollection, Tenant, CampaignId, manifest.ToJsonString(), Author, ct);
    private static JsonObject BinaryHashes(string root) => new(Directory.GetFiles(root, "GnOuGo.*.dll").Order(StringComparer.Ordinal)
        .Select(p => new KeyValuePair<string, JsonNode?>(Path.GetFileName(p), JsonValue.Create(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))))));
    private static void RequireManifest(JsonObject existing, JsonObject candidate)
    {
        foreach (var field in new[] { "productionCommit", "binaries", "model", "evidenceHash", "policyHash", "limits" })
            if (!JsonNode.DeepEquals(existing[field], candidate[field])) throw new InvalidOperationException("The existing campaign cannot be refrozen with changed inputs.");
    }
    private static void RequireFrozenProduction()
    {
        using var process = Process.Start(new ProcessStartInfo("git") { ArgumentList = { "diff", "--quiet", ProgressiveRules.ProductionCommit, "--", "src" }, UseShellExecute = false })!;
        process.WaitForExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Production code differs from the frozen commit.");
    }
}
