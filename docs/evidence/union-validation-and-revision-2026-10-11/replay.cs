// Read-only reproduction; arguments: authorized workspace, disposable output directory.
// Reference the corrected test build dependencies; no provider or MCP client is created.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
Directory.CreateDirectory(args[1]);
var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, args[0]);
var record = await records.GetAsync("planning-schema-portability", "benchmark", "schema-portability-20261002:run:sequential20261011a-amazon-1", "GnOuGo.Planning.Benchmark") ?? throw new Exception("Missing retained run");
var saved = JsonNode.Parse(record.Value)!;
var state = saved["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
var cap = state.Catalog!.Capabilities.Single(c => c.Method == "document_write");


Console.WriteLine("DIAGNOSTICS " + saved["result"]!["diagnostics"]!.ToJsonString());
Console.WriteLine("provider_calls=0 mcp_calls=0 saved_session_modified=false");
var asm = typeof(WorkflowPlanSemanticValidator).Assembly;
var validator = asm.GetType("GnOuGo.Flow.Core.Runtime.StepExpressionTypeValidator")!.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "ValidateInput" && m.GetParameters().Length == 5);
var instanceValidator = asm.GetType("GnOuGo.Flow.Core.Runtime.JsonSchemaInstanceValidator")!.GetMethod("ValidateInstance", BindingFlags.Public | BindingFlags.Static)!;
var sourceTypes = new Dictionary<string, JsonNode?> { ["source"] = JsonNode.Parse("""{"type":"object","required":["rows"],"properties":{"rows":{"type":"array","items":{"type":"array","items":{"type":["string","null"]}}}}}""") };
var probe = new JsonObject { ["filePath"] = "output/fixture.xlsx", ["rows"] = "${data.steps.source.rows}" };
var literal = JsonNode.Parse("""{"filePath":"output/fixture.xlsx","rows":[["exact",null]]}""")!;
JsonNode Check(JsonNode input, JsonObject schema) => JsonSerializer.SerializeToNode(validator.Invoke(null, [input, schema, null, sourceTypes, null]))!;
var reversed = (JsonObject)cap.InputSchema.DeepClone();
reversed["oneOf"] = new JsonArray(reversed["oneOf"]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray());
var generic = JsonNode.Parse(cap.InputSchema.ToJsonString().Replace("\"rows\"", "\"entries\"").Replace("\"content\"", "\"payload\""))!.AsObject();
var genericInput = new JsonObject { ["filePath"] = "output/fixture.xlsx", ["entries"] = "${data.steps.source.rows}" };
var report = new JsonObject {
 ["frozen_source"] = "1cc0fe8aba01cc0eb93e35d4e3de4ff2242581a2", ["run"] = "sequential20261011a-amazon-1", ["provider_calls"] = 0, ["mcp_calls"] = 0,
 ["expression_rows"] = Check(probe, cap.InputSchema), ["literal_rows"] = Check(literal, cap.InputSchema),
 ["reversed_branches_expression_rows"] = Check(probe, reversed), ["generic_names_expression_rows"] = Check(genericInput, generic),
 ["resolved_rows_schema_errors"] = JsonSerializer.SerializeToNode(instanceValidator.Invoke(null,[literal,cap.InputSchema])),
 ["both_content_and_rows_errors"] = JsonSerializer.SerializeToNode(instanceValidator.Invoke(null,[JsonNode.Parse("""{"filePath":"output/fixture.xlsx","content":"x","rows":[["exact"]]}"""),cap.InputSchema]))
};
var method = typeof(GnOuGo.Flow.Planning.TaskPlanCompiler).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == "Compile" && m.GetParameters().Length == 3 && m.GetParameters()[2].ParameterType == typeof(PlanningRequest));
var compiled = (GnOuGo.Flow.Planning.TaskCompilation)method.Invoke(new GnOuGo.Flow.Planning.TaskPlanCompiler(),[state.Plan!,state.Catalog!,state.Request])!;
report["task_compilation_diagnostics"] = JsonSerializer.SerializeToNode(compiled.Diagnostics);
if(compiled.Graph is null) throw new Exception("No graph");
try {
 var compile = typeof(GnOuGo.Flow.Planning.PlanningGraphCompiler).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == "Compile" && m.GetParameters().Length == 10);
 var yaml = (string)compile.Invoke(new GnOuGo.Flow.Planning.PlanningGraphCompiler(),[compiled.Graph,state.Catalog!,state.Request.Name,true,true,true,true,true,true,true])!;
 await File.WriteAllTextAsync(Path.Combine(args[1],"retained-v9.yaml"),yaml);
 WorkflowPlanSemanticValidator.Validate(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(yaml), state.Catalog!.Capabilities.Where(c => c.Server is not null && c.Method is not null).Select(c => new McpToolOutputContract(c.Server!,c.Method!,c.InputSchema,c.OutputSchema,c.ExampleResponse)).ToArray());
 report["recompiled_error"] = "success";
} catch(TargetInvocationException ex) { report["recompiled_error"] = ex.InnerException!.Message; }
catch(WorkflowSemanticValidationException ex) { report["recompiled_diagnostics"] = JsonSerializer.SerializeToNode(ex.Errors.Select(e => new { e.Code,e.Field,e.Message })); }
// Disposable explicit revision using the same already-tested deterministic adapter.
var testAssembly = Assembly.Load("GnOuGo.Agent.Server.Tests");
var fixture = testAssembly.GetType("GnOuGo.Agent.Server.Tests.LocalProductOutcomeExecutionTests")!;
var corrected = (PlanningProposal)fixture.GetMethod("UnionRevisionProposal", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [state.Catalog])!;
var frozen = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
var copy = JsonSerializer.Deserialize(frozen, PlanningJsonContext.Default.PlanningSession)!;
var actual = new GnOuGo.Flow.Planning.WorkflowPlanningRuntime(new WorkflowEngine(), (_, _) => Task.CompletedTask);
var adapterType = fixture.GetNestedType("ProposalRuntime", BindingFlags.NonPublic)!;
var adapter = (IPlanningRuntime)Activator.CreateInstance(adapterType, actual, corrected)!;
var planner = new GnOuGo.Flow.Planning.HybridWorkflowPlanner();
copy = await planner.AdvanceAsync(copy, new() { Kind = "revise", PreserveRequirements = true,
    ExpectedRevision = copy.Revision, ArtifactHash = copy.ComputeArtifactHash(),
    Text = "Revise only data adaptation and remove unrequested actions. Preserve accepted requirements, navigation, individual visits, exact descriptions, workbook writing and cleanup. Use complete pages, compact candidate IDs, offered/original lookup, deterministic rows/status." }, adapter, CancellationToken.None);
