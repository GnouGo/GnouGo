using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning;
var runtime=new WorkflowPlanningRuntime(new(),(_,_)=>Task.CompletedTask);
var catalog=await runtime.DiscoverAsync(new(){Policy=new(){RequireExternalConfirmation=false}},CancellationToken.None);
foreach(var compact in new[]{false,true}) foreach(var count in new[]{4,8}) {
 var compilation=new TaskPlanCompiler().Compile(Plan(true,false),catalog,compact);
 if(compilation.Diagnostics.Count>0)throw new Exception(JsonSerializer.Serialize(compilation.Diagnostics));
 var yaml=new PlanningGraphCompiler().Compile(compilation.Graph!,catalog);
 var doc=new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
 var store=new InMemoryWorkflowRunStore();var engine=new WorkflowEngine {RunStore=store,Limits=new(){TenantId="metrics",RunId="fixture"}};
 var result=await engine.ExecuteAsync(doc.Workflows["main"],new JsonObject{["records"]=Records(count,1000)},CancellationToken.None);
 if(!result.Success || !JsonNode.DeepEquals(Expected(count),result.Outputs!["rows"]))throw new Exception("Independent value oracle failed.");
 var run=(await store.ReadAsync("metrics","fixture"))!;
 var loops=run.Invocations.Values.Where(i=>i.StepType=="loop.sequential");
 Console.WriteLine(JsonSerializer.Serialize(new{compact,count,yaml_bytes=System.Text.Encoding.UTF8.GetByteCount(yaml),workflows=doc.Workflows.Count,
 invocations=run.Invocations.Count,collected_bytes=loops.Sum(i=>System.Text.Encoding.UTF8.GetByteCount(i.Output!["results"]!.ToJsonString())),
 snapshot_bytes=run.Invocations.Values.Sum(i=>(long)System.Text.Encoding.UTF8.GetByteCount(i.DataBefore.ToJsonString())+(i.DataAfter is null?0:System.Text.Encoding.UTF8.GetByteCount(i.DataAfter.ToJsonString()))),model_calls=0,execution_oracle=true}));
}
    static TaskValue Ref(string task, string port) => new() { Kind = "output", Source = task, Port = port };
    static TaskValue Field(TaskValue value, string port) => new() { Kind = "field", Port = port, Items = [value] };
    static TaskPlan Plan(bool guarded, bool parallel) => new()
    {
        Inputs = [new() { Name = "records", Type = new() { Kind = "array", Items = new() { Kind = "object", Fields = [
            new() { Name = "label" }, new() { Name = "noise" }, new() { Name = "children", Type = new() { Kind = "array", Items = new() { Kind = "string", Nullable = true } } }] } } }],
        Root = new() { Tasks = new[] { "first", "second" }.Select(id => new PlanTask
        {
            Id = id, Kind = "foreach", Objective = "Preserve selected observed fields", MaxItems = 100, Parallel = parallel,
            Items = new() { Kind = "input", Source = "records" }, Body = new() { Tasks = [new() {
                Id = id + "_view", Kind = "value", Objective = "Copy fields",
                Requires = guarded ? new() { Kind = "boolean", Boolean = true } : null,
                Outputs = [new("row", new() { Kind = "object", Members = [new("name", Field(new() { Kind = "item" }, "label")),
                    new("nested", Field(new() { Kind = "item" }, "children")), new("label", new() { Kind = "string", Text = "explicit business literal" })] })] }], Outputs = [new("rows", Ref(id + "_view", "row"))] }
        }).ToList(), Outputs = [new("rows", Ref("second", "rows"))] }
    };

    static JsonArray Records(int count, int noise) => new(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject {
        ["label"] = "same", ["noise"] = new string('x', noise), ["children"] = new JsonArray("a", null, "a") }).ToArray());
    static JsonArray Expected(int count) => new(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject {
        ["name"] = "same", ["nested"] = new JsonArray("a", null, "a"), ["label"] = "explicit business literal" }).ToArray());
