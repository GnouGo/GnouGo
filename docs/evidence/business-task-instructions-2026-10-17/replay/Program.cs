using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using GnOuGo.KeyVault.Core.Services;
using GnOuGo.Flow.Persistence;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;
if (args.Length != 2) throw new ArgumentException("Usage: workspace private-evidence-directory");
var workspace=Path.GetFullPath(args[0]); var dir=Path.GetFullPath(args[1]); Directory.CreateDirectory(dir);
const string id="338d861b810ad788539ca715b0e6cbcd";
var vault=KeyVaultRecordStoreFactory.CreateWorkspaceStore(null,workspace);
var head=await vault.GetAsync(EncryptedWorkflowRunStore.Collection,"default",id,"GnOuGo.Flow.Persistence")??throw new Exception("Missing run");
var run=await EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory:workspace).ReadAsync("default",id)??throw new Exception("Missing run");
var invocations=new JsonArray();int number=0;
foreach(var i in run.Invocations.Values.OrderBy(i=>i.PreparedAt)){
 var row=new JsonObject{["id"]=i.Id,["type"]=i.StepType,["status"]=i.Status,["description"]=i.Description,["dispatched"]=i.DispatchedAt?.ToString(),["completed"]=i.CompletedAt?.ToString(),["error"]=i.Error?.ToString()};
 if(i.StepType=="mcp.call")row["method"]=i.ResolvedInput?["method"]?.DeepClone();
 if(i.StepType=="mapping.dynamic"){
  row["binding"]=i.ResolvedInput?["binding"]?.DeepClone();row["objective"]=i.ResolvedInput?["objective"]?.DeepClone();row["inputKeys"]=string.Join(",",i.ResolvedInput?.AsObject().Select(p=>p.Key)??[]);
  File.WriteAllText(Path.Combine(dir,"mapping-input-private.json"),i.ResolvedInput!.ToJsonString());
 }
 if(i.StepType=="llm.call"&&i.Id.Contains("/mapping/")){
  number++;var request=i.ResolvedInput!.Deserialize(PlanningJsonContext.Default.LLMRequest)!;
  var response=i.Output?["json"]??JsonNode.Parse(i.Output?["text"]?.GetValue<string>()??"null");
  var script=response?["script"]?.GetValue<string>();
  File.WriteAllText(Path.Combine(dir,$"request-{number}-private.json"),i.ResolvedInput!.ToJsonString());
  File.WriteAllText(Path.Combine(dir,$"response-{number}-private.json"),i.Output!.ToJsonString());
  row["strict"]=request.StructuredOutputStrict;row["schemaFindings"]=PlanningContractValidation.ValidateInstanceFindings(response,request.StructuredOutputSchema!).Count;row["script"]=script;
  try{JintSandbox.ValidateMapping(script!);row["preflight"]="passed";}catch(WorkflowRuntimeException e){row["preflight"]=e.Message;row["details"]=e.Details?.DeepClone();}
 }
 invocations.Add(row);
}
File.WriteAllText(Path.Combine(dir,"workflow-private.yaml"),run.WorkflowYaml);
var report=new JsonObject{["run"]=run.RunId,["status"]=run.Status,["workflow"]=run.WorkflowName,["revision"]=run.Revision,["stepsStarted"]=run.StepsStarted,["finalizationStarted"]=run.FinalizationStarted,["finalizationCompleted"]=run.FinalizationCompleted,["invocations"]=invocations,["headHash"]=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(head.Value))),["headUnchanged"]=head.Value==(await vault.GetAsync(EncryptedWorkflowRunStore.Collection,"default",id,"GnOuGo.Flow.Persistence"))!.Value};
File.WriteAllText(Path.Combine(dir,"report-private.json"),report.ToJsonString(new(){WriteIndented=true}));

var sessions=await vault.ListAsync("agent-planning-sessions-v10","default","GnOuGo.Agent.Server.Planning");
foreach(var record in sessions.OrderByDescending(r=>r.UpdatedAt)){
 var state=JsonSerializer.Deserialize(record.Value,PlanningJsonContext.Default.PlanningSession)!;
 if(state.Yaml?.TrimEnd()!=run.WorkflowYaml.TrimEnd())continue;

 File.WriteAllText(Path.Combine(dir,"planning-private.json"),record.Value);
 var requests=await vault.ListAsync("agent-planning-model-requests-v10","default","GnOuGo.Agent.Server.Planning");
 var j=0;foreach(var request in requests.Where(r=>r.Key.StartsWith(state.Request.SessionId+":",StringComparison.Ordinal)).OrderBy(r=>r.CreatedAt)){
  File.WriteAllText(Path.Combine(dir,$"planning-request-{++j}-private.json"),request.Value);
  var response=await vault.GetAsync("agent-planning-model-receipts-v10","default",request.Key,"GnOuGo.Agent.Server.Planning");
  if(response is not null)File.WriteAllText(Path.Combine(dir,$"planning-response-{j}-private.json"),response.Value);
 }
 break;
}

