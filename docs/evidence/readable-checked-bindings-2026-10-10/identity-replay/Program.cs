using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Scripting;
using GnOuGo.Flow.Persistence;
var journal=await EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory:args[0]).ReadAsync("benchmark","nullfacts20261010a-amazon-1")??throw new Exception("Missing retained journal");
var invocation=journal.Invocations["/workflow/main/step/n_9c5fd213b408a23c"];
var original=journal.Invocations[invocation.Id+"/mapping/2"].Output!["json"]!["script"]!.GetValue<string>();
var input=invocation.ResolvedInput!.DeepClone();var sources=input["sources"]!.AsObject();var each=input["each"]!;
var pages=sources[each["input"]!.GetValue<string>()]!.AsArray();
var doc=WorkflowParser.Parse(await File.ReadAllTextAsync(args[1]));
var target=doc.Workflows.Values.SelectMany(w=>w.Steps).Single(s=>s.Id=="n_9c5fd213b408a23c").OutputSchema!["properties"]!["value"]!["properties"]![each["output"]!.GetValue<string>()]!["items"]!.AsObject();
var fixedScript=original.Replace("m.text(r.reference,\":(record:.+)$\")","r.reference",StringComparison.Ordinal);
if(fixedScript==original)throw new Exception("Unexpected original identity expression: "+original);
var before=Hash(input.ToJsonString());var measurements=new JsonArray();
foreach(var script in new[]{original,fixedScript}){
 var start=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();var sandbox=new JintSandbox();
 var extracted=sandbox.ExecuteMappingItems(script,sources,each["input"]!.GetValue<string>(),target);watch.Stop();
 var offered=new JsonArray(extracted.SelectMany(p=>p!.AsArray()).Select(p=>p!.DeepClone()).ToArray());
 var ids=new JsonArray(offered[1]!["key"]!.DeepClone());
 sandbox.ExecuteMapping("m.lookup(source.rows,source.ids,'key')",new JsonObject{["rows"]=offered.DeepClone(),["ids"]=ids.DeepClone()});
 var records=new JsonArray(pages.SelectMany(p=>p!["records"]!.AsArray()).Select(p=>p!.DeepClone()).ToArray());
 bool reconnected;string? error=null;
 try{var result=sandbox.ExecuteMapping("m.lookup(source.rows,source.ids,'reference')",new JsonObject{["rows"]=records.DeepClone(),["ids"]=ids.DeepClone()});reconnected=JsonNode.DeepEquals(result![0],records.Single(r=>r!["reference"]!.GetValue<string>()==ids[0]!.GetValue<string>()));}
 catch(WorkflowRuntimeException ex){reconnected=false;error=ex.Code+": "+ex.Message;}
 if(reconnected!=(script==fixedScript))throw new Exception("Incorrect lookup outcome");
 measurements.Add(new JsonObject{["phase"]=script==original?"original":"verbatim_identity_only",["script_sha256"]=Hash(script),["reconnected"]=reconnected,["error"]=error,["pages"]=extracted.Count,["candidates"]=offered.Count,["elapsed_ms"]=watch.Elapsed.TotalMilliseconds,["allocated_bytes"]=GC.GetTotalAllocatedBytes(true)-start});
}
if(before!=Hash(input.ToJsonString()))throw new Exception("Observed values changed");
Console.WriteLine(new JsonObject{["provider_calls"]=0,["mcp_calls"]=0,["historical_run_resumed"]=false,["storage_writes"]=0,["input_sha256"]=before,["source_pages"]=pages.Count,["source_records"]=pages.Sum(p=>p!["records"]!.AsArray().Count),["original_script"]=original,["corrected_script"]=fixedScript,["measurements"]=measurements}.ToJsonString(new(){WriteIndented=true}));
static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
