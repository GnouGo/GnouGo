using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
var fixture=await File.ReadAllTextAsync(args[0]);
foreach(var count in new[]{4,8,16}) {
 var document=new WorkflowCompiler().Compile(WorkflowParser.Parse(fixture));
 var input=new JsonObject{["payload"]=new string('x',8192),["items"]=new JsonArray(Enumerable.Range(0,count).Select(i=>(JsonNode)JsonValue.Create(i)).ToArray())};
 var result=await new WorkflowEngine().ExecuteAsync(document.Workflows["main"],input,CancellationToken.None);
 if(!result.Success) throw new Exception(result.Error?.Message);
 var first=result.Outputs!["first"]!.AsArray(); var second=result.Outputs["second"]!.AsArray();
 if(first.Count!=count||second.Count!=count)throw new Exception("Missing items");
 for(var i=0;i<count;i++)if(first[i]!["first_record"]!["value"]!.GetValue<int>()!=i||second[i]!["second_record"]!["value"]!.GetValue<int>()!=i)throw new Exception("Changed business values");
 Console.WriteLine(new JsonObject{["items"]=count,["source_payload_bytes"]=8192,["first_result_json_bytes"]=Encoding.UTF8.GetByteCount(first.ToJsonString()),["second_result_json_bytes"]=Encoding.UTF8.GetByteCount(second.ToJsonString()),["first_result_copies_inside_second"]=second.Count(r=>r!["first_view"] is not null),["exact_values_and_order"]=true,["journal_enabled"]=false,["model_calls"]=0,["external_calls"]=0}.ToJsonString());
}
