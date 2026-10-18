using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
namespace GnOuGo.Agent.Server.Planning;
internal static class PlanningPersistenceSmoke
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var database = GnOuGoWorkspace.ResolveDatabasePath(Path.Combine(directory, "planning-smoke.db"), directory, ".GnOuGo/data/planning-smoke.db");
        var vault = GnOuGoWorkspace.ResolveDatabasePath(Path.Combine(directory, "planning-smoke-vault.db"), directory, ".GnOuGo/data/planning-smoke-vault.db");
        var factory = new PooledDbContextFactory<PlanningDbContext>(new DbContextOptionsBuilder<PlanningDbContext>()
            .UseSqlite("Data Source=" + database + ";Pooling=False").UseModel(CompiledModels.PlanningDbContextModel.Instance).Options);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory);
        var store = new EfPlanningSessionStore(factory, records);
        var state = new PlanningSession { Request = new() { TenantId = "smoke", SessionId = Guid.NewGuid().ToString("N"), Prompt = "Private published smoke content" } };
        state.Requirements = new() { Summary = "Private requirements", Outcomes = [new("result", "Private acceptance criterion")] };
        state.IntentVersion = 1; state.OutcomeVersion = 1;
        state.Requirements.Outcomes[0] = state.Requirements.Outcomes[0] with { Execution = "data", Always = false, Conditional = false };
        state.OutcomeBindings = [new("result", [], ["private"])];
        state.Requirements.Inputs = [new() { Name = "reference" }];
        state.PendingQuestions = [new("interface", "Private caller interface question", [new("compact", "Private compact interface"), new("explicit", "Private explicit interface")], "compact")];
        state.AnswerHistory = [new(0, [new("fact", "Private missing fact", [], null)], [new("fact", Text: "Private custom answer")])];
        state.Discovery.Limitations.Add("Private unavailable source detail");
        if (!await store.TrySaveAsync(state, null, CancellationToken.None)) throw new InvalidOperationException("Insert failed.");
        state.Revision = 1; state.Status = PlanningStatus.Stopped; state.ModelCalls = 2; state.ReplanAttempts = 1;
        state.RevisionScope = ["main/consumer"];
        state.EditablePaths = ["/root/outputs/private"];
        state.Plan = new() { Root = new() { Outputs = [new("private", new() { Kind = "string", Text = "Private semantic value" })] } };
        state.Graph = new() { Workflows = [new() { Key = "main" }] };
        state.Diagnostics = [new("STAGE_CONTRACT_INVALID", "/scopes/main/operations/consumer", "Private computation finding")
        {
            Prerequisite = new("blocked_dependency", "Private prerequisite", RootActionId: "producer"),

        }];
        if (!await store.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("Update failed.");
        var reopened = new EfPlanningSessionStore(factory, KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory));
        var restored = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (restored?.SchemaVersion != 10 || restored.Revision != 1 || restored.ModelCalls != 2 || restored.ReplanAttempts != 1 || restored.Requirements?.Summary != "Private requirements" || restored.Graph is null || restored.Plan?.Root.Outputs[0].Value.Text != "Private semantic value" || restored.Diagnostics.Count != 1 ||
            restored.Diagnostics[0].Prerequisite?.RootActionId != "producer" || !restored.RevisionScope.SequenceEqual(["main/consumer"]) || restored.EditablePaths is not ["/root/outputs/private"] ||
            restored.OutcomeVersion != 1 || restored.OutcomeBindings?.Single().Outputs.Single() != "private" || restored.Requirements.Outcomes.Single().Execution != "data" ||
            restored.IntentVersion != 1 || restored.Requirements.Inputs?.Single().Name != "reference" || restored.PendingQuestions?.Single().Recommended != "compact" ||
            restored.AnswerHistory?.Single().Answers.Single().Text != "Private custom answer" ||
            await reopened.LoadAsync("another-tenant", state.Request.SessionId, CancellationToken.None) is not null || (await reopened.ListAsync("smoke", CancellationToken.None)).Count == 0)
            throw new InvalidOperationException("Published persistence or tenant isolation failed.");
        state.Revision = 2;
        if (await reopened.TrySaveAsync(state, 0, CancellationToken.None)) throw new InvalidOperationException("A stale update was accepted.");
        state.OutcomeVersion = 2;
        state.Requirements.Outcomes[0] = state.Requirements.Outcomes[0] with { Execution = "read", Coverage = "each_item" };
        state.OutcomeBindings = [new("result", ["collection"], []) { ForEachTaskId = "collection" }];
        if (!await reopened.TrySaveAsync(state, 1, CancellationToken.None)) throw new InvalidOperationException("Outcome-v2 update failed.");
        var perItem = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        if (perItem?.OutcomeVersion != 2 || perItem.Requirements?.Outcomes.Single().Coverage != "each_item" || perItem.OutcomeBindings?.Single().ForEachTaskId != "collection")
            throw new InvalidOperationException("Optional outcome-v2 metadata did not survive published encrypted recovery.");
        var legacy = new PlanningSession { Request = new() { TenantId = "smoke", SessionId = Guid.NewGuid().ToString("N"), Name = "Legacy smoke session" },
            ModelCalls = 1, PendingCall = new() { Id = "uncertain" } };
        if (!await store.TrySaveAsync(legacy, null, CancellationToken.None)) throw new InvalidOperationException("Legacy fixture insert failed.");
        string legacyKey;
        await using (var db = factory.CreateDbContext()) legacyKey = (await db.Sessions.SingleAsync(s => s.TenantId == "smoke" && s.SessionId == legacy.Request.SessionId)).PayloadKey;
        var payload = System.Text.Json.JsonSerializer.SerializeToNode(legacy, PlanningJsonContext.Default.PlanningSession)!;
        payload["schemaVersion"] = 8;
        var before = await records.UpsertAsync(EfPlanningSessionStore.Collection, "smoke", legacyKey, payload.ToJsonString(), "smoke");
        var history = await EfPlanningSessionStore.InspectAllAsync(factory, records, "smoke", CancellationToken.None);
        if (history.Single(s => s.Entry.SessionId == legacy.Request.SessionId) is not { Entry.Available: false, Session: null } ||
            (await reopened.ListAsync("smoke", CancellationToken.None)).Any(s => s.Request.SessionId == legacy.Request.SessionId) ||
            before != await records.GetAsync(EfPlanningSessionStore.Collection, "smoke", legacyKey, "smoke"))
            throw new InvalidOperationException("Incompatible history isolation failed.");
        try { await reopened.LoadAsync("smoke", legacy.Request.SessionId, CancellationToken.None); throw new InvalidOperationException("Incompatible history was admitted."); }
        catch (PlanningConflictException) { }
        await VerifyJudgeRecoveryAsync(directory, vault, factory, records, store, 1);
        await VerifyJudgeRecoveryAsync(directory, vault, factory, records, store, 2);
        foreach (var file in Directory.EnumerateFiles(directory, "*.db"))
            if (System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)) is { } bytes &&
                (bytes.Contains("Private published smoke content", StringComparison.Ordinal) || bytes.Contains("Private requirements", StringComparison.Ordinal) ||
                 bytes.Contains("Private custom answer", StringComparison.Ordinal) || bytes.Contains("Private caller interface question", StringComparison.Ordinal)))
                throw new InvalidOperationException("Sensitive session content was persisted unencrypted.");
        GnOuGo.Flow.Core.Runtime.IMappingArtifactStore mappingStore = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(records,
            Path.Combine(directory, "mapping-index.db"), Path.Combine(directory, "mapping-owners"));
        const string mappingScript = "({private_mapping:source.observed})";
        await mappingStore.WriteAsync("smoke", new("mapping-smoke", mappingScript, null, 1), CancellationToken.None);
        GnOuGo.Flow.Core.Runtime.IMappingArtifactStore reopenedMappings = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(
            KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory), Path.Combine(directory, "mapping-index.db"), Path.Combine(directory, "mapping-owners"));
        if ((await reopenedMappings.ReadAsync("smoke", "mapping-smoke", CancellationToken.None))?.Script != mappingScript ||
            await reopenedMappings.ReadAsync("other", "mapping-smoke", CancellationToken.None) is not null ||
            System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(vault)).Contains(mappingScript, StringComparison.Ordinal))
            throw new InvalidOperationException("Published mapping encryption/recovery/tenant isolation failed.");
        await reopenedMappings.RemoveAsync("smoke", "mapping-smoke", CancellationToken.None);
        var runs = (GnOuGo.Flow.Core.Runtime.IWorkflowRunStore)mappingStore;
        var run = new GnOuGo.Flow.Core.Runtime.WorkflowRun { TenantId = "smoke", RunId = "description-" + Guid.NewGuid().ToString("N") };
        run.Limits.TenantId = run.TenantId; run.Limits.RunId = run.RunId;
        const string description = "Private literal ${metadata}";
        run.Invocations["/workflow/main/step/work"] = new() { Id = "/workflow/main/step/work", StepType = "set", Description = description,
            Status = "completed", Output = System.Text.Json.Nodes.JsonNode.Parse("{\"value\":\"exact\"}") };
        await runs.CreateAsync(run, CancellationToken.None);
        var recoveredRun = await ((GnOuGo.Flow.Core.Runtime.IWorkflowRunStore)reopenedMappings).ReadAsync("smoke", run.RunId, CancellationToken.None);
        if (recoveredRun?.Invocations["/workflow/main/step/work"].Description != description ||
            recoveredRun.Invocations["/workflow/main/step/work"].Output?["value"]?.ToString() != "exact" ||
            System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(vault)).Contains(description, StringComparison.Ordinal))
            throw new InvalidOperationException("Published encrypted description/receipt recovery failed.");
        Console.WriteLine("Format-10 planning persistence smoke passed; execution journal schema 9 is unchanged.");
    }

    private static async Task VerifyJudgeRecoveryAsync(string directory, string vault, IDbContextFactory<PlanningDbContext> factory,
        IKeyVaultRecordStore records, EfPlanningSessionStore store, int reviewVersion)
    {
        var historical = System.Text.Json.JsonSerializer.Deserialize("{}", PlanningJsonContext.Default.PlanningRequest)!;
        var current = new PlanningRequest();
        if (historical.MaxModelCalls != 8 || historical.MaxReplanAttempts != 2 ||
            historical.Generation.MaxInputTokensPerRequest != 12000 || historical.Generation.MaxOutputTokens != 8192 ||
            historical.Options.ContainsKey("business_judge") || current.MaxModelCalls != 12 || current.MaxReplanAttempts != 4 ||
            current.Generation.MaxInputTokensPerRequest != 32000 || current.Generation.MaxOutputTokens != 32768)
            throw new InvalidOperationException("Published fresh defaults changed historical source-generated deserialization.");

        const string evidence = "Private published judge request evidence";
        const string authority = "/root/outputs/private_published_judge_authority";
        const string uncertainty = "Private published judge uncertainty";
        const string rejectedReference = "/private_published_rejected_reference";
        var sessionId = "judge-" + Guid.NewGuid().ToString("N");
        // Cost accounting is enabled only when a financial ceiling is configured.
        // Use declared USD fixture pricing so recovery verifies an actual charge.
        var limits = new LLMUsageBudgetLimits { MaxCalls = current.MaxModelCalls, MaxEstimatedCost = new(0.01m, "USD") };
        var budget = new LLMUsageBudgetScope(limits, sink: new PlanningBudgetSink(records, "smoke", sessionId));
        var request = PlanningGenerationPolicy.Apply(new LLMRequest
        {
            Model = "published-smoke-fixture", Prompt = evidence, StructuredOutputStrict = true,
            StructuredOutputSchema = System.Text.Json.Nodes.JsonNode.Parse("""
                {"type":"object","additionalProperties":false,"required":["findings","uncertainty"],"properties":{
                  "findings":{"type":"array","items":{"type":"object","additionalProperties":false,
                    "required":["kind","location","evidence","explanation","uncertainty"],"properties":{
                      "kind":{"type":"string","enum":["omission","unnecessary_operation","inconsistency"]},
                      "location":{"type":"string"},"evidence":{"type":"array","items":{"type":"string"}},
                      "explanation":{"type":"string"},"uncertainty":{"type":"string"}}}},
                  "uncertainty":{"type":"string"}}}
                """)
        }, current.Generation);
        request.ClientRequestId = sessionId + ":1:" + PlanningGraphCompiler.Fingerprint(
            System.Text.Json.JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        var state = new PlanningSession
        {
            IntentVersion = 3, ModelCalls = 1, Status = PlanningStatus.Stopped,
            Request = new()
            {
                TenantId = "smoke", SessionId = sessionId, Prompt = evidence,
                Options = new()
                {
                    ["business_judge"] = true,
                    ["business_judge_authority"] = new System.Text.Json.Nodes.JsonArray(authority),
                    ["business_judge_pending_context"] = new System.Text.Json.Nodes.JsonObject { ["version"] = reviewVersion, ["fingerprint"] = "published-judge-context" },
                    ["business_judge_reviews"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                    { ["version"] = reviewVersion, ["fingerprint"] = "previous-published-judge-context", ["outcome"] = "inconclusive", ["description"] = uncertainty })
                }
            },
            PendingCall = new() { Id = request.ClientRequestId, Purpose = "judge", Request = request },
            Diagnostics = [new("MODEL_DISPATCH_UNVERIFIABLE", "/", "Interrupted coordinator checkpoint after the durable judge response.")]
        };
        if (reviewVersion == 2)
        {
            state.Request.Options["business_judge_review_version"] = 2;
            state.Request.Options["planning_input_review_version"] = 2;
            state.Request.Options["planning_diagnostics_version"] = 1;
            var retainedReview = state.Request.Options["business_judge_reviews"]![0]!;
            retainedReview["validation_status"] = "invalid";
            retainedReview["validation_diagnostics"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["location"] = "/findings/0/evidence/0", ["reference"] = rejectedReference,
                ["reference_truncated"] = false, ["reason"] = "reference_not_found"
            });
            retainedReview["validation_diagnostics_omitted"] = 0;
        }
        if (!await store.TrySaveAsync(state, null, CancellationToken.None)) throw new InvalidOperationException("Published judge fixture insert failed.");
        var model = new JudgeSmokeModel(uncertainty);
        var journal = new PlanningModelJournal(model, factory, records, "smoke", sessionId, budget, new JudgeSmokeEstimator(), current.Generation);
        var completed = await journal.CallAsync(request, CancellationToken.None);

        // Reopen all persisted state after the receipt, without saving a newer
        // coordinator snapshot. Recovery must use the exact issued request.
        var reopenedRecords = KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory);
        var reopened = new EfPlanningSessionStore(factory, reopenedRecords);
        var restored = await reopened.LoadAsync("smoke", sessionId, CancellationToken.None)
            ?? throw new InvalidOperationException("Published judge checkpoint is unavailable.");
        await PlanningModelRecovery.ResumeAsync(restored, reopenedRecords, request.ClientRequestId, CancellationToken.None, limits);
        var restoredBudget = new LLMUsageBudgetScope(limits, restored.Usage);
        var replayed = await new PlanningModelJournal(model, factory, reopenedRecords, "smoke", sessionId, restoredBudget,
            new JudgeSmokeEstimator(), restored.Request.Generation, new UnavailableJudgeSmokeCapabilities())
            .CallAsync(restored.PendingCall!.Request, CancellationToken.None);
        Check(model.Calls == 1 && restored.ModelCalls == 1 && restored.ReplanAttempts == 0 && restoredBudget.Snapshot.Calls == 1, "call and correction counters");
        Check(restoredBudget.Snapshot.InputTokens == 10 && restoredBudget.Snapshot.OutputTokens == 5 && restoredBudget.Snapshot.TotalTokens == 15, "token accounting");
        Check(restoredBudget.Snapshot.EstimatedCost == 0.001m && restoredBudget.Snapshot.EstimatedCostCurrency == "USD" &&
            restoredBudget.Snapshot.EstimatedCostUsd == 0.001m, "cost accounting");
        Check(restored.PendingCall.Purpose == "judge" && restored.PendingCall.Id == request.ClientRequestId, "issued request identity and purpose");
        Check(System.Text.Json.JsonSerializer.Serialize(restored.PendingCall.Request, PlanningJsonContext.Default.LLMRequest) ==
            System.Text.Json.JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "issued request content");
        Check(System.Text.Json.JsonSerializer.Serialize(completed, PlanningJsonContext.Default.LLMResponse) ==
            System.Text.Json.JsonSerializer.Serialize(replayed, PlanningJsonContext.Default.LLMResponse), "replayed response content");
        Check(restored.Request.Options["business_judge"]?.GetValue<bool>() == true, "captured judge setting");
        Check(restored.Request.Options["business_judge_authority"]?[0]?.ToString() == authority, "captured edit authority");
        Check(restored.Request.Options["business_judge_reviews"]?[0]?["description"]?.ToString() == uncertainty, "retained review history");
        Check(restored.Request.Options["business_judge_pending_context"]?["version"]?.GetValue<int>() == reviewVersion, "private context version");
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(state.Request.Options, restored.Request.Options), "private review diagnostics and captured profiles");
        if (reviewVersion == 2)
            Check(restored.Request.Options["business_judge_reviews"]?[0]?["response"] is null &&
                restored.Request.Options["business_judge_reviews"]?[0]?["validation_diagnostics"]?[0]?["reference"]?.ToString() == rejectedReference,
                "rejected findings remain separate from validated responses");
        Check(restored.Status == PlanningStatus.Generating && restored.Diagnostics.Count == 0 && restored.ApprovedHash is null, "unapproved recovery state");
        Check(await reopened.LoadAsync("another-tenant", sessionId, CancellationToken.None) is null, "session tenant isolation");
        Check(await reopenedRecords.GetAsync(PlanningModelJournal.Collection, "another-tenant", sessionId + ":" + request.ClientRequestId,
            EfPlanningSessionStore.Author, CancellationToken.None) is null, "receipt tenant isolation");
        foreach (var path in Directory.EnumerateFiles(directory, "*.db*"))
        {
            var bytes = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            if (bytes.Contains(evidence, StringComparison.Ordinal) || bytes.Contains(authority, StringComparison.Ordinal) ||
                bytes.Contains(uncertainty, StringComparison.Ordinal) || bytes.Contains(rejectedReference, StringComparison.Ordinal))
                throw new InvalidOperationException("Private judge content was persisted unencrypted.");
        }

        static void Check(bool valid, string field)
        {
            if (!valid) throw new InvalidOperationException("Published encrypted judge recovery failed: " + field + ".");
        }
    }

    private sealed class JudgeSmokeModel(string uncertainty) : ILLMClient, ILLMCapabilityResolver
    {
        internal int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LLMResponse
            {
                Json = new System.Text.Json.Nodes.JsonObject { ["findings"] = new System.Text.Json.Nodes.JsonArray(), ["uncertainty"] = uncertainty },
                Usage = new System.Text.Json.Nodes.JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 }
            });
        }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium"]);
    }

    private sealed class UnavailableJudgeSmokeCapabilities : ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => throw new InvalidOperationException("A saved judge receipt must replay without rechecking model capabilities.");
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("A saved judge receipt must replay without rechecking model capabilities.");
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("A saved judge receipt must replay without rechecking model capabilities.");
    }

    private sealed class JudgeSmokeEstimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0.001m;
    }
}
