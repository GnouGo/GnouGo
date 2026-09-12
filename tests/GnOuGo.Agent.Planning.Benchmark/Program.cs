using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.AI.Core;
using GnOuGo.Agent.Planning.Benchmark;
using GnOuGo.Agent.Server.Configuration;
using GnOuGo.Agent.Server.Planning;
using GnOuGo.Agent.Server.SmartFlow;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Integrations;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.KeyVault.Core;
using GnOuGo.KeyVault.Mcp;
using GnOuGo.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

if (args.Length < 2) throw new ArgumentException("Use capture <source-session>, start <name>, inspect <session>, or command <session> <kind> [hash].");
const string tenant = "planner-benchmark";
const string evidenceKey = "codereview-bc72dd6";
var root = AppContext.BaseDirectory;
var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, root);
if (args[0] == "campaign")
{
    await ProgressiveCampaign.RunAsync(args[1..], records, root);
    return;
}
var benchmarkPath = GnOuGoWorkspace.ResolveDatabasePath(Environment.GetEnvironmentVariable("PLANNING_BENCHMARK_DATABASE"), root, ".GnOuGo/data/planner-benchmark/gnougo-planning-v5.db");
if (args[0] == "replay")
{
    if (args.Length != 3 || !long.TryParse(args[2], out var revision)) throw new ArgumentException("Use replay SESSION REVISION. This command cannot make live requests.");
    using var replayCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; replayCancellation.Cancel(); };
    await OfflineReplay.RunAsync(new Contexts(benchmarkPath, readOnly: true), records, tenant, args[1], revision, replayCancellation.Token);
    return;
}
Directory.CreateDirectory(Path.GetDirectoryName(benchmarkPath)!);
var contexts = new Contexts(benchmarkPath);
await using (var db = contexts.CreateDbContext()) await db.Database.EnsureCreatedAsync();
var store = new EfPlanningSessionStore(contexts, records);
using var cancellation = new CancellationTokenSource(TimeSpan.FromHours(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var ct = cancellation.Token;
if (args[0] == "capture")
{
    if (await store.LoadAsync(tenant, evidenceKey, ct) is not null) throw new InvalidOperationException("The benchmark evidence is already frozen.");
    var sourceContexts = new Contexts(GnOuGoWorkspace.ResolveDatabasePath(null, root, ".GnOuGo/data/gnougo-planning-v5.db"));
    var source = await new EfPlanningSessionStore(sourceContexts, records).LoadAsync("default", args[1], ct) ?? throw new InvalidOperationException("Source session not found.");
    var fingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(source, PlanningJsonContext.Default.PlanningSnapshot));
    var sourceRevision = source.Revision;
    source.Request.SessionId = evidenceKey; source.Request.TenantId = tenant;
    source.Status = PlanningStatus.Cancelled; source.Revision = 0;
    if (!await store.TrySaveAsync(source, null, ct)) throw new InvalidOperationException("Evidence capture conflict.");
    Console.WriteLine($"Frozen source revision {sourceRevision}; fingerprint {fingerprint}; catalog {PlanningGraphCompiler.Fingerprint(source.PreparationCheckpoint!.ValidatedResults["discovery"]!.ToJsonString())}");
    return;
}
if (args[0] == "inspect")
{
    var value = await store.LoadAsync(tenant, args[1], ct) ?? throw new InvalidOperationException("Session not found.");
    Console.WriteLine(JsonSerializer.Serialize(value, PlanningJsonContext.Default.PlanningSnapshot));
    return;
}
if (args[0] == "inspect-rejection")
{
    var value = await records.GetAsync("agent-planning-benchmark-rejections-v5", tenant, args[1], EfPlanningSessionStore.Author, ct);
    Console.WriteLine(value?.Value ?? "null");
    return;
}
if (args[0] is "report" or "summary")
{
    var value = await store.LoadAsync(tenant, args[1], ct) ?? throw new InvalidOperationException("Session not found.");
    if (args[0] == "summary")
    {
        var snapshot = JsonSerializer.SerializeToNode(value, PlanningJsonContext.Default.PlanningSnapshot)!;
        var summary = new JsonObject
        {
            ["session"] = value.Request.SessionId, ["revision"] = value.Revision, ["status"] = value.Status,
            ["phase"] = value.CurrentPhase, ["usage"] = snapshot["usage"]?.DeepClone(),
            ["maximumEstimatedInputTokens"] = value.RequestAccounting.Count == 0 ? null : value.RequestAccounting.Max(r => r.EstimatedInputTokens),
            ["maximumActualInputTokens"] = value.RequestAccounting.Where(r => r.InputTokens.HasValue).Select(r => r.InputTokens).DefaultIfEmpty(null).Max(),
            ["requestReasons"] = new JsonObject(value.RequestAccounting.SelectMany(r => r.HoleReasons.Values).GroupBy(reason => reason, StringComparer.Ordinal)
                .Select(g => new KeyValuePair<string, JsonNode?>(g.Key, JsonValue.Create(g.Count())))),
            ["requestCounts"] = snapshot["requestCounts"]?.DeepClone(),
            ["workflowCounts"] = new JsonArray(snapshot["construction"]!["workflows"]!.AsArray().OfType<JsonObject>().Select(workflow =>
                (JsonNode)new JsonObject(workflow.Where(p => p.Key is "workflowKey" or "totalHoles" or "deterministicallyResolvedHoles" or "modelHoles" or "modelHoleExposures" or "deterministicSchemaHoles" or "modelSchemaHoles" or "modelRequired" or "modelUsed" or "gates")
                    .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value?.DeepClone())))).ToArray()),
            ["diagnostics"] = new JsonArray(value.Diagnostics.Select(d => (JsonNode)new JsonObject { ["code"] = d.Code, ["location"] = d.Location }).ToArray())
        };
        Console.WriteLine(summary.ToJsonString());
        return;
    }
    Console.WriteLine($"Session {args[1]} revision={value.Revision} status={value.Status} calls={value.Usage?.Calls}");
    await using var db = contexts.CreateDbContext();
    foreach (var row in await db.Calls.AsNoTracking().Where(c => c.TenantId == tenant && c.SessionId == args[1]).OrderBy(c => c.RequestHash).ToListAsync(ct))
    {
        var record = await records.GetAsync("agent-planning-model-receipts-v5", tenant, row.PayloadKey, "GnOuGo.Agent.Server.Planning", ct);
        var response = record is null ? null : JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.LLMResponse);
        var accounting = value.RequestAccounting.SingleOrDefault(a => a.Id == row.RequestHash);
        Console.WriteLine($"request={row.RequestHash} status={row.Status} completion={response?.CompletionStatus} estimated={accounting?.EstimatedInputTokens} text_chars={response?.Text?.Length} usage={response?.Usage?.ToJsonString()}");
    }
    return;
}
var evidence = await store.LoadAsync(tenant, evidenceKey, ct) ?? throw new InvalidOperationException("Capture the evidence first.");
if (args[0] == "verify-fixtures")
{
    await CodeReviewExecutionFixture.VerifyAsync(evidence.PreparationCheckpoint!.ValidatedResults["discovery"]!.AsArray(), ct);
    return;
}
var catalog = new FrozenCatalog(evidence.PreparationCheckpoint!.ValidatedResults["discovery"]!.AsArray());
var services = new ServiceCollection().AddLogging();
services.AddKeyVaultMcpPersistence(KeyVaultDatabasePathResolver.Resolve(null, root));
await using var provider = services.BuildServiceProvider();
var vault = new KeyVaultRuntimeConfigStore(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<KeyVaultRuntimeConfigStore>.Instance);
var generator = evidence.Request.Options["generator"]!;
var options = new LLMRuntimeOptionsStore(Options.Create(new LLMOptions { DefaultProvider = generator["provider"]!.ToString(), DefaultModel = generator["model"]!.ToString() }), NullLogger<LLMRuntimeOptionsStore>.Instance);
var runtime = new SecureWorkflowRuntimeFactory(options, vault, mcpClientFactoryOverride: catalog);
using var ratesHttp = new HttpClient();
if (args[0] == "execute")
{
    if (args.Length != 4) throw new ArgumentException("Use execute SESSION PR_URL_INPUT_PORT REVIEW_INSTRUCTIONS_INPUT_PORT.");
    var saved = await store.LoadAsync(tenant, args[1], ct) ?? throw new InvalidOperationException("Session not found.");
    await CodeReviewExecution.RunAsync(saved, evidence.PreparationCheckpoint!.ValidatedResults["discovery"]!.AsArray(), options, vault, contexts, records,
        new EcbExchangeRateProvider(ratesHttp), args[2], args[3], ct);
    return;
}
var background = args[0] == "resume" || args[0] == "command" && args.Length > 2 && args[2] == "revise";
// Preserve the transport's already-redacted rejection before its public failure
// mapper removes provider details. Private schema coordinates remain encrypted;
// this observer neither dispatches nor turns a rejection into a model receipt.
var rejections = new System.Collections.Concurrent.ConcurrentQueue<string>();
void CaptureRejection(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs error)
{
    if (error.Exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest } http)
        rejections.Enqueue(http.Message);
}
AppDomain.CurrentDomain.FirstChanceException += CaptureRejection;
using var service = new PlanningSessionService(store, contexts, records, runtime, new TypedWorkflowPlanner(), new EcbExchangeRateProvider(ratesHttp),
    Options.Create(new WorkflowPlanningBudgetSettings()), Options.Create(new TypedWorkflowPlanningSettings { BackgroundProcessingEnabled = background }),
    Options.Create(new OpenTelemetrySettings { TenantId = tenant }), NullLogger<PlanningSessionService>.Instance);
