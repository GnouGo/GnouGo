using System.Text.Json.Nodes;
using GnOuGo.KeyVault.Core.Services;
var store=KeyVaultRecordStoreFactory.CreateWorkspaceStore(null,args[0]);
var prefix="schema-portability-20261002:compactv1020261011a-amazon-1:";
var rows=new JsonArray();
foreach(var record in (await store.ListAsync("planning-evaluation-http-attempts","benchmark","GnOuGo.Planning.Benchmark")).Where(r=>r.Key.StartsWith(prefix,StringComparison.Ordinal)))
{
 var data=JsonNode.Parse(record.Value)!;var attempts=new JsonArray();
 foreach(var a in data["transport"]!["Attempts"]!.AsArray())
 {
  var item=new JsonObject(); foreach(var key in new[]{"Id","Status","RetryAfter","Failure","NotBefore"})item[key]=a![key]?.DeepClone();
  if(a!["Status"]?.GetValue<int>() is >=400 && a["Body"]?.GetValue<string>() is {} body)
  {item["response_excerpt"]=body[..Math.Min(body.Length,1024)]; try{var error=JsonNode.Parse(body)?["error"]; item["error_code"]=error?["code"]?.DeepClone();item["error_type"]=error?["type"]?.DeepClone();}catch(System.Text.Json.JsonException){}}
  attempts.Add(item);
 }
 rows.Add(new JsonObject{["request_id"]=record.Key,["attempts"]=attempts,["usage"]=data["usage"]?.DeepClone()});
}
Console.WriteLine(rows.ToJsonString());

Console.Error.WriteLine((await store.GetAsync("planning-evaluation-configuration","benchmark","schema-portability-20261002:http-retry-policy","GnOuGo.Planning.Benchmark"))?.Value);

var requests=new JsonArray();
foreach(var r in (await store.ListAsync("planning-evaluation-requests","benchmark","GnOuGo.Planning.Benchmark")).Where(r=>r.Key.StartsWith(prefix,StringComparison.Ordinal)))
{
 var request=JsonNode.Parse(r.Value)!;
 requests.Add(new JsonObject{["request_id"]=r.Key,["issued_request_serialized_bytes"]=System.Text.Encoding.UTF8.GetByteCount(r.Value),["issued_request_sha256"]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(r.Value))).ToLowerInvariant(),["has_committed_receipt"]=await store.GetAsync("planning-evaluation-receipts","benchmark",r.Key,"GnOuGo.Planning.Benchmark") is not null});
}
Console.WriteLine(new JsonObject{["request_metrics"]=requests,["read_only"]=true,["model_calls"]=0}.ToJsonString());
