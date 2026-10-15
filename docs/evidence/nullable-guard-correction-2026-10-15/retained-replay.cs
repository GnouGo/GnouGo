using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

const string author = "GnOuGo.Planning.Benchmark", tenant = "benchmark", campaign = "schema-portability-20261002";
const string run = "initialrequirements20261015a-amazon-1";
var store = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args[0]);
var key = campaign + ":run:" + run;
var record = await store.GetAsync("planning-schema-portability", tenant, key, author) ?? throw new Exception("Run missing");
var retained = JsonNode.Parse(record.Value)!["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
if (retained.Request.TenantId != tenant || retained.Request.SessionId != run || retained.Revision != 5 || retained.Status != PlanningStatus.Stopped)
    throw new Exception("Retained ownership/revision/status mismatch");
var receipts = (await store.ListAsync("planning-evaluation-receipts", tenant, author))
    .Where(r => r.Key.StartsWith(campaign + ":" + run + ":", StringComparison.Ordinal)).OrderBy(r => r.Key).ToArray();
if (receipts.Length != 4) throw new Exception("Expected four receipts");
var responses = new List<LLMResponse>(); var hashes = new JsonArray();
foreach (var receipt in receipts)
{
    var request = await store.GetAsync("planning-evaluation-requests", tenant, receipt.Key, author) ?? throw new Exception("Request missing");
    var issued = JsonSerializer.Deserialize(request.Value, PlanningJsonContext.Default.LLMRequest)!;
    var response = JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse)!;
    var body = response.Json ?? JsonNode.Parse(response.Text)!;
    if (PlanningContractValidation.ValidateInstanceFindings(body, issued.StructuredOutputSchema!).Count != 0) throw new Exception("Original schema mismatch");
    response.Json = body; responses.Add(response);
    hashes.Add(new JsonObject { ["request"] = PlanningGraphCompiler.Fingerprint(request.Value), ["receipt"] = PlanningGraphCompiler.Fingerprint(receipt.Value),
        ["schema"] = PlanningGraphCompiler.Fingerprint(issued.StructuredOutputSchema!.ToJsonString()), ["schemaFindings"] = 0 });
}
var reports = new JsonArray();
foreach (var version in new[] { 1, 2 })
{
    var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(retained, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
    var state = new PlanningSession { Catalog = copy.Catalog, Discovery = copy.Discovery, Request = copy.Request };
    state.Request.SessionId = "offline-guard-v" + version; state.Request.Baseline = null; state.Request.RevisionContext = null;
    state.Request.Options.Remove("automatic_correction_progress"); state.Request.Options["automatic_correction"] = true;
    var runtime = new OfflineRuntime(state.Catalog!, state.Discovery, [responses[1], responses[2]]);
    var planner = new HybridWorkflowPlanner();
    state = await planner.AdvanceAsync(state, new(), runtime, CancellationToken.None);
    if (state.Diagnostics.Count(d => d.Code == "TASK_CONDITION_TYPE" || d.ValidationStage == "contract:TASK_CONDITION_TYPE") != 2)
        throw new Exception("Expected original guard diagnostics: " + string.Join(';', state.Diagnostics));
    if (version == 1)
    {
        state.Request.Baseline = state.Plan;
        state.Request.Options["automatic_correction_progress"] = new JsonObject { ["version"] = 1, ["attempts"] = new JsonArray(),
            ["locations"] = new JsonArray("/tasks/accept_cookies/requires", "/tasks/extract_candidates_by_page/requires") };
    }
    state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
    if (version == 1 && !state.Diagnostics.Any(d => d.Code == "REVISION_SCOPE_CHANGED")) throw new Exception("Legacy preservation changed");
    if (version == 2 && state.Diagnostics.Any(d => d.Code is "REVISION_SCOPE_CHANGED" or "AUTOMATIC_GUARD_CORRECTION_UNSAFE")) throw new Exception("Guard correction rejected");
    reports.Add(new JsonObject { ["version"] = version, ["status"] = state.Status, ["deterministicCalls"] = runtime.Calls,
        ["corrections"] = state.ReplanAttempts, ["approved"] = state.ApprovedHash is not null,
        ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
        ["progress"] = state.Request.Options["automatic_correction_progress"]!.DeepClone() });
    if (version == 2) await File.WriteAllTextAsync(Path.Combine(args[1], "minimal-guard-taskplan.json"), JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
}
if ((await store.GetAsync("planning-schema-portability", tenant, key, author))!.Value != record.Value) throw new Exception("Historical run changed");
foreach (var receipt in receipts)
    if ((await store.GetAsync("planning-evaluation-receipts", tenant, receipt.Key, author))!.Value != receipt.Value) throw new Exception("Receipt changed");
Console.WriteLine(new JsonObject { ["historicalRunUnchanged"] = true, ["retainedRevision"] = 5, ["providerCalls"] = 0, ["mcpCalls"] = 0,
    ["originalRequests"] = hashes, ["replays"] = reports }.ToJsonString(new() { WriteIndented = true }));

sealed class OfflineRuntime(PlanningCatalog catalog, CapabilityDiscoveryState discovery, LLMResponse[] responses) : IPlanningRuntime, ICapabilityCatalog
{
    internal int Calls;
    private readonly WorkflowPlanningRuntime validator = new(new WorkflowEngine(), (_, _) => Task.CompletedTask);
    public ICapabilityCatalog Capabilities => this;
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        if (Calls >= responses.Length) throw new Exception("Unexpected extra inference request");
        var response = responses[Calls++];
        if (PlanningContractValidation.ValidateInstanceFindings(response.Json, request.StructuredOutputSchema!).Count != 0)
            throw new Exception("Saved payload does not satisfy disposable issued schema");
        return Task.FromResult(response);
    }
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new Exception("No discovery");
    public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>(discovery.Sources);
    public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => throw new Exception("No discovery");
    public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(catalog.Capabilities.Concat(discovery.Resolved).First(c => c.Id == summary.Id && c.Version == summary.Version));
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => validator.ValidateAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog value, CancellationToken ct) => throw new Exception("No approval");
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
}
