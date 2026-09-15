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
using GnOuGo.Flow.Core.Expressions;
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

// No PlanningSessionService starts or approvals. The supplied port-only baseline
// and exact declaration attachments are fixture preconditions, not fresh model declaration evidence.
internal static partial class RuntimeAdmissionDiagnostic
{
    private const string Tenant = "runtime-admission-diagnostics", Collection = "agent-planning-diagnostics-v5", Author = "GnOuGo.Agent.Planning.Benchmark";
    private static string Identity => RuntimeAdmissionDiagnosticRules.Identity;
    private const string Mixed = """
        Build one reusable workflow. Required input sourceId:string identifies one record to load.
        Optional input threshold:number defaults to 100 when omitted; explicit null is invalid.
        Read the record identified by sourceId once from the external record store.
        Classify the loaded record: rejected when approved is false, high when approved is true and amount>=threshold, standard otherwise.
        Return classifiedResult:{id:string,amount:number,category:string}; preserve the loaded record's original id and amount.
        category has exactly the values rejected, high, standard.
        """;

    internal static async Task RunAsync(string command, string? commit, string root, IKeyVaultRecordStore records)
    {
        if (command == "selfcheck") { await SelfcheckAsync(); return; }
        using var cancel = new CancellationTokenSource(TimeSpan.FromHours(5));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); }; var ct = cancel.Token;
        var directory = GnOuGoWorkspace.ResolveDatabasePath(null, root, $".GnOuGo/data/planner-diagnostics/{Identity}/gnougo-planning-v5.db");
        if (command is not ("freeze" or "run-local" or "run-mixed" or "report")) throw new ArgumentException("diagnose-runtime-admission freeze COMMIT | run-local | run-mixed | report | selfcheck");
        if (command == "report")
        {
            Console.WriteLine((await ReportAsync(records, ct)).ToJsonString()); return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        using var lease = new FileStream(directory + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var contexts = new Contexts(directory);
        await using (var db = await ((IDbContextFactory<PlanningDbContext>)contexts).CreateDbContextAsync(ct))
        { await db.Database.EnsureCreatedAsync(ct); if (await db.Sessions.AnyAsync(ct)) throw new InvalidOperationException("Diagnostics cannot own planning sessions."); }
        var archive = await ArchiveAsync(records, ct);
        var prior = await records.GetAsync(ProgressiveCampaign.CampaignCollection, ProgressiveCampaign.Tenant, ProgressiveCampaign.CampaignId, ProgressiveCampaign.Author, ct)
            ?? throw new InvalidOperationException("The retained comparison campaign is required.");
        var campaign = JsonNode.Parse(prior.Value)!;
        var sourcePath = GnOuGoWorkspace.ResolveDatabasePath(null, root, $".GnOuGo/data/planner-progressive/{ProgressiveCampaign.CampaignId}/gnougo-planning-v5.db");
        var source = await new EfPlanningSessionStore(new Contexts(sourcePath, readOnly: true), records).LoadAsync(ProgressiveCampaign.Tenant,
            campaign["stages"]![0]!["session"]!.ToString(), ct) ?? throw new InvalidOperationException("Retained settings are required.");
        var services = new ServiceCollection().AddLogging();
        services.AddKeyVaultMcpPersistence(KeyVaultDatabasePathResolver.Resolve(null, root));
        services.AddAgentMcpPersistence(AgentMcpHostingExtensions.ResolveDatabasePath(null, root));
        var options = new LLMRuntimeOptionsStore(Options.Create(new LLMOptions()), NullLogger<LLMRuntimeOptionsStore>.Instance);
        services.AddSingleton(options).AddSingleton<IKeyVaultRuntimeConfigStore, KeyVaultRuntimeConfigStore>().AddSingleton<ILLMCapabilityResolver, FlowLlmCapabilityResolver>();
        await using var provider = services.BuildServiceProvider(); var vault = provider.GetRequiredService<IKeyVaultRuntimeConfigStore>();
        await using (var scope = provider.CreateAsyncScope()) await ProgressiveRules.HydrateModelAsync(options, vault, scope.ServiceProvider.GetRequiredService<IUserConfigRepository>(), ct);
        var capabilities = provider.GetRequiredService<ILLMCapabilityResolver>();
        await using var transport = await new SecureWorkflowRuntimeFactory(options, vault, mcpClientFactoryOverride: new InMemoryMcpClientFactory(), llmCapabilityResolver: capabilities).CreateAsync(ct);
        if (transport.Options.DefaultProvider != campaign["model"]!["provider"]!.ToString() || transport.Options.DefaultModel != "gpt-5.5-2026-04-24" ||
            await capabilities.SupportsStructuredOutputAsync(transport.Options.DefaultProvider, transport.Options.DefaultModel, ct) != true ||
            (await capabilities.SupportedReasoningLevelsAsync(transport.Options.DefaultProvider, transport.Options.DefaultModel, ct))?.Contains("low", StringComparer.Ordinal) != true)
            throw new InvalidOperationException("The configured model or declared capabilities differ from the frozen campaign.");
        JsonObject Binaries() => new(Directory.GetFiles(root, "GnOuGo.*.dll").Order(StringComparer.Ordinal).Select(p =>
            new KeyValuePair<string, JsonNode?>(Path.GetFileName(p), JsonValue.Create(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))))));
        var binaries = Binaries();
        var transportFingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(transport.Options));
        var manifestRecord = await records.GetAsync(Collection, Tenant, Identity, Author, ct);
        var manifest = manifestRecord is null ? null : JsonNode.Parse(manifestRecord.Value)!.AsObject();
        if (command == "freeze")
        {
            if (commit != "2f8b177ec6076cfdedbf524bd72a01d3f1b457d7") throw new InvalidOperationException("The authorized production commit is required.");
            if (manifest is not null) throw new InvalidOperationException("This diagnostic has already been frozen.");
            var previousRecord = await records.GetAsync(Collection, Tenant, "schema5-realized-governing-diagnostics-1", Author, ct)
                ?? throw new InvalidOperationException("The previous frozen settings are required.");
            var previousManifest = JsonNode.Parse(previousRecord.Value)!;
            if (!JsonNode.DeepEquals(previousManifest["model"], campaign["model"]) ||
                previousManifest["transportConfigurationFingerprint"]!.ToString() != transportFingerprint ||
                previousManifest["sourceOptionsFingerprint"]!.ToString() != PlanningGraphCompiler.Fingerprint(source.Request.Options.ToJsonString()) ||
                !JsonNode.DeepEquals(previousManifest["catalogFingerprint"], campaign["stages"]![0]!["catalogHash"]))
                throw new InvalidOperationException("The prior model, policy, catalog or budget configuration changed.");
            foreach (var frozenCase in previousManifest["cases"]!.AsArray())
            {
                var name = frozenCase!["name"]!.ToString();
                if (frozenCase["promptHash"]!.ToString() != PlanningGraphCompiler.Fingerprint(name == "local" ? ProgressiveScenarios.Simple : Mixed) ||
                    frozenCase["declarationFixtureHash"]!.ToString() != DeclarationFixtureHash(name))
                    throw new InvalidOperationException("A diagnostic scenario or declaration fixture changed.");
            }
            manifest = new() { ["commit"] = commit, ["binaries"] = binaries, ["archiveFingerprint"] = archive,
                ["model"] = campaign["model"]!.DeepClone(), ["sourceOptionsFingerprint"] = PlanningGraphCompiler.Fingerprint(source.Request.Options.ToJsonString()),
                ["transportConfigurationFingerprint"] = transportFingerprint,
                ["catalogFingerprint"] = campaign["stages"]![0]!["catalogHash"]!.DeepClone(),
                ["cases"] = new JsonArray(RuntimeAdmissionDiagnosticRules.Cases.Select(name => (JsonNode)new JsonObject
                { ["name"] = name, ["promptHash"] = PlanningGraphCompiler.Fingerprint(name == "local" ? ProgressiveScenarios.Simple : Mixed),
                    ["declarationFixtureHash"] = DeclarationFixtureHash(name), ["preflight"] = Preflight(name, source), ["status"] = "not_run" }).ToArray()),
                ["limits"] = new JsonObject { ["callsPerCase"] = RuntimeAdmissionDiagnosticRules.MaxCalls, ["reasoning"] = "low", ["input"] = 12000, ["dispatchTarget"] = 9600, ["output"] = 8192, ["singletonOutput"] = 16384 },
                ["declarationEvidence"] = "Supplied canonical ports and exact owned attachment fixtures; no live declaration convergence claimed." };
            await records.UpsertAsync(Collection, Tenant, Identity, manifest.ToJsonString(), Author, ct); Console.WriteLine(manifest.ToJsonString()); return;
        }
        if (manifest is null || !JsonNode.DeepEquals(manifest["binaries"], binaries) || manifest["archiveFingerprint"]!.ToString() != archive ||
            manifest["transportConfigurationFingerprint"]!.ToString() != transportFingerprint ||
            manifest["sourceOptionsFingerprint"]!.ToString() != PlanningGraphCompiler.Fingerprint(source.Request.Options.ToJsonString()))
            throw new InvalidOperationException("Frozen inputs, binaries or archive accounting changed.");
        foreach (var frozen in manifest["cases"]!.AsArray())
        {
            var name = frozen!["name"]!.ToString();
            if (!RuntimeAdmissionDiagnosticRules.Cases.Contains(name, StringComparer.Ordinal) ||
                frozen["promptHash"]!.ToString() != PlanningGraphCompiler.Fingerprint(name == "local" ? ProgressiveScenarios.Simple : Mixed) ||
                frozen["declarationFixtureHash"]!.ToString() != DeclarationFixtureHash(name))
                throw new InvalidOperationException("A frozen diagnostic precondition changed.");
        }
        using var ratesHttp = new HttpClient(); JsonObject? previous = null;
        if (command == "run-mixed")
        {
            var completedLocal = await records.GetAsync(Collection, Tenant, Identity + ":local:report", Author, ct);
            previous = completedLocal is null ? null : JsonNode.Parse(completedLocal.Value)!.AsObject();
        }
        var caseName = command == "run-local" ? "local" : "mixed";
        RuntimeAdmissionDiagnosticRules.RequireCase(caseName, previous);
        await RunCaseAsync(caseName, source, contexts, records, transport.LlmClient, capabilities,
            new ModelMetadataUsageCostEstimator(transport.Options), new EcbExchangeRateProvider(ratesHttp), ct);
        if (archive != await ArchiveAsync(records, CancellationToken.None)) throw new InvalidOperationException("Archived accounting changed.");
        if (!JsonNode.DeepEquals(binaries, Binaries())) throw new InvalidOperationException("Frozen binaries changed during the diagnostics.");
        await records.UpsertAsync(Collection, Tenant, Identity + ":archive-check", "unchanged", Author, CancellationToken.None);
        Console.WriteLine((await ReportAsync(records, CancellationToken.None)).ToJsonString());
    }

    private static PlanningGraph Ports(string name) => new() { Workflows = [new()
    {
        Key = "main", Inputs = [new() { Name = name == "local" ? "record" : "sourceId", Required = true, Schema = name == "local"
            ? new() { Type = "object", Properties = [new() { Name = "id", Schema = new() { Type = "string" }, Required = true }, new() { Name = "amount", Schema = new() { Type = "number" }, Required = true }, new() { Name = "approved", Schema = new() { Type = "boolean" }, Required = true }] }
            : new() { Type = "string" } }, new() { Name = "threshold", Required = false, Default = new() { Kind = "number", Number = 100 }, Schema = new() { Type = "number" } }],
        Outputs = [new() { Name = "classifiedResult", Schema = new() { Type = "object", Properties = [new() { Name = "id", Schema = new() { Type = "string" }, Required = true },
            new() { Name = "amount", Schema = new() { Type = "number" }, Required = true }, new() { Name = "category", Required = true, Schema = new() { Type = "string", Enum = ["rejected", "high", "standard"] } }] } }]
    }] };

    private static async Task<JsonObject> RunCaseAsync(string name, PlanningSnapshot source, Contexts contexts, IKeyVaultRecordStore records,
        ILLMClient client, ILLMCapabilityResolver capabilities, IModelUsageCostEstimator estimator, IExchangeRateProvider rates, CancellationToken ct)
    {
        var id = Identity + ":" + name;
        var completed = await records.GetAsync(Collection, Tenant, id + ":report", Author, ct);
        if (completed is not null) return JsonNode.Parse(completed.Value)!.AsObject();
        var retained = await records.GetAsync(Collection, Tenant, id + ":checkpoint", Author, ct);
        var envelope = retained is null ? new JsonObject { ["phase"] = "intent" } : JsonNode.Parse(retained.Value)!.AsObject();
        var state = retained is null ? NewState(name, source)
            : JsonSerializer.Deserialize(envelope["snapshot"], PlanningJsonContext.Default.PlanningSnapshot)!;
        if (state.Request.Options["llm_budget"]!["max_calls"]!.GetValue<int>() != RuntimeAdmissionDiagnosticRules.MaxCalls)
            throw new InvalidOperationException("The persisted diagnostic budget changed.");
        async Task Save(PlanningSnapshot snapshot, CancellationToken token)
        {
            RuntimeAdmissionDiagnosticRules.RequireRequest(snapshot);
            envelope["snapshot"] = JsonSerializer.SerializeToNode(snapshot, PlanningJsonContext.Default.PlanningSnapshot);
            await records.UpsertAsync(Collection, Tenant, id + ":checkpoint", envelope.ToJsonString(), Author, token);
            Console.WriteLine(new JsonObject { ["case"] = name, ["checkpoint"] = envelope["phase"]!.ToString(),
                ["reservations"] = snapshot.RequestAccounting.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count(),
                ["verified"] = snapshot.RequestAccounting.DistinctBy(c => c.Id).Count(c => c.Evidence == "receipt"),
                ["partitions"] = snapshot.DecisionPages.Count(p => p.Origin == PlanningDecisionPageOrigin.OutputPartition),
                ["escalations"] = snapshot.DecisionPages.Count(p => p.Origin == PlanningDecisionPageOrigin.OutputBudgetEscalation) }.ToJsonString());
        }
        var savedBudget = await records.GetAsync(PlanningBudgetSink.Collection, Tenant, id, EfPlanningSessionStore.Author, ct);
        var budget = new LLMUsageBudgetScope(PlanningBudgetOptions.Parse(state.Request.Options)!, savedBudget is null ? null :
            JsonSerializer.Deserialize(savedBudget.Value, PlanningJsonContext.Default.LLMUsageBudgetSnapshot), sink: new PlanningBudgetSink(records, Tenant, id), exchangeRateProvider: rates);
        var journal = new PlanningModelJournal(client, contexts, records, Tenant, id, budget, estimator, state.Request.Generation);
        var runtime = new WorkflowPlanningRuntime(new WorkflowEngine { LLMClient = journal, LLMCapabilities = capabilities,
            Limits = new() { TenantId = Tenant, RunId = id, LogStepContent = false } }, Save);
        var alreadyStopped = envelope["phase"]!.ToString() == "stopped";
        string? failure = alreadyStopped ? state.TechnicalStop?.Code ?? "STOPPED" : null;
        if (!alreadyStopped)
        {
        try
        {
            if (envelope["phase"]!.ToString() == "intent")
            {
                await Save(state, ct); await PlanningSourceDecisions.InterpretAsync(state, runtime, ct);
                // Preserve the exact live interpretation in audit storage. Replace
                // only declaration candidates with the frozen, labelled precondition.
                envelope["interpretedObligations"] = JsonSerializer.SerializeToNode(state.Obligations, PlanningJsonContext.Default.ListPlanningObligation);
                InstallDeclarations(name, state); state.OperationAdmissionFingerprint = null;
                envelope["phase"] = "admission"; await Save(state, ct);
            }
            await PlanningOperations.ResolveAsync(state, runtime, ct); PlanningOperations.RequireExecutableIntent(state);
            var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray();
            if (operations.Count(o => o.Kind == "local_processing") != 1 || operations.Count(o => o.Kind == "external_read") != (name == "mixed" ? 1 : 0) ||
                operations.Any(o => !o.Required || o.Kind is not ("local_processing" or "external_read")))
                throw new WorkflowRuntimeException("DIAGNOSTIC_ADMISSION_MISMATCH", "The isolated fixture's expected runtime actions were not established.");
            CheckEffectFixture(name, state, operations);
            if (name == "mixed")
            {
                envelope["phase"] = "relations"; await Save(state, ct);
                await PlanningSourceDecisions.RelateAsync(state, runtime, ct);
                var read = operations.Single(o => o.Kind == "external_read"); var local = operations.Single(o => o.Kind == "local_processing");
                if (!state.ObligationRelations.Any(r => r.Producer == read.Id && r.Consumer == local.Id && r.Role == "data") ||
                    state.ObligationRelations.Any(r => r.Producer == local.Id && r.Consumer == read.Id))
                    throw new WorkflowRuntimeException("DIAGNOSTIC_DEPENDENCY_MISMATCH", "The local transformation must depend on the external read without a reverse dependency.");
            }
        }
        catch (Exception error)
        {
            failure = error is WorkflowRuntimeException workflow ? workflow.Code : error is OperationCanceledException ? "CANCELLED" : error.GetType().Name;
            state.TechnicalStop = new(failure, envelope["phase"]!.ToString() == "admission" ? "intent_operations" : state.RequestAccounting.LastOrDefault()?.Phase ?? "intent",
                error is WorkflowRuntimeException located ? located.Details?["location"]?.ToString() ?? "$" : "$", state.RequestAccounting.Any(c => c.Evidence == "unverifiable"));
            await records.UpsertAsync(Collection, Tenant, id + ":failure", error.ToString(), Author, CancellationToken.None);
        }
        }
        envelope["phase"] = failure is null ? "completed" : "stopped";
        // Stops are durable even when global budgeting rejects a reservation before dispatch.
        envelope["snapshot"] = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSnapshot);
        await records.UpsertAsync(Collection, Tenant, id + ":checkpoint", envelope.ToJsonString(), Author, CancellationToken.None);
        var receipts = new Dictionary<string, LLMResponse?>();
        var domains = new JsonArray(); var journalRequests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in state.RequestAccounting.DistinctBy(c => c.Id))
        {
            var receipt = await records.GetAsync(PlanningModelJournal.Collection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author, CancellationToken.None);
            receipts[call.Id] = receipt is null ? null : JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse);
            var issued = await records.GetAsync(PlanningModelJournal.RequestCollection, Tenant, id + ":" + call.Id, EfPlanningSessionStore.Author, CancellationToken.None);
            if (issued is not null)
            {
                journalRequests.Add(call.Id);
                var request = JsonSerializer.Deserialize(issued.Value, PlanningJsonContext.Default.LLMRequest)!;
                domains.Add(new JsonObject { ["requestId"] = call.Id, ["requestFingerprint"] = PlanningGraphCompiler.Fingerprint(issued.Value),
                    ["schemaFingerprint"] = PlanningGraphCompiler.Fingerprint(request.StructuredOutputSchema!.ToJsonString()),
                    ["contextFingerprint"] = PlanningGraphCompiler.Fingerprint(request.Prompt), ["outputCeiling"] = request.MaxTokens,
                    ["domains"] = RuntimeAdmissionDiagnosticRules.Domains(request.StructuredOutputSchema.AsObject()),
                    ["decisions"] = new JsonArray(request.StructuredOutputSchema["properties"]!.AsObject().Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray()),
                    ["receiptFingerprint"] = receipt is null ? null : PlanningGraphCompiler.Fingerprint(receipt.Value) });
            }
        }
        var report = ProgressiveReport.Build(state, receipts);
        var declarations = state.OperationAdmissionFingerprint is not null ? PlanningOperations.DeclarationExclusions(state) : null;
        report["journalRequests"] = journalRequests.Count;
        report["journalReservationsWithoutReceipt"] = journalRequests.Count(key => !receipts.TryGetValue(key, out var response) || response is null);
        report["coordinatorReservationsWithoutJournalRequest"] = state.RequestAccounting.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count(key => !journalRequests.Contains(key));
        report["modelRuntimeDecisionsRemoved"] = PlanningSourceDecisions.InterpretationDecisions(state).Count(d => d.Schema["properties"]!["runtime"] is null);
        report["declarationCoveredOccurrences"] = declarations?.Count;
        report["declarationExclusions"] = declarations is null ? null : new JsonObject(declarations.Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
            new JsonArray(p.Value.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()))));
        report["dependencies"] = new JsonArray(state.ObligationRelations.Select(r => (JsonNode)new JsonObject { ["producer"] = r.Producer, ["consumer"] = r.Consumer, ["role"] = r.Role }).ToArray());
        report["fixtureDeclarations"] = state.Declarations.Count == 0 ? null : new JsonArray(state.Declarations.Select(d => (JsonNode)new JsonObject
        { ["name"] = PlanningDeclarations.Name(state, d), ["direction"] = d.Direction, ["required"] = d.Required,
            ["default"] = PlanningDeclarations.Default(state, d) is { } value ? JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.PlanningValue) : null,
            ["modifiers"] = d.ModifierReferences.Count }).ToArray());
        report["case"] = name; report["status"] = failure is null ? "passed" : "stopped"; report["firstBlocker"] = failure;
        report["runtimeRoles"] = new JsonObject(state.RuntimeEvidence.GroupBy(e => e.Role).Select(g => new KeyValuePair<string, JsonNode?>(g.Key, JsonValue.Create(g.Count()))));
        report["runtimeEvidence"] = new JsonArray(state.RuntimeEvidence.Select(e => (JsonNode)new JsonObject
        { ["id"] = e.Id, ["role"] = e.Role, ["scope"] = e.ExecutionScope.ToString(), ["origin"] = e.Origin.ToString(),
            ["kind"] = e.Kind, ["necessity"] = e.Necessity.ToString(), ["necessityReference"] = e.NecessityReference, ["evidenceRole"] = e.EvidenceRole, ["actionReference"] = e.ActionReference,
            ["resourceReference"] = e.ResourceReference, ["clauseReference"] = e.ClauseReference, ["proofFingerprint"] = e.ProofFingerprint }).ToArray());
        var assignments = state.Obligations.Where(PlanningSourceDecisions.IsOperation).SelectMany(o => o.OperationAdmission!.Assignments).ToArray();
        report["actionCandidates"] = state.RuntimeEvidence.Count(e => e.EvidenceRole == "action");
        report["canonicalOperations"] = state.OperationAdmissionFingerprint is null ? null : new JsonArray(state.Obligations.Where(PlanningSourceDecisions.IsOperation)
            .Select(o => (JsonNode)new JsonObject { ["id"] = o.Id, ["kind"] = o.Kind, ["required"] = o.Required, ["anchor"] = o.OperationAdmission!.AnchorReference,
                ["evidence"] = new JsonArray(o.OperationAdmission.Assignments.Select(a => (JsonNode)new JsonObject
                { ["decisionId"] = a.DecisionId, ["evidenceId"] = a.RuntimeEvidenceId, ["disposition"] = a.Disposition,
                    ["origin"] = a.ResolutionOrigin, ["target"] = a.TargetId }).ToArray()) }).ToArray());
        report["identityModelDecisions"] = assignments.Where(a => a.ResolutionOrigin == "model" && a.Disposition is "distinct" or "same_as").DistinctBy(a => a.DecisionId).Count();
        report["identityDeterministicDecisions"] = assignments.Where(a => a.ResolutionOrigin == "deterministic" && a.Disposition is "distinct" or "same_as").DistinctBy(a => a.DecisionId).Count();
        report["governingAttachmentsByOperation"] = state.OperationAdmissionFingerprint is null ? null : new JsonObject(state.Obligations.Where(PlanningSourceDecisions.IsOperation)
            .Select(o => new KeyValuePair<string, JsonNode?>(o.Id, JsonValue.Create(o.OperationAdmission!.Assignments.Count(a => a.Disposition == "attach")))));
        report["governingModelDecisions"] = assignments.Where(a => a.ResolutionOrigin == "model" && a.Disposition == "attach").DistinctBy(a => a.DecisionId).Count();
        report["governingDeterministicAttachments"] = assignments.Count(a => a.ResolutionOrigin == "deterministic" && a.Disposition == "attach");
        report["retiredActionCandidates"] = state.DecisionPages.Where(p => p.Phase == "intent_operations" && p.Status == "completed" && p.Candidate is not null)
            .SelectMany(p => p.Candidate!.Where(v => v.Value is JsonObject body && body["status"]?.ToString() == "not_an_operation").Select(v => v.Key)).Distinct(StringComparer.Ordinal).Count();
        report["engineAdmittedLocalActions"] = state.OperationAdmissionFingerprint is null ? null : state.Obligations.Count(o => o.OperationAdmission is not null && o.Kind == "local_processing" && o.OperationAdmission.Assignments[0].ResolutionOrigin == "deterministic");
        report["engineAdmittedExternalActions"] = state.OperationAdmissionFingerprint is null ? null : state.Obligations.Count(o => o.OperationAdmission is not null && o.Kind != "local_processing");
        report["admissionModelCalls"] = state.RequestAccounting.Count(c => c.Phase.StartsWith("intent_operations", StringComparison.Ordinal) && receipts.GetValueOrDefault(c.Id) is not null);
        report["fixturePreconditions"] = "Canonical declarations supplied; interpretation obligations retained encrypted but not adjudicated in this diagnostic.";
        report["requestDomains"] = domains;
        AddEffectReport(report, state, receipts, domains);
        report["fullStageOneSuccess"] = false;
        await records.UpsertAsync(Collection, Tenant, id + ":report", report.ToJsonString(), Author, CancellationToken.None);
        return report;
    }

    private static async Task<JsonObject> ReportAsync(IKeyVaultRecordStore records, CancellationToken ct)
    {
        var cases = new JsonArray();
        foreach (var name in RuntimeAdmissionDiagnosticRules.Cases)
        {
            var record = await records.GetAsync(Collection, Tenant, Identity + ":" + name + ":report", Author, ct);
            cases.Add(record is null ? new JsonObject { ["case"] = name, ["status"] = "not_run" } : JsonNode.Parse(record.Value));
        }
        var manifest = await records.GetAsync(Collection, Tenant, Identity, Author, ct);
        return new() { ["identity"] = Identity, ["manifest"] = manifest is null ? null : JsonNode.Parse(manifest.Value), ["cases"] = cases,
            ["archiveCheck"] = (await records.GetAsync(Collection, Tenant, Identity + ":archive-check", Author, ct))?.Value };
    }
    private static async Task<string> ArchiveAsync(IKeyVaultRecordStore records, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var tenant in new[] { ProgressiveCampaign.Tenant, Tenant })
        foreach (var collection in new[] { ProgressiveCampaign.CampaignCollection, Collection, EfPlanningSessionStore.Collection, PlanningModelJournal.RequestCollection, PlanningModelJournal.Collection, PlanningBudgetSink.Collection })
            foreach (var item in (await records.ListAsync(collection, tenant, EfPlanningSessionStore.Author, ct)).Where(r => !r.Key.StartsWith(Identity, StringComparison.Ordinal)).OrderBy(r => r.Key, StringComparer.Ordinal))
                parts.Add(tenant + ":" + collection + ":" + item.Key + ":" + PlanningGraphCompiler.Fingerprint(item.Value));
        return PlanningGraphCompiler.Fingerprint(string.Join('|', parts));
    }
}
