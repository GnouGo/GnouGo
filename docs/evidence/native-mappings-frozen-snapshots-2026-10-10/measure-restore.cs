using System.Diagnostics;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Persistence;
using GnOuGo.KeyVault.Core.Services;
var root=Directory.CreateTempSubdirectory("gnougo-v7-restore-").FullName;
EncryptedWorkflowRunStore Store()=>new(new KeyVaultRecordStore(Path.Combine(root,"vault.db")),Path.Combine(root,"index.db"),Path.Combine(root,"owners"));
var data=new JsonObject { ["pages"]=new JsonArray(Enumerable.Range(0,52).Select(p=>(JsonNode)new JsonObject {
 ["records"]=new JsonArray(Enumerable.Range(0,p==51?22:29).Select(i=>(JsonNode)new JsonObject {
 ["id"]=p*29+i,["text"]=new string('x',780),["number"]=JsonNode.Parse("1.2300"),["missing"]=null }).ToArray())}).ToArray())};
var state=new JsonObject { ["steps"]=new JsonObject { ["observed"]=data } };
for(var i=0;i<3;i++) state["steps"]!["branch"+i]=state["steps"]!.DeepClone();
await Store().CreateAsync(new() { TenantId="measure",RunId="fresh",Limits=new() { TenantId="measure",RunId="fresh" } });
long revision;
await using(var owner=await Store().AcquireAsync("measure","fresh",0)) {
 for(var i=0;i<12;i++) { var item=new WorkflowInvocation { Id="step"+i,StepType="set",Status="completed" };owner.Run.Invocations[item.Id]=item;
 await owner.CaptureSnapshotAsync(item,state,false);await owner.CaptureSnapshotAsync(item,state,true);await owner.SaveAsync([item.Id]); }
 revision=owner.Run.Revision;
}
var allocated=GC.GetTotalAllocatedBytes();var timer=Stopwatch.StartNew();
await using(var owner=await Store().AcquireAsync("measure","fresh",revision)) {
 Console.WriteLine($"restart_ms={timer.Elapsed.TotalMilliseconds:F3}; restart_allocated_bytes={GC.GetTotalAllocatedBytes()-allocated}");
 foreach(var item in owner.Run.Invocations.Values) if(item.TryGetMaterializedSnapshot(false,out _))throw new Exception("expanded history");
 var restored=new JsonObject();allocated=GC.GetTotalAllocatedBytes();timer.Restart();
 await owner.RestoreSnapshotAsync(owner.Run.Invocations["step11"],restored,true);
 Console.WriteLine($"cold_restore_ms={timer.Elapsed.TotalMilliseconds:F3}; cold_restore_allocated_bytes={GC.GetTotalAllocatedBytes()-allocated}");
 var observed=restored["steps"]!["observed"];allocated=GC.GetTotalAllocatedBytes();timer.Restart();
 await owner.RestoreSnapshotAsync(owner.Run.Invocations["step11"],restored,true);
 Console.WriteLine($"warm_restore_ms={timer.Elapsed.TotalMilliseconds:F3}; warm_restore_allocated_bytes={GC.GetTotalAllocatedBytes()-allocated}");
 if(!ReferenceEquals(observed,restored["steps"]!["observed"])||!JsonNode.DeepEquals(state,restored))throw new Exception("restore mismatch");
}
Console.WriteLine("PASS exact restore with unmaterialized history; no inference or external execution");
var files=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Select(f=>new FileInfo(f)).ToArray();
Console.WriteLine($"physical_file_bytes={files.Sum(f=>f.Length)}; physical_file_count={files.Length}");
Directory.Delete(root,true);
