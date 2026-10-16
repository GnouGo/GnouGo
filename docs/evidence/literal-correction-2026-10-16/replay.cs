using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.KeyVault.Core.Services;

// Public encrypted-storage reads only. The planner below receives a disposable
// clone and an exact receipt-only adapter; no session store is attached.
const string id = "4e9f48d922a54e15a09ed4ed6da0577b", tenant = "default", author = "GnOuGo.Agent.Server.Planning";
var store = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args[0]);
var records = new Dictionary<string, KeyVaultRecordValue[]>();
foreach (var collection in new[] { "sessions", "model-requests", "model-receipts" })
    records[collection] = (await store.ListAsync("agent-planning-" + collection + "-v10", tenant, author))
        .Where(r => r.Key.StartsWith(id + ":", StringComparison.Ordinal)).OrderBy(r => r.CreatedAt).ToArray();
var sessions = records["sessions"].Select(r => JsonSerializer.Deserialize(r.Value, PlanningJsonContext.Default.PlanningSession)!).ToArray();
var last = sessions.MaxBy(s => s.Revision)!;
var report = new JsonObject { ["session"] = id, ["retainedRevision"] = last.Revision, ["status"] = last.Status,
    ["calls"] = last.ModelCalls, ["repairs"] = last.ReplanAttempts, ["providerCalls"] = 0, ["mcpCalls"] = 0,
    ["receipts"] = new JsonArray() };
for (var i = 0; i < records["model-requests"].Length; i++)
{
    var request = Request(i); var receipt = Receipt(i); var response = receipt.Json ?? JsonNode.Parse(receipt.Text)!;
    if (request.StructuredOutputStrict != true || PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!).Count != 0)
        throw new Exception("Issued contract mismatch");
    report["receipts"]!.AsArray().Add((JsonNode)new JsonObject { ["call"] = i + 1, ["strictSchemaValid"] = true,
        ["kind"] = response["discoveryRequests"] is not null ? "discovery" : response["clarifications"] is not null ? "clarification" : "proposal",
        ["requestSha256"] = Hash(records["model-requests"][i].Value), ["receiptSha256"] = Hash(records["model-receipts"][i].Value) });
}
var issued = Request(7); var saved = Receipt(7);
var pending = sessions.Single(s => s.PendingCall?.Id == issued.ClientRequestId);
var clone = JsonSerializer.Deserialize(JsonSerializer.Serialize(pending, PlanningJsonContext.Default.PlanningSession), PlanningJsonContext.Default.PlanningSession)!;
var runtime = new ReceiptOnly(issued, saved);
var replayed = await new HybridWorkflowPlanner().AdvanceAsync(clone, new() { ExpectedRevision = clone.Revision }, runtime, CancellationToken.None);
if (runtime.Reads != 1 || replayed.ModelCalls != 8 || replayed.ReplanAttempts != 0 || replayed.ApprovedHash is not null ||
    !replayed.Diagnostics.Any(d => d.Code == "TASK_LITERAL_INVALID" && d.Location == "/tasks/define_workflow/outputs/workflowDefinition"))
    throw new Exception("Retained failure was not reproduced");
report["offlineReplay"] = new JsonObject { ["receiptReads"] = runtime.Reads,
    ["diagnostics"] = JsonSerializer.SerializeToNode(replayed.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic),
    ["callsUnchanged"] = replayed.ModelCalls, ["repairsUnchanged"] = replayed.ReplanAttempts };
// Freeze the failed proposal, not private prompts, credentials or full catalogs.
var sanitized = (saved.Json ?? JsonNode.Parse(saved.Text)!).DeepClone();
var context = JsonNode.Parse(issued.Prompt[(issued.Prompt.IndexOf("\n{", StringComparison.Ordinal) + 1)..])!;
var repository = Regex.Match(context["request"]!.ToString(), @"https://github\.com/([^/\s]+)/([^/\s]+)");
Sanitize(sanitized);
var fixture = sanitized.ToJsonString(new() { WriteIndented = true });
report["sanitizedProposalSha256"] = Hash(fixture);
report["sanitization"] = "HTTPS URLs and source repository labels replaced by inert fixture equivalents; no binding, task, requirement or interpolation was corrected.";
report["resolvedCapabilities"] = new JsonArray(last.Discovery.Resolved.Concat(last.Catalog!.Capabilities)
    .DistinctBy(c => c.Id).Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["stepType"] = c.StepType, ["method"] = c.Method }).ToArray());
report["limitation"] = "The recorded exact contracts do not establish command/check execution or cleanup capability. No code review, command, GitHub submission or live permission was exercised.";
foreach (var (collection, snapshots) in records)
    foreach (var record in snapshots)
        if (record.Value != (await store.GetAsync("agent-planning-" + collection + "-v10", tenant, record.Key, author))!.Value)
            throw new Exception("Historical evidence changed");
report["historicalRecordsUnchanged"] = true;
Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);
await File.WriteAllTextAsync(args[1], fixture);
await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(args[1])!, "retained-generation.json"),
    JsonSerializer.Serialize(last.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions));
report["retainedGeneration"] = JsonSerializer.SerializeToNode(last.Request.Generation, PlanningJsonContext.Default.PlanningGenerationOptions);
Console.WriteLine(report.ToJsonString(new() { WriteIndented = true }));

LLMRequest Request(int i) => JsonSerializer.Deserialize(records["model-requests"][i].Value, PlanningJsonContext.Default.LLMRequest)!;
LLMResponse Receipt(int i) => JsonSerializer.Deserialize(records["model-receipts"][i].Value, PlanningJsonContext.Default.LLMResponse)!;
static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
void Sanitize(JsonNode? node)
{
    if (node is JsonObject obj)
        foreach (var (key, value) in obj.ToArray())
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                var clean = Regex.Replace(text, @"https://[^\s""']+", "https://fixture.invalid/change");
                if (repository.Success) clean = clean.Replace(repository.Groups[1].Value, "ExampleOrg", StringComparison.OrdinalIgnoreCase)
                    .Replace(repository.Groups[2].Value, "ExampleProject", StringComparison.OrdinalIgnoreCase);
                obj[key] = clean;
            }
            else Sanitize(value);
    else if (node is JsonArray array)
        foreach (var value in array) Sanitize(value);
}
sealed class ReceiptOnly(LLMRequest issued, LLMResponse receipt) : IPlanningRuntime
{
    public int Reads;
    public ICapabilityCatalog Capabilities => throw new Exception("No discovery");
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request, CancellationToken ct) => throw new Exception("No discovery");
    public Task<LLMResponse> CallAsync(LLMRequest request, string purpose, CancellationToken ct)
    {
        if (++Reads != 1 || JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest) != JsonSerializer.Serialize(issued, PlanningJsonContext.Default.LLMRequest))
            throw new Exception("Unexpected inference request");
        return Task.FromResult(receipt);
    }
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request, CancellationToken ct) => throw new Exception("Invalid literal must stop compilation");
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct) => throw new Exception("No approval");
    public Task CheckpointAsync(PlanningSession state, CancellationToken ct) => Task.CompletedTask;
}
