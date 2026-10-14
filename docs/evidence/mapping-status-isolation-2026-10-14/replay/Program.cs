using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Flow.Persistence;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Planning;
using GnOuGo.Planning.Examples;

if (args.Length != 4) throw new ArgumentException("Usage: workspace run-id planning-session-id evidence-directory");
var vault = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args[0]);
const string tenant = "default", author = "GnOuGo.Flow.Persistence";
var head = await vault.GetAsync(EncryptedWorkflowRunStore.Collection, tenant, args[1], author) ?? throw new Exception("Missing retained run");
var run = await EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: args[0]).ReadAsync(tenant, args[1]) ?? throw new Exception("Missing run");
var record = (await vault.ListAsync("agent-planning-sessions-v10", tenant, "GnOuGo.Agent.Server.Planning"))
    .Where(r => r.Key.StartsWith(args[2] + ":", StringComparison.Ordinal) &&
        JsonSerializer.Deserialize(r.Value, PlanningJsonContext.Default.PlanningSession)!.Status == PlanningStatus.FinalReview)
    .OrderByDescending(r => r.CreatedAt).First();
var original = JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.PlanningSession)!;
var copy = JsonSerializer.Deserialize(record.Value, PlanningJsonContext.Default.PlanningSession)!;
// The host trims the terminal newline when loading the saved YAML.
if (original.Yaml?.TrimEnd('\r', '\n') != run.WorkflowYaml.TrimEnd('\r', '\n'))
    throw new Exception("The retained review must match the executed artifact apart from terminal line endings");
var mapping = run.Invocations.Values.Single(i => i.StepType == "mapping.dynamic" && i.Id.EndsWith("/n_60b9a17edad8a778", StringComparison.Ordinal));
var document = WorkflowParser.Parse(run.WorkflowYaml);
var target = document.Workflows["w_b759e95806481289"].Steps.Single(s => s.Id == "n_60b9a17edad8a778").OutputSchema!["properties"]!["value"]!.AsObject();
var replays = new JsonArray();
var sources = mapping.ResolvedInput!["sources"]!;
var sourceBytes = sources.ToJsonString();
foreach (var receipt in run.Invocations.Values.Where(i => i.Id.StartsWith(mapping.Id + "/mapping/", StringComparison.Ordinal)).OrderBy(i => i.Id, StringComparer.Ordinal))
{
    if (receipt.CompletedAt is null || receipt.Output?["json"]?["script"] is not {} token) throw new Exception("Committed script required");
    var script = token.GetValue<string>();
    var row = new JsonObject { ["script_sha256"] = Hash(script), ["receipt_sha256"] = Hash(receipt.Output.ToJsonString()) };
    try { new JintSandbox().ExecuteMapping(script, sources, CancellationToken.None, target: target); throw new Exception("Expected retained failure"); }
    catch (WorkflowRuntimeException e) { row["code"] = e.Code; row["message"] = e.Message; row["details"] = e.Details?.DeepClone(); }
    replays.Add(row);
}
// Evidence-only ablation: preserve the first script's complete row expression verbatim.
// This isolates the rejected status; it is not a universal learned script or business oracle.
var first = run.Invocations.Values.Single(i => i.Id == mapping.Id + "/mapping/0").Output!["json"]!["script"]!.GetValue<string>();
var factsOnly = first[..first.IndexOf(",status:", StringComparison.Ordinal)] + "})";
var factsTarget = target.DeepClone().AsObject(); factsTarget["properties"]!.AsObject().Remove("status"); factsTarget["required"] = new JsonArray("row");
var facts = new JintSandbox().ExecuteMapping(factsOnly, sources, CancellationToken.None, target: factsTarget)!;
var expected = new JsonObject();
foreach (var (name, index) in new[] { ("name", 46), ("description", 47), ("price", 50) })
    expected[name] = sources["productPage"]!["pages"]![1]!["records"]![index]!["text"]!.DeepClone();
if (!JsonNode.DeepEquals(expected, facts["row"])) throw new Exception("Observed row changed");
if (sourceBytes != sources.ToJsonString()) throw new Exception("Source mutated");

var plan = JsonSerializer.Deserialize(JsonSerializer.Serialize(copy.Plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
var branch = plan.Root.Tasks.Single(t => t.Id == "visit_products").Body!.Tasks.Single(t => t.Id == "product_url_branch").Body!;
MappingStatusComposition.Separate(branch, "open_product_page", "extract_product_row", "assemble_product_status");
var proposal = new PlanningProposal { Plan = plan, Requirements = copy.Requirements };
var runtime = new OfflineRuntime(copy, proposal); var planner = new HybridWorkflowPlanner();
copy = await planner.AdvanceAsync(copy, new() { Kind = "revise", ExpectedRevision = copy.Revision, ArtifactHash = copy.ComputeArtifactHash(), PreserveRequirements = true,
    Text = "Separate product facts extraction from deterministic producer status in the non-null product URL branch. Preserve all accepted requirements, public contracts, other tasks, operations and arguments. Check snapshot completeness before extraction; preserve exact observed facts and permitted nulls." }, runtime, CancellationToken.None);
for (var i = 0; i < 4 && copy.Status == PlanningStatus.Generating; i++) copy = await planner.AdvanceAsync(copy, new() { ExpectedRevision = copy.Revision }, runtime, CancellationToken.None);
if (copy.Status != PlanningStatus.FinalReview) throw new Exception("Offline revision: " + copy.Status + " " + string.Join(';', copy.Diagnostics));
if (runtime.Calls != 1 || copy.ApprovedHash is not null) throw new Exception("Expected one unapproved offline proposal");
if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(original.Requirements, PlanningJsonContext.Default.PlanningRequirements),
    JsonSerializer.SerializeToNode(copy.Requirements, PlanningJsonContext.Default.PlanningRequirements))) throw new Exception("Requirements changed");
