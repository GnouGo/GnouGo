using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class FusedBindingCompilationTests
{
    [Theory]
    [InlineData("  exact\ntext é ", true, true)]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    public async Task PureBranchesPreserveNullableValuesAndOnlyReachedChecks(string label, bool authorized, bool success)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Steps.Clear(); workflow.Outputs.Clear();
        var nullable = Schema("""{"type":"object","properties":{"value":{"type":["string","null"]}},"required":["value"],"additionalProperties":false}""");
        workflow.Steps.Add(new() { Key = "choose", Type = "switch", Expr = new() { Kind = "input", Source = "authorized" },
            Cases = [new("true", null, [new() { Key = "copy", Input = Project(new() { Kind = "input", Source = "label" }),
                OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"string","minLength":1}},"required":["value"],"additionalProperties":false}""") }])],
            Default = [new() { Key = "absent", Input = Obj("value", new() { Kind = "null" }), OutputSchema = nullable }] });
        workflow.Steps.Add(new() { Key = "merge", Input = new() { Kind = "projection", Members = [new("value", Ref("choose")),
            new("paths", new() { Kind = "array", Items = [new() { Kind = "array", Items = [new() { Kind = "string", Text = "copy" }, new() { Kind = "string", Text = "value" }] },
                new() { Kind = "array", Items = [new() { Kind = "string", Text = "absent" }, new() { Kind = "string", Text = "value" }] }] })] }, OutputSchema = nullable });
        workflow.Outputs.Add(new() { Name = "text", Schema = new() { Type = "string", Nullable = true }, Value = Ref("merge", "value") });
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        var before = compiler.Compile(graph, catalog, "branches", true, true, true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "branches", true, true, true, true, true, true, true, true, true, true, true);
        SaveV13Comparison("nullable-branch", before, after);
        Assert.DoesNotContain("type: switch", after); Assert.Contains("scope:", after);
        Assert.Empty(WorkflowParser.Parse(after).Workflows["main"].Steps);
        Assert.Contains("m.select", after); // First-present includes a present null.
        foreach (var yaml in new[] { before, after })
        {
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                new JsonObject { ["label"] = label, ["authorized"] = authorized }, PlannerFixture.Ct);
            Assert.Equal(success, result.Success);
            if (success) Assert.Equal(authorized ? label : null, result.Outputs!["text"]?.GetValue<string>());
            else Assert.Contains("at least 1 characters", result.Error!.Message);
        }
    }
}
