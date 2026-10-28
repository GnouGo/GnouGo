using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
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
        await VerifySessionStorageAsync(store, factory, records, vault, directory);
        await VerifyJudgeRecoveryAsync(directory, vault, factory, records, store);
        IMappingArtifactStore mappings = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(records,
            Path.Combine(directory, "mapping-index.db"), Path.Combine(directory, "mapping-owners"));
        const string mappingScript = "({private_mapping:source.observed})";
        await mappings.WriteAsync("smoke", new("mapping-smoke", mappingScript, null, 1), CancellationToken.None);
        IMappingArtifactStore reopened = new GnOuGo.Flow.Persistence.EncryptedWorkflowRunStore(
            KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory), Path.Combine(directory, "mapping-index.db"), Path.Combine(directory, "mapping-owners"));
        Check((await reopened.ReadAsync("smoke", "mapping-smoke", CancellationToken.None))?.Script == mappingScript &&
            await reopened.ReadAsync("other", "mapping-smoke", CancellationToken.None) is null, "mapping tenant recovery");
        await reopened.RemoveAsync("smoke", "mapping-smoke", CancellationToken.None);
        var runs = (IWorkflowRunStore)mappings;
        var run = new WorkflowRun { TenantId = "smoke", RunId = "description-" + Guid.NewGuid().ToString("N") };
        run.Limits.TenantId = run.TenantId; run.Limits.RunId = run.RunId;
        const string description = "Private literal ${metadata}";
        run.Invocations["/workflow/main/step/work"] = new() { Id = "/workflow/main/step/work", StepType = "set", Description = description,
            Status = "completed", Output = JsonNode.Parse("{\"value\":\"exact\"}") };
        await runs.CreateAsync(run, CancellationToken.None);
        var restoredRun = await ((IWorkflowRunStore)reopened).ReadAsync("smoke", run.RunId, CancellationToken.None);
        Check(restoredRun?.SchemaVersion == 9 && restoredRun.Invocations["/workflow/main/step/work"].Description == description &&
            restoredRun.Invocations["/workflow/main/step/work"].Output?["value"]?.ToString() == "exact", "runtime format-9 receipt recovery");
        foreach (var file in Directory.EnumerateFiles(directory, "*.db*"))
        {
            var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Check(new[] { "Private original, binding and business review evidence", "Private current judge evidence", description, mappingScript }
                .All(secret => !bytes.Contains(secret, StringComparison.Ordinal)), "encrypted sensitive data");
        }
        Console.WriteLine("Format-11 planning persistence and exact receipt recovery passed; execution journal schema 9 is unchanged.");
    }

    private static async Task VerifySessionStorageAsync(EfPlanningSessionStore store, IDbContextFactory<PlanningDbContext> factory,
        IKeyVaultRecordStore records, string vault, string directory)
    {
        const string secret = "Private original, binding and business review evidence";
        var plan = new TaskPlan { Root = new() { Outputs = [new("accepted", new() { Kind = "string", Text = secret })] } };
        var state = new PlanningSession
        {
            Request = new() { TenantId = "smoke", SessionId = Guid.NewGuid().ToString("N"), Prompt = secret },
            Plan = plan, ModelCalls = 2, ReplanAttempts = 1, RevisionScope = ["producer"], EditablePaths = ["/root/outputs/accepted"],
            Requirements = new() { Summary = secret, Outcomes = [new("result", secret)] },
            PendingQuestions = [new("interface", secret, [], null)],
            AnswerHistory = [new(0, [new("interface", secret, [], null)], [new("interface", Text: secret)])],
            Construction = new()
            {
                Compact = new()
                {
                    Sources = [new() { Id = "request:0", Version = "exact-source", Text = secret, Indexed = true }],
                    InterfaceExtractions = [new() { Id = "request:0", SourceIds = ["request:0"], Inputs = true }],
                    CurrentStep = new() { Id = "step_1", BaseHash = PlanningConstruction.Hash(plan), Objective = secret, Kind = null },
                    SuspendedSteps = [new() { Id = "consumer", Prerequisite = new() { Need = secret, ExpectedType = new() { Kind = "number", Nullable = true } } }]
                },
                Checkpoints = [new() { Sequence = 1, PlanHash = PlanningConstruction.Hash(plan), Fragment = new() { ["accepted"] = secret } }],
                Candidate = new() { BaseHash = PlanningConstruction.Hash(plan), Plan = plan, Reviews = [new() { ["verdict"] = "needs_context", ["reason"] = secret }] },
                PendingReviewContext = new() { ["original"] = secret }
            },
            PendingCall = new() { Id = "exact-request", Purpose = "construction_local_judge", Request = new() { Prompt = secret,
                StructuredOutputSchema = JsonNode.Parse("{\"type\":\"object\",\"properties\":{}}") } }
        };
        var original = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Check(await store.TrySaveAsync(state, null, CancellationToken.None), "insert");
        var reopened = new EfPlanningSessionStore(factory, KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory));
        var restored = await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None);
        Check(restored is not null && JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession) == original, "source-generated exact current recovery");
        Check(await reopened.LoadAsync("other", state.Request.SessionId, CancellationToken.None) is null, "tenant isolation");
        state.Revision = 1;
        Check(await reopened.TrySaveAsync(state, 0, CancellationToken.None), "optimistic revision");
        state.Revision = 2;
        Check(!await reopened.TrySaveAsync(state, 0, CancellationToken.None), "stale update rejection");
        string key;
        await using (var db = factory.CreateDbContext()) key = (await db.Sessions.SingleAsync(s => s.SessionId == state.Request.SessionId)).PayloadKey;
        var retired = JsonNode.Parse(original)!; retired["schemaVersion"] = 10;
        var archive = await records.UpsertAsync(EfPlanningSessionStore.Collection, "smoke", key, retired.ToJsonString(), "smoke");
        try { await reopened.LoadAsync("smoke", state.Request.SessionId, CancellationToken.None); throw new InvalidOperationException("Retired planning format was admitted."); }
        catch (PlanningConflictException) { }
        Check(archive == await records.GetAsync(EfPlanningSessionStore.Collection, "smoke", key, "smoke"), "retired records unchanged");
    }

    private static async Task VerifyJudgeRecoveryAsync(string directory, string vault, IDbContextFactory<PlanningDbContext> factory,
        IKeyVaultRecordStore records, EfPlanningSessionStore store)
    {
        var state = new PlanningSession
        {
            Request = new() { TenantId = "smoke", SessionId = "judge-" + Guid.NewGuid().ToString("N"), Prompt = "Private current judge evidence" },
            Construction = new() { Limits = new() { PhasePolicy = new()
            {
                Navigation = new() { TargetInputTokens = 2048, MaxInputTokens = 4096, OutputTokens = 128 },
                Draft = new() { TargetInputTokens = 2048, MaxInputTokens = 4096, OutputTokens = 128 },
                Review = new() { TargetInputTokens = 2048, MaxInputTokens = 4096, OutputTokens = 64 }
            } } }
        };
        PlanningSessionCreation.Capture(state);
        var request = PlanningConstructionPhasePolicies.Apply(new()
        {
            Provider = "fixture", Model = "journal-fixture", Prompt = state.Request.Prompt, StructuredOutputStrict = true, RequiredReviewCalls = 0,
            StructuredOutputSchema = JsonNode.Parse("""{"title":"construction_local_judge","type":"object","additionalProperties":false,"required":["verdict"],"properties":{"verdict":{"type":"string","enum":["ok"]}}}""")
        }, state.Request, "construction_local_judge");
        request.ClientRequestId = state.Request.SessionId + ":1:" + PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
        state.ModelCalls = 1; state.Status = PlanningStatus.Stopped;
        state.Diagnostics = [new("MODEL_DISPATCH_UNVERIFIABLE", "/", "Interrupted before coordinator checkpoint")];
        state.PendingCall = new() { Id = request.ClientRequestId, Purpose = "construction_local_judge", Request = request };
        Check(await store.TrySaveAsync(state, null, CancellationToken.None), "judge insert");
        var limits = new LLMUsageBudgetLimits { MaxCalls = 64, MaxEstimatedCost = new(0.01m, "USD") };
        var budget = new LLMUsageBudgetScope(limits, sink: new PlanningBudgetSink(records, "smoke", state.Request.SessionId));
        using var handler = new JudgeSmokeHandler();
        using var http = new HttpClient(handler);
        var client = new RoutingLLMClientAdapter(new RoutingLLMClient(http, new LLMOptions
        {
            DefaultProvider = "fixture", DefaultModel = "journal-fixture",
            Models = { ["fixture"] = new() { Type = "openai", Url = "https://example.invalid/v1/chat/completions" } },
            ModelOverrides = { ["openai/journal-fixture"] = new() { Id = "journal-fixture", ProviderType = "openai", MaxInputTokens = 128000,
                MaxOutputTokens = 65536, ContextWindowTokens = 200000,
                Capabilities = new() { SupportsStructuredOutput = true, SupportsReasoningEffort = true, SupportedReasoningEfforts = ["medium"] } } }
        }));
        var completed = await new PlanningModelJournal(client, factory, records, "smoke", state.Request.SessionId, budget,
            new JudgeSmokeEstimator(), state.Request.Generation, planning: state.Request).CallAsync(request, CancellationToken.None);
        var reopenedRecords = KeyVaultRecordStoreFactory.CreateWorkspaceStore(vault, directory);
        var restored = await new EfPlanningSessionStore(factory, reopenedRecords).LoadAsync("smoke", state.Request.SessionId, CancellationToken.None)
            ?? throw new InvalidOperationException("Current judge checkpoint is unavailable.");
        await PlanningModelRecovery.ResumeAsync(restored, reopenedRecords, request.ClientRequestId, CancellationToken.None, limits);
        var recoveredBudget = new LLMUsageBudgetScope(limits, restored.Usage);
        var replayed = await new PlanningModelJournal(client, factory, reopenedRecords, "smoke", state.Request.SessionId, recoveredBudget,
            new JudgeSmokeEstimator(), restored.Request.Generation, new UnavailableCapabilities(), restored.Request)
            .CallAsync(restored.PendingCall!.Request, CancellationToken.None);
        Check(handler.Calls == 1 && restored.ModelCalls == 1 && restored.ReplanAttempts == 0 && recoveredBudget.Snapshot.Calls == 1, "once-only physical accounting");
        Check(recoveredBudget.Snapshot.InputTokens == 10 && recoveredBudget.Snapshot.OutputTokens == 5 && recoveredBudget.Snapshot.EstimatedCost == 0.001m, "provider usage and costs");
        Check(JsonSerializer.Serialize(completed, PlanningJsonContext.Default.LLMResponse) == JsonSerializer.Serialize(replayed, PlanningJsonContext.Default.LLMResponse) &&
            JsonSerializer.Serialize(restored.PendingCall.Request, PlanningJsonContext.Default.LLMRequest) == JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest), "exact saved request and response");
    }

    private static void Check(bool valid, string field)
    {
        if (!valid) throw new InvalidOperationException("Published planning persistence failed: " + field + ".");
    }

    private sealed class JudgeSmokeHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"verdict\":\"ok\"}"}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""", Encoding.UTF8, "application/json") });
        }
    }

    private sealed class JudgeSmokeEstimator : IModelUsageCostEstimator
    {
        public decimal? EstimateCost(string? model, long? inputTokens = null, long? outputTokens = null, string? providerType = null) => 0.001m;
    }

    private sealed class UnavailableCapabilities : ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => throw new InvalidOperationException("Receipt recovery cannot query model limits.");
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("Receipt recovery cannot re-admit its schema.");
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => throw new InvalidOperationException("Receipt recovery cannot re-admit reasoning.");
    }
}
