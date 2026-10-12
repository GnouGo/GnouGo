using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class FusedBindingCompilationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ControlConsumerRetainsChecksWithoutAPreparationInvocation(bool allowed)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Steps.Clear(); workflow.Outputs.Clear();
        workflow.Steps.Add(new() { Key = "checked", Input = Project(new() { Kind = "input", Source = "authorized" }),
            OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}""") });
        workflow.Steps.Add(new() { Key = "branch", Type = "switch", Expr = Ref("checked", "value"), Cases = [new("true", null, [
            new() { Key = "visited", Input = Obj("visited", new() { Kind = "boolean", Boolean = true }) }])] });
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        var before = compiler.Compile(graph, catalog, "controls", true, true, true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "controls", true, true, true, true, true, true, true, true, true, true, true);
        SaveV13Comparison("control", before, after);
        Assert.Contains("control_contracts:", after);
        Assert.Single(WorkflowParser.Parse(after).Workflows["main"].Steps);
        foreach (var yaml in new[] { before, after })
        {
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                new JsonObject { ["authorized"] = allowed, ["label"] = "unused" }, PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(allowed, result.StepResults.Last().Output?.AsObject().Any(p => p.Value is JsonObject obj && obj.ContainsKey("visited")) == true);
        }
    }

    private static void SaveV13Comparison(string name, string before, string after)
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_V13_EXAMPLES") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + "-v12.yaml"), before);
        File.WriteAllText(Path.Combine(folder, name + "-v13.yaml"), after);
    }
}