var request2=JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir,"request-2-private.json")),PlanningJsonContext.Default.LLMRequest)!;
var payload=JsonNode.Parse(request2.Prompt[request2.Prompt.IndexOf("{\"objective\"",StringComparison.Ordinal)..])!;
var receipt2=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"response-2-private.json")))!;
var savedScript=receipt2["json"]!["script"]!.GetValue<string>();
var root=(Acornima.Ast.ObjectExpression)new Acornima.Parser().ParseExpression(savedScript);
var fields=new List<string>();
foreach(var prop in root.Properties.Cast<Acornima.Ast.Property>()){
 var name=((Acornima.Ast.Identifier)prop.Key).Name;
 if(name is not ("owner" or "repo" or "pullNumber" or "remoteUrl"))continue;
 var value=name=="remoteUrl"?((Acornima.Ast.BinaryExpression)prop.Value).Left:prop.Value;
 fields.Add(name+":"+savedScript[value.Start..value.End]);
}
var factsScript="({"+string.Join(",",fields)+"})";
var target=payload["target"]!.DeepClone().AsObject();
target["properties"]!.AsObject().Remove("prHeadRefSpec");target["properties"]!.AsObject().Remove("headFetchRef");
target["required"]=new JsonArray("owner","repo","pullNumber","remoteUrl");
var result=new JintSandbox().ExecuteMapping(factsScript,payload["source"],CancellationToken.None,target:target)!;
var uri=new Uri(payload["source"]!["pullRequestUrl"]!.GetValue<string>());var segments=uri.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);
if(result["owner"]!.GetValue<string>()!=segments[0]||result["repo"]!.GetValue<string>()!=segments[1]||result["pullNumber"]!.GetValue<int>()!=int.Parse(segments[3])||result["remoteUrl"]!.GetValue<string>()!=uri.GetLeftPart(UriPartial.Authority)+"/"+segments[0]+"/"+segments[1])throw new Exception("Facts mismatch");
Console.WriteLine("Facts-only ablation: four exact source-derived values validated; no generated Git refs, no suffix, zero provider/MCP calls.");

var retained=JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dir,"planning-private.json")),PlanningJsonContext.Default.PlanningSession)!;
var corrected=JsonSerializer.Deserialize(JsonSerializer.Serialize(retained.Plan,PlanningJsonContext.Default.TaskPlan),PlanningJsonContext.Default.TaskPlan)!;
var parse=corrected.Root.Tasks.Single(t=>t.Id=="parse_pr_url");
parse.ResultType!.Fields.RemoveAll(f=>f.Name is "prHeadRefSpec" or "headFetchRef");
var clone=corrected.Root.Tasks.Single(t=>t.Id=="clone_repository_once");
clone.Inputs.Add(new("branch",new(){Kind="field",Port="headSha",Items=[new(){Kind="output",Source="extract_pr_refs"}]}));
clone.DependsOn.Add("extract_pr_refs");
corrected.Root.Tasks.Single(t=>t.Id=="fetch_pr_head").Inputs.RemoveAll(i=>i.Name=="refSpec");
corrected.Root.Tasks.Single(t=>t.Id=="fetch_pr_head").Objective="Fetch the configured remote using its declared default refspec; clone and checkout receive the producer's exact commit ID.";
var proposal=new PlanningProposal{Plan=corrected,Requirements=retained.Requirements};
// This new, disposable session has its own deterministic adapter. It is neither
// the exhausted saved session nor a reset of that session's accounting.
var copy=new PlanningSession{IntentVersion=retained.IntentVersion,Status=PlanningStatus.FinalReview,
 Plan=retained.Plan,Requirements=retained.Requirements,Catalog=retained.Catalog,Discovery=retained.Discovery,Yaml=retained.Yaml,
 Request=new(){TenantId="offline",SessionId="offline-observed-revision",Prompt=retained.Request.Prompt,Policy=retained.Request.Policy,
  Generation=retained.Request.Generation,Options=new(){["compilation_profile"]="compact-bindings-v15"}}};
var runtime=new OfflineRuntime(retained,proposal);var planner=new HybridWorkflowPlanner();
copy=await planner.AdvanceAsync(copy,new(){Kind="revise",PreserveRequirements=true,ExpectedRevision=copy.Revision,ArtifactHash=copy.ComputeArtifactHash(),
 Text="Explicit offline structural revision: extract only source-derived URL facts; remove constructed prHeadRefSpec and unused headFetchRef. Clone using the observed producer headSha via the existing branch contract, after metadata extraction; keep fetch with its documented default refspec and the exact checkout. Preserve accepted requirements, public interfaces, permissions, all operation identities and their order, all unrelated arguments, guards and cleanup."},runtime,CancellationToken.None);
