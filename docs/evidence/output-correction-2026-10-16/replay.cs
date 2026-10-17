using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

const string id = "51b24cad299443eb8f3d0391db2f8285", tenant = "default", author = "GnOuGo.Agent.Server.Planning";
var store = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args.Single());
var records = new Dictionary<string, GnOuGo.KeyVault.Core.Services.KeyVaultRecordValue[]>();
foreach (var collection in new[] { "sessions", "model-requests", "model-receipts" })
    records[collection] = (await store.ListAsync("agent-planning-" + collection + "-v10", tenant, author))
        .Where(r => r.Key.StartsWith(id + ":", StringComparison.Ordinal)).OrderBy(r => r.CreatedAt).ToArray();
var sessions = records["sessions"].Select(r => JsonSerializer.Deserialize(r.Value, PlanningJsonContext.Default.PlanningSession)!).ToArray();
var report = new JsonObject { ["session"] = id, ["retainedRevision"] = sessions.Max(s => s.Revision),
    ["providerCalls"] = 0, ["externalOperations"] = 0, ["receipts"] = new JsonArray(), ["replays"] = new JsonArray() };
for (var call = 1; call <= records["model-requests"].Length; call++)
{
    var request = Request(call); var receipt = Receipt(call);
    var response = receipt.Json ?? JsonNode.Parse(receipt.Text)!;
    var violations = PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!);
    if (violations.Count > 0 || request.StructuredOutputStrict != true) throw new Exception("Original structured-output contract mismatch");
    report["receipts"]!.AsArray().Add((JsonNode)new JsonObject { ["call"] = call, ["strict"] = true, ["schemaFindings"] = violations.Count,
        ["requestSha256"] = Hash(records["model-requests"][call - 1].Value), ["receiptSha256"] = Hash(records["model-receipts"][call - 1].Value) });
}
foreach (var call in new[] { 6, 8 })
{
    var request = Request(call); var receipt = Receipt(call);
    var pending = Clone(sessions.Single(s => s.PendingCall?.Id == request.ClientRequestId));
    if (Serialize(pending.PendingCall!.Request) != Serialize(request)) throw new Exception("Issued request changed");
    var runtime = new OfflineRuntime(request, receipt);
    var restored = await new HybridWorkflowPlanner().AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, CancellationToken.None);
    if (!restored.Diagnostics.Any(d => d.Code == "REVISION_SCOPE_CHANGED" && d.Location == "/root/outputs") || runtime.Reads != 1)
        throw new Exception("Historical preservation changed");
    Add("historical_call_" + call, restored, runtime);
}
foreach (var sample in new[] { "retained_call_8", "retained_call_6", "minimal_call_5" })
{
    var state = Clone(sessions.Single(s => s.Revision == (sample == "retained_call_8" ? 16 : 10)));
    if (state.PendingCall is not null || state.Request.Options["automatic_correction_progress"] is not null) throw new Exception("Expected a fresh correction boundary");
    state.Request.SessionId = "offline-output-correction-" + sample;
    state.Request.Name = "offline-output-correction";
    // These are deterministic samples for a new disposable request, never an
    // application/rewrite of the historical issued receipt or session.
    var response = (Receipt(sample == "retained_call_8" ? 8 : 6).Json!).DeepClone();
    if (sample == "minimal_call_5")
    {
        var plan = Receipt(5).Json!["plan"]!.DeepClone();
        plan["root"]!["outputs"] = new JsonArray(plan["root"]!["outputs"]!.AsArray().Where(o => o!["name"]!.ToString() == "workflow_file").Select(o => o!.DeepClone()).ToArray());
        response["plan"] = plan;
    }
    var runtime = new OfflineRuntime(null, new() { Json = response });
    var planner = new HybridWorkflowPlanner();
    var corrected = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, CancellationToken.None);
    if (runtime.Reads != 1 || corrected.ModelCalls != state.ModelCalls + 1 || corrected.ReplanAttempts != state.ReplanAttempts + 1 || corrected.ApprovedHash is not null)
        throw new Exception("Accounting or approval changed");
    if (sample == "retained_call_6")
    {
        if (!corrected.Diagnostics.Any(d => d.Location == "/tasks/generate_workflow/inputs/4/value/text")) throw new Exception("Unrelated instruction change accepted");
    }
    else if (corrected.Status != PlanningStatus.FinalReview) throw new Exception("Expected review: " + string.Join(';', corrected.Diagnostics));
    Add(sample, corrected, runtime);
    if (corrected.Status == PlanningStatus.FinalReview)
    {
        PlanningArtifactApproval.Verify(corrected);
        await planner.AdvanceAsync(Clone(corrected), new() { ExpectedRevision = corrected.Revision }, runtime, CancellationToken.None);
        if (runtime.Reads != 1) throw new Exception("Completed proposal was redispatched");
    }
}
foreach (var (collection, snapshots) in records)
    foreach (var record in snapshots)
        if (record.Value != (await store.GetAsync("agent-planning-" + collection + "-v10", tenant, record.Key, author))!.Value)
            throw new Exception("Historical evidence changed");
report["historicalRecordsUnchanged"] = true;
report["limitation"] = "Static review of the export correction does not establish that the retained file-generation composition performs the requested code review. No execution or approval occurred.";
Console.WriteLine(report.ToJsonString(new() { WriteIndented = true }));

LLMRequest Request(int call) => JsonSerializer.Deserialize(records["model-requests"][call - 1].Value, PlanningJsonContext.Default.LLMRequest)!;
LLMResponse Receipt(int call) => JsonSerializer.Deserialize(records["model-receipts"][call - 1].Value, PlanningJsonContext.Default.LLMResponse)!;
static PlanningSession Clone(PlanningSession state) => JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
static string Serialize(LLMRequest request) => JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest);
static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
void Add(string name, PlanningSession state, OfflineRuntime runtime) => report["replays"]!.AsArray().Add((JsonNode)new JsonObject
{
    ["name"] = name, ["status"] = state.Status, ["version"] = state.Request.Options["automatic_correction_progress"]?["version"]?.DeepClone(),
    ["sampleReads"] = runtime.Reads, ["modelCalls"] = state.ModelCalls, ["replanAttempts"] = state.ReplanAttempts,
    ["diagnostics"] = JsonSerializer.SerializeToNode(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
    ["requestEstimate"] = runtime.Estimate, ["requestLimit"] = state.Request.Generation.MaxInputTokensPerRequest,
    ["yamlBytes"] = state.Yaml is null ? null : Encoding.UTF8.GetByteCount(state.Yaml), ["approved"] = state.ApprovedHash is not null
});
sealed class OfflineRuntime(LLMRequest? issued, LLMResponse response) : IPlanningRuntime
{
    private readonly WorkflowPlanningRuntime actual = new(new WorkflowEngine(), (_, _) => Task.CompletedTask);
    public int Reads, Estimate;
    public ICapabilityCatalog Capabilities => throw new Exception("No discovery");
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new Exception("No discovery");
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        if (++Reads != 1 || purpose != "replan" || issued is not null &&
            JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest) != JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest))
            throw new Exception("Unexpected request");
        if (issued is null && !request.Prompt.Contains("Remove only the diagnosed unaccepted exports", StringComparison.Ordinal)) throw new Exception("Missing scoped guidance");
        Estimate = checked((Encoding.UTF8.GetByteCount(request.Prompt) + Encoding.UTF8.GetByteCount(request.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256);
        return Task.FromResult(response);
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => actual.ValidateAsync(request, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new Exception("No approval");
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
}
