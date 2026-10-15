using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

const string tenant = "default", author = "GnOuGo.Agent.Server.Planning";
var store = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args.Single());
var reports = new JsonArray();
foreach (var id in new[] { "9d073e284c5142a0923d3f8dd99b6966", "b992404c2ada42f2916ae2aa2232f642" })
{
    var states = (await store.ListAsync("agent-planning-sessions-v10", tenant, author))
        .Where(r => r.Key.StartsWith(id + ":", StringComparison.Ordinal)).ToArray();
    var requests = (await store.ListAsync("agent-planning-model-requests-v10", tenant, author))
        .Where(r => r.Key.StartsWith(id + ":", StringComparison.Ordinal)).ToArray();
    var receipts = (await store.ListAsync("agent-planning-model-receipts-v10", tenant, author))
        .Where(r => r.Key.StartsWith(id + ":", StringComparison.Ordinal)).ToArray();
    var latest = states.Select(r => JsonSerializer.Deserialize(r.Value, PlanningJsonContext.Default.PlanningSession)!).MaxBy(s => s.Revision)!;
    var pending = states.Select(r => JsonSerializer.Deserialize(r.Value, PlanningJsonContext.Default.PlanningSession)!)
        .Where(s => s.PendingCall is not null).MaxBy(s => s.Revision)!;
    if (pending.Request.TenantId != tenant || pending.Request.SessionId != id) throw new Exception("Ownership mismatch");
    var key = id + ":" + pending.PendingCall!.Id;
    var requestRecord = requests.Single(r => r.Key == key); var receiptRecord = receipts.Single(r => r.Key == key);
    var issued = JsonSerializer.Deserialize(requestRecord.Value, PlanningJsonContext.Default.LLMRequest)!;
    if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(issued, PlanningJsonContext.Default.LLMRequest),
        JsonSerializer.SerializeToNode(pending.PendingCall.Request, PlanningJsonContext.Default.LLMRequest))) throw new Exception("Issued request mismatch");
    var receipt = JsonSerializer.Deserialize(receiptRecord.Value, PlanningJsonContext.Default.LLMResponse)!;
    var response = receipt.Json ?? JsonNode.Parse(receipt.Text)!;
    if (PlanningContractValidation.ValidateInstanceFindings(response, issued.StructuredOutputSchema!).Count != 0) throw new Exception("Invalid original schema response");
    if (response["requirements"] is not null || response["plan"] is null || response["clarifications"] is not null) throw new Exception("Unexpected retained defect");
    var runtime = new ReadOnlyRuntime(issued, receipt, pending.Discovery, pending.Catalog!);
    var planner = new HybridWorkflowPlanner();
    var applied = await planner.AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, CancellationToken.None);
    if (applied.ModelCalls != latest.ModelCalls || applied.ReplanAttempts != 0 || applied.Requirements is not null || applied.Plan is not null ||
        !applied.Diagnostics.Any(d => d.Code == "REQUIREMENTS_INVALID")) throw new Exception("Replay did not reproduce the defect");
    var corrected = await planner.AdvanceAsync(applied, new() { ExpectedRevision = applied.Revision }, runtime, CancellationToken.None);
    if (corrected.Status != PlanningStatus.Clarification || corrected.ModelCalls != latest.ModelCalls + 1 || corrected.ReplanAttempts != 1 ||
        corrected.ApprovedHash is not null || corrected.Plan is not null || runtime.ReceiptReads != 1 || runtime.Corrections != 1) throw new Exception("Correction routing failed");
    foreach (var (collection, records) in new[] { ("agent-planning-sessions-v10", states),
        ("agent-planning-model-requests-v10", requests), ("agent-planning-model-receipts-v10", receipts) })
        foreach (var record in records)
            if (record.Value != (await store.GetAsync(collection, tenant, record.Key, author))!.Value) throw new Exception("Historical record changed");
    reports.Add((JsonNode)new JsonObject
    {
        ["session"] = id, ["retainedRevision"] = latest.Revision, ["retainedCalls"] = latest.ModelCalls,
        ["retainedRepairs"] = latest.ReplanAttempts, ["originalRequestSha256"] = Hash(requestRecord.Value),
        ["originalReceiptSha256"] = Hash(receiptRecord.Value), ["originalSchemaSha256"] = Hash(issued.StructuredOutputSchema!.ToJsonString()),
        ["strictStructuredOutput"] = issued.StructuredOutputStrict, ["originalSchemaFindings"] = 0,
        ["replayedDiagnostic"] = JsonSerializer.SerializeToNode(applied.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
        ["receiptReads"] = runtime.ReceiptReads, ["deterministicCorrectionCalls"] = runtime.Corrections,
        ["correctiveInputEstimate"] = runtime.Estimate, ["effectiveRequestLimit"] = corrected.Request.Generation.MaxInputTokensPerRequest,
        ["finalDisposableStatus"] = corrected.Status, ["finalDisposableCalls"] = corrected.ModelCalls,
        ["finalDisposableRepairs"] = corrected.ReplanAttempts, ["providerCalls"] = 0, ["mcpCalls"] = 0,
        ["historicalRecordsUnchanged"] = true,
        ["limit"] = "Routing/receipt proof only. A deterministic clarification was returned; this is not approval or proof of the retained plan's business composition."
    });
}
Console.WriteLine(reports.ToJsonString(new() { WriteIndented = true }));
static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

sealed class ReadOnlyRuntime(LLMRequest original, LLMResponse receipt, CapabilityDiscoveryState discovery, PlanningCatalog catalog) : IPlanningRuntime, ICapabilityCatalog
{
    internal int ReceiptReads, Corrections, Estimate;
    public ICapabilityCatalog Capabilities => this;
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        if (request.ClientRequestId == original.ClientRequestId)
        {
            if (++ReceiptReads != 1 || JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest) != JsonSerializer.Serialize(original, PlanningJsonContext.Default.LLMRequest)) throw new Exception("Unexpected receipt read");
            return Task.FromResult(receipt);
        }
        if (++Corrections != 1 || purpose != "replan" || !request.Prompt.Contains("REQUIREMENTS_INVALID", StringComparison.Ordinal)) throw new Exception("Unexpected corrective call");
        Estimate = checked((Encoding.UTF8.GetByteCount(request.Prompt) + Encoding.UTF8.GetByteCount(request.StructuredOutputSchema!.ToJsonString()) + 2) / 3 + 256);
        return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("""
            {"requirements":null,"discoveryRequests":null,"plan":null,"clarifications":[{"id":"intent","question":"Which business outcomes and public interfaces should be guaranteed?","alternatives":[],"recommended":null}]}
            """) });
    }
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new Exception("No discovery dispatch");
    public Task<IReadOnlyList<CapabilitySource>> ListSourcesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CapabilitySource>>(discovery.Sources);
    public Task<CapabilityPage> ListAsync(string sourceId, string? cursor, CancellationToken ct, string? query = null, string? producedArtifactKind = null) => throw new Exception("No metadata dispatch");
    public Task<PlanningCapability> ResolveAsync(CapabilitySummary summary, CancellationToken ct) => Task.FromResult(
        discovery.Resolved.Concat(catalog.Capabilities).FirstOrDefault(c => c.Id == summary.Id && c.Version == summary.Version) ?? throw new Exception("No new contract resolution"));
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => throw new Exception("No workflow validation expected for clarification");
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new Exception("No approval");
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
}