for(var i=0;i<4&&copy.Status==PlanningStatus.Generating;i++)copy=await planner.AdvanceAsync(copy,new(){ExpectedRevision=copy.Revision},runtime,CancellationToken.None);
if(copy.Status!=PlanningStatus.FinalReview)throw new Exception("Offline revision: "+copy.Status+" "+string.Join(';',copy.Diagnostics));
if(runtime.Calls!=1||copy.ApprovedHash is not null)throw new Exception("Expected one unapproved deterministic proposal");
if(!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(retained.Requirements,PlanningJsonContext.Default.PlanningRequirements),JsonSerializer.SerializeToNode(copy.Requirements,PlanningJsonContext.Default.PlanningRequirements)))throw new Exception("Requirements changed");
File.WriteAllText(Path.Combine(dir,"corrected-taskplan.json"),JsonSerializer.Serialize(copy.Plan,PlanningJsonContext.Default.TaskPlan));
File.WriteAllText(Path.Combine(dir,"corrected.yaml"),copy.Yaml);
File.WriteAllText(Path.Combine(dir,"original-taskplan.json"),JsonSerializer.Serialize(retained.Plan,PlanningJsonContext.Default.TaskPlan));
File.WriteAllText(Path.Combine(dir,"requirements.json"),JsonSerializer.Serialize(retained.Requirements,PlanningJsonContext.Default.PlanningRequirements));
File.WriteAllText(Path.Combine(dir,"facts-script.js"),factsScript);
var summary=new JsonObject{["run"]=id,["planningSession"]=retained.Request.SessionId,["historicalRunStatus"]=run.Status,
 ["historicalHeadHash"]=report["headHash"]!.DeepClone(),["historicalHeadUnchanged"]=head.Value==(await vault.GetAsync(EncryptedWorkflowRunStore.Collection,"default",id,"GnOuGo.Flow.Persistence"))!.Value,
 ["scriptFailures"]=new JsonArray(invocations.Where(i=>i?["preflight"] is not null).Select(i=>(JsonNode)new JsonObject{["scriptSha256"]=PlanningGraphCompiler.Fingerprint(i!["script"]!.ToString()),["strict"]=i["strict"]!.DeepClone(),["schemaFindings"]=i["schemaFindings"]!.DeepClone(),["message"]=i["preflight"]!.DeepClone(),["details"]=i["details"]!.DeepClone()}).ToArray()),
 ["factsOnlyExact"]=true,["providerCalls"]=0,["mcpCalls"]=0,["offlineProposalCalls"]=runtime.Calls,["offlineRequestBytes"]=runtime.RequestBytes,["offlineStatus"]=copy.Status,["approved"]=false};
File.WriteAllText(Path.Combine(dir,"summary.json"),summary.ToJsonString(new(){WriteIndented=true}));Console.WriteLine(summary.ToJsonString());

sealed class OfflineRuntime(PlanningSession retained,PlanningProposal proposal):IPlanningRuntime
{
 readonly WorkflowPlanningRuntime actual=new(new(),(_,_)=>Task.CompletedTask);
 public int Calls,RequestBytes;
 public ICapabilityCatalog Capabilities=>actual.Capabilities;
 public Task<PlanningCatalog> DiscoverAsync(PlanningRequest request,CancellationToken ct)=>Task.FromResult(retained.Catalog!);
 public Task<LLMResponse> CallAsync(LLMRequest request,string purpose,CancellationToken ct){
  if(++Calls!=1)throw new Exception("No additional offline repair allowed");
  RequestBytes=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request,PlanningJsonContext.Default.LLMRequest));
  return Task.FromResult(new LLMResponse{Json=PlanningCorpus.Transport(JsonSerializer.SerializeToNode(proposal,PlanningJsonContext.Default.PlanningProposal),request.StructuredOutputSchema!.AsObject(),request.StructuredOutputSchema.AsObject())});
 }
 public Task CheckpointAsync(PlanningSession state,CancellationToken ct)=>Task.CompletedTask;
 public Task<IReadOnlyList<PlanningDiagnostic>> ValidateAsync(PlanningArtifactValidationRequest request,CancellationToken ct)=>actual.ValidateAsync(request,ct);
 public Task<IReadOnlyList<PlanningDiagnostic>> ValidateCatalogAsync(PlanningCatalog catalog,CancellationToken ct){
  if(!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(catalog,PlanningJsonContext.Default.PlanningCatalog),JsonSerializer.SerializeToNode(retained.Catalog,PlanningJsonContext.Default.PlanningCatalog)))throw new Exception("Frozen contracts changed");
  return Task.FromResult<IReadOnlyList<PlanningDiagnostic>>([]);
 }
}