if(copy.ModelCalls != state.ModelCalls || copy.Request.Baseline is null) throw new Exception("Revision reset baseline/accounting");
copy = await planner.AdvanceAsync(copy, new() { ExpectedRevision = copy.Revision }, adapter, CancellationToken.None);
report["disposable_revision"] = new JsonObject { ["status"] = copy.Status.ToString(), ["model_calls_before"] = state.ModelCalls,
    ["model_calls_after"] = copy.ModelCalls, ["repairs_before"] = state.ReplanAttempts, ["repairs_after"] = copy.ReplanAttempts,
    ["diagnostics"] = JsonSerializer.SerializeToNode(copy.Diagnostics), ["approval"] = copy.ApprovedHash,
    ["historical_session_unchanged"] = frozen == JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession),
    ["requirements_unchanged"] = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements) == JsonSerializer.Serialize(copy.Requirements, PlanningJsonContext.Default.PlanningRequirements) };
await File.WriteAllTextAsync(Path.Combine(args[1],"corrected-proposal.json"),JsonSerializer.Serialize(corrected, new JsonSerializerOptions(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
if (copy.Yaml is not null) await File.WriteAllTextAsync(Path.Combine(args[1],"corrected-v9.yaml"),copy.Yaml);
if(copy.Status != PlanningStatus.FinalReview || copy.ModelCalls != state.ModelCalls+1) throw new Exception(report["disposable_revision"]!.ToJsonString());
var after = await records.GetAsync("planning-schema-portability", "benchmark", "schema-portability-20261002:run:sequential20261011a-amazon-1", "GnOuGo.Planning.Benchmark");
report["saved_record_unchanged"] = record.Value == after!.Value;
var directory = args[1];
Directory.CreateDirectory(directory);
var requestChecks = new JsonArray();
foreach(var issued in (await records.ListAsync("planning-evaluation-requests", "benchmark", "GnOuGo.Planning.Benchmark")).Where(r => r.Key.StartsWith("schema-portability-20261002:sequential20261011a-amazon-1:",StringComparison.Ordinal)).OrderBy(r => r.UpdatedAt)) {
 var request = issued.Value;
 var receipt = await records.GetAsync("planning-evaluation-receipts","benchmark",issued.Key,"GnOuGo.Planning.Benchmark") ?? throw new Exception("Missing receipt");
 var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request)));
 var receiptHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(receipt.Value)));
 requestChecks.Add(new JsonObject { ["request_id"] = issued.Key, ["original_request_sha256"] = requestHash, ["committed_receipt_sha256"] = receiptHash, ["each_guidance_present"] = request.Contains("each.input=bound input name",StringComparison.Ordinal), ["verbatim_guidance_present"] = request.Contains("Verbatim:choose block IDs, lookup text",StringComparison.Ordinal), ["unrequested_work_guidance_present"] = request.Contains("No unrequested work/filter/dedup/invention",StringComparison.Ordinal), ["has_committed_receipt"] = await records.GetAsync("planning-evaluation-receipts","benchmark",issued.Key,"GnOuGo.Planning.Benchmark") is not null });
}
report["issued_request_checks"] = requestChecks;
report["validator_assembly_sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(typeof(WorkflowPlanSemanticValidator).Assembly.Location)));
await File.WriteAllTextAsync(Path.Combine(directory,"offline-rows-reproduction.json"),report.ToJsonString(new(){WriteIndented=true}));
await File.WriteAllTextAsync(Path.Combine(directory,"planning-r2-plan.json"),JsonSerializer.Serialize(state.Plan, new JsonSerializerOptions(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
await File.WriteAllTextAsync(Path.Combine(directory,"planning-r2-requirements.json"),saved["session"]!["requirements"]!.ToJsonString(new(){WriteIndented=true}));
await File.WriteAllTextAsync(Path.Combine(directory,"planning-result.json"),saved["result"]!.ToJsonString(new(){WriteIndented=true}));
await File.WriteAllTextAsync(Path.Combine(directory,"cohort.json"),saved["manifest"]!.ToJsonString(new(){WriteIndented=true}));
Console.WriteLine(report.ToJsonString(new(){WriteIndented=true}));

if (report["expression_rows"]!.AsArray().Count != 0 || report["literal_rows"]!.AsArray().Count != 0 ||
    report["reversed_branches_expression_rows"]!.AsArray().Count != 0 || report["generic_names_expression_rows"]!.AsArray().Count != 0 ||
    report["resolved_rows_schema_errors"]!.AsArray().Count != 0 || report["both_content_and_rows_errors"]!.AsArray().Count != 1 ||
    report["recompiled_error"]?.ToString() != "success" || report["saved_record_unchanged"]!.GetValue<bool>() != true)
    throw new Exception("The retained replay failed; inspect without dispatch.");
