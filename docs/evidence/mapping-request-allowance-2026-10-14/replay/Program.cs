using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Flow.Persistence;
using System.Text.Json;
using GnOuGo.Flow.Core.Runtime;
using System.Security.Cryptography;
using System.Text;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
if (args.Length != 3) throw new ArgumentException("Usage: <workspace> <run-id> <report-path>");
var workspace = args[0];
const string tenant = "default";
var records = KeyVaultRecordStoreFactory.CreateWorkspaceStore(null, workspace);
var selected = await records.GetAsync(EncryptedWorkflowRunStore.Collection, tenant, args[1], "GnOuGo.Flow.Persistence") ?? throw new Exception("Missing retained journal head");
var journal = await EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory: workspace).ReadAsync(tenant, selected.Key) ?? throw new Exception("Missing run");
var mapping = journal.Invocations.Values.Single(i => i.Id.EndsWith("/step/n_45947512fc71c0d2", StringComparison.Ordinal));
Console.WriteLine(new JsonObject
{
 ["run"] = journal.RunId, ["status"] = journal.Status.ToString(), ["revision"] = journal.Revision,
 ["max_mapping_input"] = journal.Limits.MaxMappingInputTokens,
 ["head_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selected.Value))),
 ["invocations"] = new JsonArray(journal.Invocations.Values.Select(i => (JsonNode)new JsonObject {
   ["step"] = i.Id, ["type"] = i.StepType, ["status"] = i.Status, ["dispatched"] = i.DispatchedAt,
   ["completed"] = i.CompletedAt, ["error"] = i.Error?.Code }).ToArray()),
 ["pages"] = (mapping.ResolvedInput["sources"]?["pages"] as JsonArray)?.Count,
 ["first_page_keys"] = new JsonArray(mapping.ResolvedInput["sources"]!["pages"]![0]!.AsObject().Select(p => (JsonNode)JsonValue.Create(p.Key)).ToArray()),
 ["first_record_keys"] = new JsonArray(mapping.ResolvedInput["sources"]!["pages"]![0]!["records"]![0]!.AsObject().Select(p => (JsonNode)JsonValue.Create(p.Key)).ToArray())
}.ToJsonString());
var sourceDocument = WorkflowParser.Parse(journal.WorkflowYaml!);
var originalStep = sourceDocument.Workflows["main"].Steps.Single(s => s.Id == "n_45947512fc71c0d2");
var measurements = new JsonArray();
foreach (var limit in new[] { 12000, 96000 })
{
 var model = new OfflineModel(null);
 var result = await Replay(limit, originalStep.OutputSchema!.DeepClone().AsObject(), model);
 measurements.Add(new JsonObject { ["kind"]="original_contract_admission", ["host_limit"]=limit,
   ["offline_adapter_calls"]=model.Requests.Count, ["admission_passed"]=model.Requests.Count > 0, ["error"]=result.Error?.Code, ["details"]=result.Error?.Details?.DeepClone(),
   ["serialized_request_bound"] = model.Requests.Count == 0 ? null : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(model.Requests[0], PlanningJsonContext.Default.LLMRequest)) + 4096 });
}
// Separate coverage fixture: exact original inputs, declared reference-only target.
// This proves complete processing; it is not a product-selection or business-success oracle.
var coverageModel = new OfflineModel("item.records.map(r=>r.reference)");
var coverageSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"object","properties":{"candidates":{"type":"array","items":{"type":"array","items":{"type":"string"}}}},"required":["candidates"],"additionalProperties":false}},"required":["value"],"additionalProperties":false}""")!.AsObject();
var coverage = await Replay(96000, coverageSchema, coverageModel);
var expected = new JsonArray(mapping.ResolvedInput["sources"]!["pages"]!.AsArray().Select(p => (JsonNode)new JsonArray(p!["records"]!.AsArray().Select(r=>r!["reference"]!.DeepClone()).ToArray())).ToArray());
var actual = coverage.StepResults.Single(s=>s.StepId=="mapping").Output?["value"]?["candidates"];
measurements.Add(new JsonObject{["kind"]="separate_reference_coverage_fixture",["success"]=coverage.Success,["exact_all_references"]=JsonNode.DeepEquals(expected,actual),
 ["pages"]=expected.Count,["records"]=expected.Sum(p=>p!.AsArray().Count),["offline_adapter_calls"]=coverageModel.Requests.Count,["error"]=coverage.Error?.Message});
var after = await records.GetAsync(EncryptedWorkflowRunStore.Collection, tenant, selected.Key, "GnOuGo.Flow.Persistence");
var report = new JsonObject{["run"]=journal.RunId,["journal_head_unchanged"]=selected.Value==after!.Value,["provider_calls"]=0,["mcp_calls"]=0,["measurements"]=measurements};
File.WriteAllText(args[2],report.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(report.ToJsonString());

async Task<RunResult> Replay(int limit, JsonObject schema, OfflineModel model)
{
 var stepInput=mapping.ResolvedInput!.DeepClone().AsObject(); stepInput["sources"]="${data.inputs.sources}";
 var doc=new WorkflowDocument{Version=1,Workflows=new(){["main"]=new WorkflowDef{Steps=[new StepDef{Id="mapping",Type="mapping.dynamic",Input=stepInput,OutputSchema=schema}]}}};
 var engine=new WorkflowEngine{LLMClient=model,LlmDefaults=new(){Model="offline"},LLMUsageBudget=new(new(){MaxCalls=1}),Limits=new(){TenantId="offline",MaxMappingInputTokens=limit}};
 return await engine.ExecuteAsync(new WorkflowCompiler().Compile(doc).Workflows["main"],new JsonObject{["sources"]=mapping.ResolvedInput["sources"]!.DeepClone()},CancellationToken.None);
}

sealed class OfflineModel(string? script): ILLMClient, ILLMCapabilityResolver
{
 public List<LLMRequest> Requests {get;}=[];
 public Task<LLMResponse> CallAsync(LLMRequest r,CancellationToken ct){Requests.Add(r);if(script is null)throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException("OFFLINE_ADMISSION_CAPTURE", "Intentional stop after request admission; no provider dispatch.");return Task.FromResult(new LLMResponse{Json=new JsonObject{["script"]=script},Usage=new JsonObject{["input_tokens"]=20,["output_tokens"]=10,["total_tokens"]=30}});}
 public Task<int?> InputTokenAllowanceAsync(string? provider,string model,int outputTokens,CancellationToken ct)=>Task.FromResult<int?>(96000);
 public Task<bool?> SupportsStructuredOutputAsync(string? provider,string model,CancellationToken ct)=>Task.FromResult<bool?>(true);
 public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider,string model,CancellationToken ct)=>Task.FromResult<IReadOnlyList<string>?>([]);
}