static IEnumerable<PlanTask> Tasks(TaskScope scope)
{
    foreach (var task in scope.Tasks)
    {
        yield return task;
        if (task.Body is not null) foreach (var child in Tasks(task.Body)) yield return child;
        if (task.Otherwise is not null) foreach (var child in Tasks(task.Otherwise)) yield return child;
    }
}
var beforeOperations = Tasks(original.Plan!.Root).Where(t => t.Kind == "operation").Select(t => JsonSerializer.Serialize(t, PlanningJsonContext.Default.PlanTask)).ToArray();
var afterOperations = Tasks(copy.Plan!.Root).Where(t => t.Kind == "operation").Select(t => JsonSerializer.Serialize(t, PlanningJsonContext.Default.PlanTask)).ToArray();
if (!beforeOperations.SequenceEqual(afterOperations)) throw new Exception("Operations or arguments changed");
Directory.CreateDirectory(args[3]);
File.WriteAllText(Path.Combine(args[3], "retained-taskplan.json"), JsonSerializer.Serialize(original.Plan, PlanningJsonContext.Default.TaskPlan));
File.WriteAllText(Path.Combine(args[3], "corrected-taskplan.json"), JsonSerializer.Serialize(copy.Plan, PlanningJsonContext.Default.TaskPlan));
File.WriteAllText(Path.Combine(args[3], "corrected.yaml"), copy.Yaml);
File.WriteAllText(Path.Combine(args[3], "retained.yaml"), run.WorkflowYaml);
var operations = new JsonArray(run.Invocations.Values.Where(i => i.StepType == "mcp.call").OrderBy(i => i.PreparedAt).Select(i => (JsonNode)new JsonObject
{ ["method"] = i.ResolvedInput?["method"]?.DeepClone(), ["status"] = i.Status, ["success"] = i.Output?["response"]?["success"]?.DeepClone() }).ToArray());
var report = new JsonObject { ["run"] = run.RunId, ["revision"] = run.Revision, ["status"] = run.Status, ["run_head_sha256"] = Hash(head.Value),
    ["planning_record_sha256"] = Hash(record.Value), ["historical_yaml_sha256"] = Hash(run.WorkflowYaml), ["review_yaml_sha256"] = Hash(original.Yaml!), ["yaml_diff"] = "terminal_newline_only", ["source_sha256"] = Hash(sourceBytes),
    ["source_pages"] = sources["productPage"]!["pages"]!.AsArray().Count, ["source_records"] = sources["productPage"]!["recordCount"]!.DeepClone(),
    ["operations"] = operations, ["replays"] = replays, ["facts_only_ablation_exact"] = true, ["provider_calls"] = 0, ["mcp_calls"] = 0,
    ["offline_revision_calls"] = runtime.Calls, ["offline_status"] = copy.Status, ["offline_revision"] = copy.Revision, ["approved"] = false,
    ["source_review_revision"] = original.Revision, ["operations_unchanged"] = true, ["requirements_unchanged"] = true,
    ["tasks_before"] = Tasks(original.Plan.Root).Count(), ["tasks_after"] = Tasks(copy.Plan.Root).Count(),
    ["yaml_bytes_before"] = Encoding.UTF8.GetByteCount(original.Yaml!), ["yaml_bytes_after"] = Encoding.UTF8.GetByteCount(copy.Yaml!),
    ["revision_request_utf8_bytes"] = runtime.RequestBytes,
    ["historical_head_unchanged"] = head.Value == (await vault.GetAsync(EncryptedWorkflowRunStore.Collection, tenant, args[1], author))!.Value,
    ["historical_planning_record_unchanged"] = record.Value == (await vault.GetAsync("agent-planning-sessions-v10", tenant, record.Key, "GnOuGo.Agent.Server.Planning"))!.Value };
File.WriteAllText(Path.Combine(args[3], "replay.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(report.ToJsonString());
static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

sealed class OfflineRuntime(PlanningSession retained, PlanningProposal proposal) : IPlanningRuntime
{
    private readonly WorkflowPlanningRuntime actual = new(new(), (_, _) => Task.CompletedTask);
    public int Calls;
    public int RequestBytes;
    public ICapabilityCatalog Capabilities => actual.Capabilities; // Any discovery is unexpected and has no configured transport.
    public Task<PlanningCatalog> DiscoverAsync(PlanningRequest r, CancellationToken ct) => Task.FromResult(retained.Catalog!);
    public Task<LLMResponse> CallAsync(LLMRequest r, string purpose, CancellationToken ct)
    {
        if (++Calls != 1) throw new Exception("Unexpected additional offline proposal/repair");
        RequestBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(r, PlanningJsonContext.Default.LLMRequest));
        var json = PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal), r.StructuredOutputSchema!.AsObject(), r.StructuredOutputSchema.AsObject());
        return Task.FromResult(new LLMResponse { Json = json });
    }
    public Task CheckpointAsync(PlanningSession s, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest r, CancellationToken ct) => actual.ValidateAsync(r, ct);
    public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog, CancellationToken ct)
    {
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(catalog, PlanningJsonContext.Default.PlanningCatalog), JsonSerializer.SerializeToNode(retained.Catalog, PlanningJsonContext.Default.PlanningCatalog))) throw new Exception("Frozen contracts changed");
        return Task.FromResult<IReadOnlyList<PlanningDiagnostic>>([]);
    }
}
