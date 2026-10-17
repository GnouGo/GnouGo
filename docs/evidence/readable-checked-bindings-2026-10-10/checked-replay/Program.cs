using System.Diagnostics;
using System.Text.Json.Nodes;
using Acornima.Ast;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Persistence;
var retained=await EncryptedWorkflowRunStore.CreateWorkspace(baseDirectory:args[0]).ReadAsync("benchmark","nullfacts20261010a-amazon-1")??throw new Exception("Missing retained journal");
IEnumerable<StepDef> Steps(IEnumerable<StepDef> steps)=>steps.SelectMany(s=>new[]{s}.Concat(Steps(s.Steps??[])).Concat(Steps(s.Default??[])).Concat(s.Branches?.SelectMany(b=>Steps(b.Steps))??[]).Concat(s.Cases?.SelectMany(c=>Steps(c.Steps))??[]));
var oldDoc=WorkflowParser.Parse(await File.ReadAllTextAsync(args[1]));var newDoc=WorkflowParser.Parse(await File.ReadAllTextAsync(args[2]));
var oldSteps=oldDoc.Workflows.Values.SelectMany(w=>Steps(w.Steps.Concat(w.Finally))).ToDictionary(s=>s.Id);
var newSteps=newDoc.Workflows.Values.SelectMany(w=>Steps(w.Steps.Concat(w.Finally))).ToDictionary(s=>s.Id);
var aliases=new Dictionary<string,Dictionary<string,string>>();
foreach(var step in newSteps.Values.Where(s=>s.ExpressionContracts?[""] is JsonObject{Count:>0})){
 var old=oldSteps[step.Id];var expr=ExpressionSegments.Read(old.Input!.GetValue<string>()).Single().Expression;
 var call=(CallExpression)new Acornima.Parser().ParseExpression(expr);var contracts=JsonNode.Parse(expr[call.Arguments[2].Start..call.Arguments[2].End])!.AsObject();
 var mapping=new Dictionary<string,string>();foreach(var contract in contracts)mapping[contract.Key]=step.ExpressionContracts![""]!.AsObject().Single(p=>p.Value!["origin"]!.ToString()==contract.Value!["origin"]!.ToString()).Key;
 aliases.Add(step.Id,mapping);
}
void Rename(JsonObject state){foreach(var(pairId,mapping)in aliases)if(state[pairId] is JsonObject record) RenameRecord(record,mapping);}
void RenameRecord(JsonObject record,Dictionary<string,string> mapping){foreach(var(from,to)in mapping)if(record.Remove(from,out var value))record[to]=value;}
var measurements=new JsonArray();var all=true;
foreach(var invocation in retained.Invocations.Values){
 var id=invocation.Id.Split('/').Last();if(!newSteps.TryGetValue(id,out var current)||current.ExpressionContracts is null||invocation.DataBefore is null)continue;
 var before=invocation.DataBefore!.DeepClone();var adjusted=before.DeepClone();if(adjusted["steps"] is JsonObject state)Rename(state);
 JsonNode? Evaluate(StepDef step,JsonNode context,out WorkflowRuntimeException? error,out long allocated,out double milliseconds){var evaluator=new StringInterpolator(new());var start=GC.GetTotalAllocatedBytes(true);var watch=Stopwatch.StartNew();try{error=null;return evaluator.ResolveDeep(step.Input,context,step.ExpressionContracts);}catch(WorkflowRuntimeException e){error=e;return null;}finally{allocated=GC.GetTotalAllocatedBytes(true)-start;milliseconds=watch.Elapsed.TotalMilliseconds;}}
 var original=Evaluate(oldSteps[id],before,out var oldError,out var oldBytes,out var oldTime);
 var improved=Evaluate(current,adjusted,out var newError,out var newBytes,out var newTime);
 var oldWarm=Evaluate(oldSteps[id],before,out var oldWarmError,out var oldWarmBytes,out var oldWarmTime);
 var newWarm=Evaluate(current,adjusted,out var newWarmError,out var newWarmBytes,out var newWarmTime);
 if(oldWarm is JsonObject warmObject&&aliases.TryGetValue(id,out var warmNames))RenameRecord(warmObject,warmNames);
 if(oldWarmError is not null||newWarmError is not null||!JsonNode.DeepEquals(oldWarm,newWarm))throw new Exception("Warm replay differs");
 if(original is JsonObject obj&&aliases.TryGetValue(id,out var names))RenameRecord(obj,names);
 var equal=oldError is null&&newError is null?JsonNode.DeepEquals(original,improved):oldError?.Code==newError?.Code&&oldError?.Message==newError?.Message;
 all&=equal;
 var counters = new JsonObject();
 foreach (var (pointer, contract) in current.ExpressionContracts) {
   JsonNode? input = current.Input;
   if (pointer.Length != 0) foreach (var escaped in pointer[1..].Split('/')) {
     var key=escaped.Replace("~1","/").Replace("~0","~");
     input=input is JsonArray array?array[int.Parse(key)]:input![key];
   }
   var allowance=new GnOuGo.Flow.Core.Scripting.JintSandbox.MappingAllowance(100000,TimeSpan.FromSeconds(15),50000000);
   var measured=new ExpressionEvaluator().EvaluateCheckedExpression(ExpressionEvaluator.ContractExpression(input),adjusted,contract!.AsObject(),allowance);
   counters[pointer]=allowance.Snapshot();
 }

 measurements.Add(new JsonObject{["invocation"]=invocation.Id,["equivalent"]=equal,["v8_sandbox"]=counters,["old_error"]=oldError?.Code+": "+oldError?.Message,["new_error"]=newError?.Code+": "+newError?.Message,["old_allocated_bytes"]=oldBytes,["new_allocated_bytes"]=newBytes,["old_ms"]=oldTime,["new_ms"]=newTime,["old_warm_allocated_bytes"]=oldWarmBytes,["new_warm_allocated_bytes"]=newWarmBytes,["old_warm_ms"]=oldWarmTime,["new_warm_ms"]=newWarmTime});
}
Console.WriteLine(new JsonObject{["provider_calls"]=0,["mcp_calls"]=0,["historical_run_resumed"]=false,["storage_writes"]=0,["all_equivalent"]=all,["measurements"]=measurements}.ToJsonString(new(){WriteIndented=true}));
if(!all)Environment.ExitCode=1;
