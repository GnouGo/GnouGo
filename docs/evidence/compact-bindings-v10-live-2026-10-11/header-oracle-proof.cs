using System.Reflection;
using System.Text.Json.Nodes;
JsonObject Read(string url,string id,params JsonObject[] records) => new() {
 ["tool"]="browser_get_content",["error"]=false,["arguments"]=new JsonObject{["url"]=url},
 ["result"]=new JsonObject{["url"]=url,["success"]=true,["truncated"]=false,
 ["observationSnapshot"]=new JsonObject{["id"]=id,["captureTruncated"]=false,["manifestTruncated"]=false,["recordCount"]=records.Length,
 ["pages"]=new JsonArray(new JsonObject{["id"]=id,["captureTruncated"]=false,["nextCursor"]=null,["records"]=new JsonArray(records.Cast<JsonNode?>().ToArray())})}}};
var events=new List<JsonObject>{
 Read("https://fixture.invalid/search","one",new JsonObject(){["href"]="https://fixture.invalid/item/a?variant=1",["text"]="Product A"}),
 Read("https://fixture.invalid/item/a?variant=1","two",new JsonObject(){["text"]="Product A"},new JsonObject(){["text"]="Complete observed description."},new JsonObject(){["text"]="12 €"})};
var oracle=Assembly.Load("GnOuGo.Agent.Planning.Benchmark").GetType("LiveWorkflowOracles")!.GetMethod("VerifyObservedProducts",BindingFlags.Static|BindingFlags.NonPublic)!;
string[] row=["Product A","Complete observed description.","12 €",""];
JsonObject Verify(string[][] rows)=>(JsonObject)oracle.Invoke(null,[events,rows,10])!;
var withoutHeader=Verify([row]);var withHeader=Verify([["name","description","price","error"],row]);
if(withoutHeader["passed"]!.GetValue<bool>() || !withHeader["passed"]!.GetValue<bool>())throw new Exception("Independent oracle witness changed.");
Console.WriteLine(new JsonObject{["model_calls"]=0,["mcp_calls"]=0,["workflow_execution"]=false,["without_header"]=withoutHeader,["with_header"]=withHeader}.ToJsonString());
