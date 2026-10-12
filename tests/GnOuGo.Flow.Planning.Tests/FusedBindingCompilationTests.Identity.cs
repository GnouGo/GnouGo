using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Planning;
namespace GnOuGo.Flow.Planning.Tests;
public sealed partial class FusedBindingCompilationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WrapperIdentityComparisonKeepsItsOriginalValue(bool alias, bool contained)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Steps.RemoveRange(2, 2);
        if (contained)
        {
            workflow.Inputs[0].Schema = Schema("""{"type":"object","properties":{"observed":{"type":"string"}},"required":["observed"],"additionalProperties":false}""");
            workflow.Steps[0].OutputSchema!.Contract!["properties"]!["value"] = workflow.Inputs[0].Schema.Contract!.DeepClone();
        }
        if (alias) workflow.Steps.Add(new() { Key = "copy", Input = Ref("first"), OutputSchema = new() { Contract = workflow.Steps[0].OutputSchema!.Contract!.DeepClone().AsObject() } });
        string[] path = contained ? ["value"] : [];
        workflow.Steps.Add(new() { Key = "compare", Input = Obj("same", PlanningValues.Predicate("equal", Ref("first", path), Ref(alias ? "copy" : "first", path))),
            OutputSchema = Schema("""{"type":"object","properties":{"same":{"type":"boolean"}},"required":["same"],"additionalProperties":false}""") });
        workflow.Outputs.Clear(); workflow.Outputs.Add(new() { Name = "same", Value = Ref("compare", "same"), Schema = new() { Type = "boolean" } });
        var catalog = await Catalog(); var compiler = new PlanningGraphCompiler(); bool? original = null;
        foreach(var profile in new[] { 11, 12, 14 })
        {
            var yaml = compiler.Compile(graph, catalog, "identity", true, true, true, true, true, true, true, true, true, profile >= 12, profile >= 13, profile >= 14);
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                new JsonObject { ["label"]=contained ? new JsonObject { ["observed"]="exact" } : JsonValue.Create("exact"), ["authorized"]=true }, PlannerFixture.Ct);
            Assert.True(result.Success,result.Error?.Message);
            var same=result.Outputs!["same"]!.GetValue<bool>();
            Assert.Equal(!alias, same);
            if(original is not null) Assert.Equal(original.Value,same); original=same;
        }
    }
}
