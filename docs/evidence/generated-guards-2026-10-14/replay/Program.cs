using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
using GnOuGo.Document.Mcp;
using GnOuGo.Planning.Examples;

if (args.Length != 4) throw new ArgumentException("workspace session-id corrected-generic-proposal output-directory");
const string tenant = "default", author = "GnOuGo.Agent.Server.Planning", sessions = "agent-planning-sessions-v10";
var vault = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args[0]);
var records = (await vault.ListAsync(sessions, tenant, author)).Where(r => r.Key.StartsWith(args[1] + ":", StringComparison.Ordinal)).ToArray();
var record = records.OrderByDescending(r => JsonNode.Parse(r.Value)!["revision"]!.GetValue<long>()).First();
var baseline = JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.PlanningSession)!;
if (baseline.Request.TenantId != tenant || baseline.Request.SessionId != args[1] || baseline.Revision != 4 || baseline.ApprovedHash is not null || baseline.PendingCall is not null || baseline.Status != PlanningStatus.Stopped)
    throw new Exception("Expected the owned, stopped session without an outstanding request");
var sourceReceipts = (await vault.ListAsync("agent-planning-model-receipts-v10", tenant, author)).Where(r => r.Key.StartsWith(args[1] + ":", StringComparison.Ordinal)).ToArray();
if (sourceReceipts.Length != 2) throw new Exception("Expected exactly the two retained committed responses");
var receiptEvidence = new JsonArray();
foreach (var receipt in sourceReceipts)
{
    var issued = await vault.GetAsync("agent-planning-model-requests-v10", tenant, receipt.Key, author) ?? throw new Exception("Original request missing");
    var request = JsonSerializer.Deserialize(issued.Value, PlanningJsonContext.Default.LLMRequest)!;
    var response = JsonSerializer.Deserialize(receipt.Value, PlanningJsonContext.Default.LLMResponse)!;
    if (PlanningContractValidation.ValidateInstance(response.Json, request.StructuredOutputSchema!).Count != 0) throw new Exception("Original response schema mismatch");
    if (!JsonNode.DeepEquals(JsonNode.Parse(response.Text!), response.Json)) throw new Exception("Text/parsed response mismatch");
    receiptEvidence.Add(new JsonObject { ["request_sha256"] = Hash(issued.Value), ["receipt_sha256"] = Hash(receipt.Value),
        ["original_schema_valid"] = true, ["text_equals_parsed"] = true, ["malformed_field_occurrences"] = response.Text!.Split("available},{", StringSplitOptions.None).Length - 1 });
}
var originalTasks = Tasks(baseline.Plan!.Root).ToDictionary(t => t.Id);
var minimal = JsonSerializer.Deserialize(JsonSerializer.Serialize(baseline.Plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
foreach (var task in Tasks(minimal.Root).Where(t => t.Id is "extract_candidates_by_page" or "extract_product_row"))
{
    if (task.Requires?.Predicate != "and" || task.Requires.Items.Count != 2 || task.Requires.Items[1].Port != "available},{") throw new Exception("Unexpected original guard");
    task.Requires.Items[1].Port = "available";
}
if (new TaskPlanCompiler().Compile(minimal, baseline.Catalog!).Diagnostics.Count != 0) throw new Exception("Minimal reference correction failed");

// Inspect only registered producer metadata; the factory rejects every tool invocation.
var contracts = RealProductContracts.Capture(new DocumentPolicy(new(), args[0]));
contracts["GnOuGo.Browser.Mcp"] = contracts["browser"]!.DeepClone(); contracts.Remove("browser");
contracts["GnOuGo.Document.Mcp"] = contracts["document"]!.DeepClone(); contracts.Remove("document");
var factory = new RealProductContracts.Factory(contracts);
var actual = new WorkflowPlanningRuntime(new() { McpClientFactory = factory }, (_, _) => Task.CompletedTask);
var proposal = JsonSerializer.Deserialize(File.ReadAllText(args[2]), PlanningJsonContext.Default.PlanningProposal)!;
var cleanupRequirement = proposal.Requirements!.Outcomes.Single(o => o.Id == "cleanup_browser");
proposal.Requirements = JsonSerializer.Deserialize(JsonSerializer.Serialize(baseline.Requirements, PlanningJsonContext.Default.PlanningRequirements), PlanningJsonContext.Default.PlanningRequirements)!;
proposal.Requirements.Outcomes.Add(cleanupRequirement); proposal.Plan!.Inputs = baseline.Plan.Inputs;
var revisedTasks = Tasks(proposal.Plan.Root).ToDictionary(t => t.Id);
foreach (var (revised, original) in new[] { ("read_results", "observe_search_results"), ("read_product", "observe_product_page"), ("write", "write_excel_file") })
{
    var destination = revisedTasks[revised]; var source = originalTasks[original]; destination.Operation = source.Operation;
    var dynamicName = revised == "write" ? "rows" : "url";
    var binding = destination.Inputs.Single(i => i.Name == dynamicName);
    destination.Inputs = source.Inputs.Select(i => i.Name == dynamicName ? binding : i).ToList();
}
var browserSource = baseline.Discovery.Pages.Single(p => p.Capabilities.Any(c => c.Name == "browser_get_content")).SourceId;
var page = await actual.Capabilities.ListAsync(browserSource, null, CancellationToken.None, "browser_close");
var closeSummary = page.Capabilities.Single(c => c.Name == "browser_close");
var close = await actual.Capabilities.ResolveAsync(closeSummary, CancellationToken.None);
revisedTasks["release"].Operation = TaskOperations.Describe(close).Id;
var copy = JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.PlanningSession)!;
var runtime = new OfflineRuntime(actual, baseline.Catalog!, proposal, closeSummary);
var planner = new HybridWorkflowPlanner();
copy = await planner.AdvanceAsync(copy, new() { Kind = "revise", ExpectedRevision = copy.Revision, ArtifactHash = copy.ComputeArtifactHash(), PreserveRequirements = false,
    Text = "Correct the two malformed guards and data adaptation. Preserve all four accepted requirements, article input, filePath output, search and first ten ordered product visits, exact three columns, destination and external literal arguments. Add only Browser cleanup. Use producer-derived assertions, complete per-page extraction, compact ID selection, offered/original lookup, exact observed blocks and deterministic header/rows. Missing navigation data and incomplete acquisitions fail before affected work." }, runtime, CancellationToken.None);
for (var i = 0; i < 6 && copy.Status == PlanningStatus.Generating; i++) copy = await planner.AdvanceAsync(copy, new() { ExpectedRevision = copy.Revision }, runtime, CancellationToken.None);
if (copy.Status != PlanningStatus.FinalReview || copy.ReplanAttempts != baseline.ReplanAttempts || copy.ApprovedHash is not null)
    throw new Exception("Offline revision failed: " + copy.Status + " " + string.Join(';', copy.Diagnostics));
if (factory.InvocationAttempts != 0) throw new Exception("Unexpected MCP execution");
var expectedRequirements = JsonSerializer.SerializeToNode(baseline.Requirements, PlanningJsonContext.Default.PlanningRequirements)!;
var resultRequirements = JsonSerializer.SerializeToNode(copy.Requirements, PlanningJsonContext.Default.PlanningRequirements)!;
var cleanup = resultRequirements["outcomes"]!.AsArray().Single(r => r!["id"]!.ToString() == "cleanup_browser"); resultRequirements["outcomes"]!.AsArray().Remove(cleanup);
if (!JsonNode.DeepEquals(expectedRequirements, resultRequirements)) throw new Exception("Unrelated requirements changed");
if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(copy.Request, PlanningJsonContext.Default.PlanningRequest)!["generation"], JsonSerializer.SerializeToNode(baseline.Request, PlanningJsonContext.Default.PlanningRequest)!["generation"])) throw new Exception("Generation allowance changed");
Directory.CreateDirectory(args[3]);
File.WriteAllText(Path.Combine(args[3], "retained-taskplan.json"), JsonSerializer.Serialize(baseline.Plan, PlanningJsonContext.Default.TaskPlan));
File.WriteAllText(Path.Combine(args[3], "reference-only-taskplan.json"), JsonSerializer.Serialize(minimal, PlanningJsonContext.Default.TaskPlan));
File.WriteAllText(Path.Combine(args[3], "corrected-taskplan.json"), JsonSerializer.Serialize(copy.Plan, PlanningJsonContext.Default.TaskPlan));
File.WriteAllText(Path.Combine(args[3], "corrected-proposal.json"), JsonSerializer.Serialize(new PlanningProposal { Plan = copy.Plan, Requirements = copy.Requirements }, PlanningJsonContext.Default.PlanningProposal));
File.WriteAllText(Path.Combine(args[3], "corrected.yaml"), copy.Yaml);
var unchanged = record.Value == (await vault.GetAsync(sessions, tenant, record.Key, author))!.Value;
foreach (var receipt in sourceReceipts) unchanged &= receipt.Value == (await vault.GetAsync("agent-planning-model-receipts-v10", tenant, receipt.Key, author))!.Value;
if (!unchanged) throw new Exception("Retained evidence changed");
var report = new JsonObject { ["session"] = args[1], ["source_revision"] = baseline.Revision, ["source_sha256"] = Hash(record.Value), ["receipts"] = receiptEvidence,
    ["offline_revision"] = copy.Revision, ["offline_status"] = copy.Status, ["calls_before"] = baseline.ModelCalls, ["calls_after_offline"] = copy.ModelCalls,
    ["deterministic_responses"] = runtime.Calls, ["repairs"] = copy.ReplanAttempts, ["provider_calls"] = 0, ["mcp_invocations"] = factory.InvocationAttempts,
    ["active_ms_before"] = baseline.ActiveMilliseconds, ["active_ms_after_offline"] = copy.ActiveMilliseconds, ["original_unchanged"] = unchanged,
    ["profile"] = copy.Request.Options["compilation_profile"]?.DeepClone(), ["artifact_hash"] = copy.ComputeArtifactHash(), ["approved"] = false,
    ["tasks_before"] = Tasks(baseline.Plan.Root).Count(), ["tasks_after"] = Tasks(copy.Plan!.Root).Count(), ["yaml_bytes"] = Encoding.UTF8.GetByteCount(copy.Yaml!),
    ["request_bytes"] = new JsonArray(runtime.RequestBytes.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
File.WriteAllText(Path.Combine(args[3], "replay.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(report.ToJsonString());
static IEnumerable<PlanTask> Tasks(TaskScope scope) { foreach (var t in scope.Tasks.Concat(scope.Always)) { yield return t; if (t.Body is not null) foreach (var c in Tasks(t.Body)) yield return c; if (t.Otherwise is not null) foreach (var c in Tasks(t.Otherwise)) yield return c; } }
static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

sealed class OfflineRuntime(WorkflowPlanningRuntime actual, PlanningCatalog original, PlanningProposal corrected, CapabilitySummary close) : IPlanningRuntime
{
    public int Calls;
    public List<int> RequestBytes = [];
    public ICapabilityCatalog Capabilities => actual.Capabilities;
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest r, CancellationToken ct) => throw new Exception("Unexpected replacement discovery");
    public Task<LLMResponse> CallAsync(LLMRequest r, string purpose, CancellationToken ct)
    {
        Calls++; RequestBytes.Add(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest)));
        var proposal = Calls switch {
            1 => new PlanningProposal { Requirements = corrected.Requirements, DiscoveryRequests = [new(close.SourceId, Query: "browser_close")] },
            2 => new PlanningProposal { Requirements = corrected.Requirements, DiscoveryRequests = [new(close.SourceId, OperationIds: [close.Operation!.Id])] },
            3 => corrected,
            _ => throw new Exception("Unexpected additional offline request") };
        return Task.FromResult(new LLMResponse { Json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), r.StructuredOutputSchema!.AsObject(), r.StructuredOutputSchema.AsObject()) });
    }
    public Task CheckpointAsync(PlanningSession s, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest r, CancellationToken ct) => actual.ValidateAsync(r, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct)
    {
        foreach (var c in original.Capabilities)
            if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(c, PlanningJsonContext.Default.PlanningCapability), JsonSerializer.SerializeToNode(catalog.Capabilities.Single(x => x.Id == c.Id), PlanningJsonContext.Default.PlanningCapability))) throw new Exception("Original contract changed");
        return Task.FromResult<IReadOnlyList<PlanningDiagnostic>>([]);
    }
}
