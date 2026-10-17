// Read-only reproduction; arguments: authorized workspace, disposable output directory.
// Reference the frozen benchmark build dependencies; no model or MCP client is created.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Models;
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
 ["source"] = "1cc0fe8aba01cc0eb93e35d4e3de4ff2242581a2", ["run"] = "sequential20261011a-amazon-1", ["provider_calls"] = 0, ["mcp_calls"] = 0,
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
 WorkflowPlanSemanticValidator.Validate(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(yaml), state.Catalog!.Capabilities.Where(c => c.Server is not null && c.Method is not null).Select(c => new McpToolOutputContract(c.Server!,c.Method!,c.InputSchema,c.OutputSchema,c.ExampleResponse)).ToArray());
 report["recompiled_error"] = "unexpected success";
} catch(TargetInvocationException ex) { report["recompiled_error"] = ex.InnerException!.Message; }
catch(WorkflowSemanticValidationException ex) { report["recompiled_diagnostics"] = JsonSerializer.SerializeToNode(ex.Errors.Select(e => new { e.Code,e.Field,e.Message })); }
var after = await records.GetAsync("planning-schema-portability", "benchmark", "schema-portability-20261002:run:sequential20261011a-amazon-1", "GnOuGo.Planning.Benchmark");
report["saved_record_unchanged"] = record.Value == after!.Value;
var directory = args[1];
Directory.CreateDirectory(directory);
var requestChecks = new JsonArray();
foreach(var issued in (await records.ListAsync("planning-evaluation-requests", "benchmark", "GnOuGo.Planning.Benchmark")).Where(r => r.Key.StartsWith("schema-portability-20261002:sequential20261011a-amazon-1:",StringComparison.Ordinal)).OrderBy(r => r.UpdatedAt)) {
 var request = issued.Value;
 requestChecks.Add(new JsonObject { ["request_id"] = issued.Key, ["each_guidance_present"] = request.Contains("each.input=bound input name",StringComparison.Ordinal), ["verbatim_guidance_present"] = request.Contains("Verbatim:choose block IDs, lookup text",StringComparison.Ordinal), ["unrequested_work_guidance_present"] = request.Contains("No unrequested work/filter/dedup/invention",StringComparison.Ordinal), ["has_committed_receipt"] = await records.GetAsync("planning-evaluation-receipts","benchmark",issued.Key,"GnOuGo.Planning.Benchmark") is not null });
}
report["issued_request_checks"] = requestChecks;
await File.WriteAllTextAsync(Path.Combine(directory,"offline-rows-reproduction.json"),report.ToJsonString(new(){WriteIndented=true}));
await File.WriteAllTextAsync(Path.Combine(directory,"planning-r2-plan.json"),JsonSerializer.Serialize(state.Plan, new JsonSerializerOptions(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
await File.WriteAllTextAsync(Path.Combine(directory,"planning-r2-requirements.json"),saved["session"]!["requirements"]!.ToJsonString(new(){WriteIndented=true}));
await File.WriteAllTextAsync(Path.Combine(directory,"planning-result.json"),saved["result"]!.ToJsonString(new(){WriteIndented=true}));
await File.WriteAllTextAsync(Path.Combine(directory,"cohort.json"),saved["manifest"]!.ToJsonString(new(){WriteIndented=true}));
Console.WriteLine(report.ToJsonString(new(){WriteIndented=true}));

if (report["expression_rows"]!.AsArray().Count != 1 || report["literal_rows"]!.AsArray().Count != 0 ||
    report["reversed_branches_expression_rows"]!.AsArray().Count != 0 || report["generic_names_expression_rows"]!.AsArray().Count != 1 ||
    report["resolved_rows_schema_errors"]!.AsArray().Count != 0 || report["both_content_and_rows_errors"]!.AsArray().Count != 1 ||
    report["recompiled_diagnostics"]!.AsArray().Count != 1 || report["saved_record_unchanged"]!.GetValue<bool>() != true)
    throw new Exception("The retained reproduction changed; inspect without dispatch.");