PlanningSnapshot state;
if (args[0] == "start")
{
    var intent = evidence.Request.Prompt + "\n" + await File.ReadAllTextAsync(Path.Combine(root, "CodeReviewPolicy.txt"), ct);
    state = await service.StartAsync(args[1], intent, false, ct);
    Console.WriteLine("Session " + state.Request.SessionId);
}
else if (args[0] == "resume") state = await service.GetAsync(args[1], ct) ?? throw new InvalidOperationException("Session not found.");
else if (args[0] is "command" or "clarify-policy")
{
    state = await service.GetAsync(args[1], ct) ?? throw new InvalidOperationException("Session not found.");
    var kind = args[0] == "clarify-policy" ? "edit_intent" : args[2];
    if (kind == "approve")
    {
        var validation = await records.GetAsync("agent-planning-benchmark-validation-v5", tenant, state.Request.SessionId + ":" + state.ArtifactHash, "GnOuGo.Agent.Server.Planning", ct);
        var catalogHash = PlanningGraphCompiler.Fingerprint(evidence.PreparationCheckpoint!.ValidatedResults["discovery"]!.ToJsonString());
        if (validation is null || JsonNode.Parse(validation.Value) is not JsonArray { Count: 18 } results ||
            results.Any(result => result?["fixtureHash"]?.ToString() != CodeReviewExecutionFixture.Fingerprint || result["catalogHash"]?.ToString() != catalogHash || result["artifactHash"]?.ToString() != state.ArtifactHash))
            throw new InvalidOperationException("Benchmark approval requires independently passing execution fixtures for this exact artifact hash.");
    }
    if (kind == "accept_behavior")
    {
        // This is the user's isolated benchmark authority, never a production
        // selector or a modification of the frozen capability discovery.
        var methods = state.Preparation?.Capabilities.Select(c => c.Method).ToHashSet(StringComparer.Ordinal);
        if (methods is null || !methods.Contains("git_compare_refs") || !methods.Contains("copilot_review"))
            throw new InvalidOperationException("Benchmark review rejected: the locked capabilities omit the required comparison/review implementation. Revise capability preparation; descriptive behavior text grants no authority.");
    }
    state = await service.SubmitAsync(args[1], new() { Kind = kind, ExpectedRevision = state.Revision,
        Text = kind == "edit_intent" ? evidence.Request.Prompt + "\n" + await File.ReadAllTextAsync(Path.Combine(root, "CodeReviewPolicy.txt"), ct)
            : kind == "revise" && args.Length > 3 ? args[3] : null,
        Answers = kind == "answer" && args.Length > 3 ? JsonNode.Parse(args[3])!.AsObject() : null,
        ArtifactHash = kind is not ("answer" or "revise") && args.Length > 3 ? args[3] : state.ArtifactHash }, ct);
}
else throw new ArgumentException("Unknown benchmark command.");
if (background) await service.StartAsync(ct);
while (!PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status))
{
    Console.WriteLine($"revision={state.Revision} phase={state.CurrentPhase} calls={state.Usage?.Calls ?? 0}");
    if (background)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        state = await service.GetAsync(state.Request.SessionId, ct) ?? throw new InvalidOperationException("Session disappeared.");
    }
    else state = await service.SubmitAsync(state.Request.SessionId, new() { Kind = "advance", ExpectedRevision = state.Revision }, ct);
}
if (background) await service.StopAsync(ct);
AppDomain.CurrentDomain.FirstChanceException -= CaptureRejection;
if (!rejections.IsEmpty)
    await records.UpsertAsync("agent-planning-benchmark-rejections-v5", tenant, state.Request.SessionId,
        new JsonArray(rejections.Distinct(StringComparer.Ordinal).Select(message => (JsonNode?)JsonValue.Create(message)).ToArray()).ToJsonString(), EfPlanningSessionStore.Author, ct);
Console.WriteLine($"session={state.Request.SessionId} revision={state.Revision} status={state.Status} phase={state.CurrentPhase} calls={state.Usage?.Calls ?? 0} artifact={state.ArtifactHash}");
foreach (var finding in state.Diagnostics) Console.WriteLine($"finding={finding.Code} location={finding.Location}");
Environment.ExitCode = state.Status is PlanningStatus.FinalReview or PlanningStatus.Approved or PlanningStatus.BehaviorReview ? 0 : 2;

internal sealed class Contexts(string path, bool readOnly = false) : IDbContextFactory<PlanningDbContext>
{
    public PlanningDbContext CreateDbContext() => new(new DbContextOptionsBuilder<PlanningDbContext>().UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
    { DataSource = path, Mode = readOnly ? Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly : Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate }.ToString()).Options);
}
