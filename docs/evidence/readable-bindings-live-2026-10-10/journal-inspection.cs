using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
var store=KeyVaultRecordStoreFactory.CreateWorkspaceStore(null,args[0]);
var headRecord=await store.GetAsync("flow-execution-journal-v9","benchmark","readablebindings20261010a-amazon-1","GnOuGo.Flow.Persistence");
if(headRecord is not null) { var h=JsonNode.Parse(headRecord.Value)!; Console.WriteLine(JsonSerializer.Serialize(new { journal_revision=h["revision"], journal_event_count=h["eventCount"], invocation_count=(h["invocations"] as JsonObject)?.Count, invocation_ids=(h["invocations"] as JsonObject)?.Select(p=>p.Key).ToArray(), checkpoint_bytes=System.Text.Encoding.UTF8.GetByteCount(headRecord.Value) })); }

if(args.Contains("--sizes") && headRecord is not null) {
 var h=JsonNode.Parse(headRecord.Value)!;
 var prefix=Hash(new JsonArray("benchmark","readablebindings20261010a-amazon-1").ToJsonString())+"/";
 var cache=new Dictionary<string,JsonObject>(); var sizes=new Dictionary<string,(long Bytes,long Count)>(); long storedBytes=0;
 async Task<JsonObject> Block(JsonNode reference) {
  var hash=reference[1]!.ToString(); if(cache.TryGetValue(hash,out var b)) return b;
  var row=await store.GetAsync("flow-execution-blocks-v1","benchmark",prefix+hash,"GnOuGo.Flow.Persistence")??throw new Exception("Missing block");
  var e=JsonNode.Parse(row.Value)!;
  if(e["tenantId"]!.ToString()!="benchmark" || e["runId"]!.ToString()!="readablebindings20261010a-amazon-1") throw new Exception("Wrong owner");
  b=e["body"]!.AsObject(); if(Hash(b.ToJsonString())!=hash)throw new Exception("Hash mismatch");
  cache[hash]=b; storedBytes+=System.Text.Encoding.UTF8.GetByteCount(row.Value); return b;
 }
 async Task<(long Bytes,long Count)> Size(JsonNode reference) {
  if(reference[0]!.GetValue<int>()==0) {var v=reference[1]; return (Bytes(v),v is JsonArray a?a.Count:-1);}
  var hash=reference[1]!.ToString(); if(sizes.TryGetValue(hash,out var old))return old;
  var b=await Block(reference); var values=b["values"] as JsonArray; long length=2,count=-1;
  switch(b["kind"]!.ToString()) {
   case "value": length=Bytes(b["value"]);break;
   case "object":
    foreach(var f in values!) length+=Bytes(f![0])+1+(await Size(f[1]!)).Bytes;
    length+=Math.Max(0,values.Count-1);break;
   case "array":count=values!.Count;foreach(var v in values)length+=(await Size(v!)).Bytes;length+=Math.Max(0,count-1);break;
   case "chunks":count=0;foreach(var v in values!){var t=await Size(v!);length+=t.Bytes-2+(count>0&&t.Count>0?1:0);count+=t.Count;}break;
   default:throw new Exception("Unknown kind");
  }
  return sizes[hash]=(length,count);
 }
 async Task<Dictionary<string,JsonNode?>> Fields(JsonNode reference) {
  if(reference[0]!.GetValue<int>()==0)return reference[1]!.AsObject().ToDictionary(p=>p.Key,p=>(JsonNode?)new JsonArray(0,p.Value?.DeepClone()));
  var b=await Block(reference); if(b["kind"]!.ToString()!="object")throw new Exception("Expected object");
  return b["values"]!.AsArray().ToDictionary(f=>f![0]!.ToString(),f=>f![1]);
 }
 JsonNode? Scalar(Dictionary<string,JsonNode?> f,string key)=>f.TryGetValue(key,out var v)&&v?[0]?.GetValue<int>()==0?v[1]?.DeepClone():null;
 var rows=new JsonArray();
 foreach(var inv in h["invocations"]!.AsObject()) {
  var f=await Fields(inv.Value!); var total=await Size(inv.Value!);
  rows.Add(new JsonObject { ["id"]=inv.Key,["status"]=Scalar(f,"status"),["stepType"]=Scalar(f,"stepType"),["error"]=Scalar(f,"error"),["completionObserved"]=Scalar(f,"externalCompletionObserved"),["logical_bytes"]=total.Bytes,
   ["dataBefore_bytes"]=f.TryGetValue("dataBefore",out var before)?(await Size(before!)).Bytes:0,
   ["dataAfter_bytes"]=f.TryGetValue("dataAfter",out var after)?(await Size(after!)).Bytes:0,
   ["output_bytes"]=f.TryGetValue("output",out var output)?(await Size(output!)).Bytes:0 });
 }
 Console.WriteLine(new JsonObject { ["inspection_only"]=true,["provider_calls"]=0,["unique_blocks_read"]=cache.Count,["unique_record_plaintext_bytes"]=storedBytes,["invocations"]=rows }.ToJsonString());
}
static long Bytes(JsonNode? value)=>System.Text.Encoding.UTF8.GetByteCount(value?.ToJsonString()??"null");
static string Hash(string s)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));
