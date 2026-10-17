using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Planning;
using GnOuGo.Agent.Server.Tests;
using GnOuGo.KeyVault.Core.Services;
var store=KeyVaultRecordStoreFactory.CreateWorkspaceStore(null,args[0]);
var record=await store.GetAsync("planning-schema-portability","benchmark","schema-portability-20261002:run:sequential20261011a-amazon-1","GnOuGo.Planning.Benchmark");
var state=JsonNode.Parse(record!.Value)!["session"]!.Deserialize(PlanningJsonContext.Default.PlanningSession)!;
state.Catalog!.Policy.RequireExternalConfirmation=false;
var fixture=new LocalProductOutcomeExecutionTests(new Quiet());
var proposal=(PlanningProposal)typeof(LocalProductOutcomeExecutionTests).GetMethod("CompactUnionProposal",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(fixture,[state.Catalog])!;
var compileTask=typeof(TaskPlanCompiler).GetMethods(BindingFlags.NonPublic|BindingFlags.Instance).Single(m=>m.Name=="Compile"&&m.GetParameters().Length==3&&m.GetParameters()[2].ParameterType==typeof(PlanningRequest));
var lower=typeof(PlanningGraphCompiler).GetMethods(BindingFlags.NonPublic|BindingFlags.Instance).Single(m=>m.Name=="Compile"&&m.GetParameters().Length==11);
var measurements=new JsonArray();
foreach(var profile in new[]{"compact-bindings-v9","compact-bindings-v10"}) {
 var compiled=(TaskCompilation)compileTask.Invoke(new TaskPlanCompiler(),[proposal.Plan!,state.Catalog,new PlanningRequest{Options=new(){["compilation_profile"]=profile}}])!;
 if(compiled.Diagnostics.Count!=0)throw new Exception(string.Join(";",compiled.Diagnostics));
 var yaml=(string)lower.Invoke(new PlanningGraphCompiler(),[compiled.Graph!,state.Catalog,"generated",true,true,true,true,true,true,true,profile.EndsWith("v10")])!;
 measurements.Add(new JsonObject{["profile"]=profile,["yaml_bytes"]=System.Text.Encoding.UTF8.GetByteCount(yaml),["max_line"]=yaml.Split('\n').Max(l=>l.Length),["select_calls"]=Regex.Matches(yaml,@"m\.select\(").Count,["lookup_calls"]=Regex.Matches(yaml,@"m\.lookup\(").Count,["checked_mapping_calls"]=Regex.Matches(yaml,@"checkedMapping\(").Count,["map_calls"]=Regex.Matches(yaml,@"\.map\(").Count,["flat_map_calls"]=Regex.Matches(yaml,@"\.flatMap\(").Count});
}
Console.WriteLine(measurements.ToJsonString());
sealed class Quiet:Xunit.ITestOutputHelper {
 public string Output=>"";
 public void Write(string message){}
 public void Write(string format,params object[] args){}
 public void WriteLine(string message){}
 public void WriteLine(string format,params object[] args){}
}
