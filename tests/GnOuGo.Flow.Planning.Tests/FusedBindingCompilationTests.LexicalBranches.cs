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
        var readable = compiler.Compile(graph, catalog, "branches", true, true, true, true, true, true, true, true, true, true, true, true);
        SaveV14Comparison("nullable-branch", after, readable);
        SaveV13Comparison("nullable-branch", before, after);
        Assert.DoesNotContain("type: switch", after); Assert.Contains("scope:", after);
        Assert.Empty(WorkflowParser.Parse(after).Workflows["main"].Steps);
        Assert.Contains("m.select", after); // First-present includes a present null.
        foreach (var yaml in new[] { before, after, readable })
        {
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                new JsonObject { ["label"] = label, ["authorized"] = authorized }, PlannerFixture.Ct);
            Assert.Equal(success, result.Success);
            if (success) Assert.Equal(authorized ? label : null, result.Outputs!["text"]?.GetValue<string>());
            else Assert.Contains("at least 1 characters", result.Error!.Message);
        }
    }

    private static void SaveV14Comparison(string name, string before, string after)
    {
        if (Environment.GetEnvironmentVariable("GNOU_GO_V14_EXAMPLES") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + "-v13.yaml"), before);
        File.WriteAllText(Path.Combine(folder, name + "-v14.yaml"), after);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BranchOutputCanShareItsNameWithAnIntermediateField(bool enabled)
    {
        var catalog = await Catalog();
        TaskValue ObservedMessage() => new()
        {
            Kind = "field", Port = "message",
            Items = [new() { Kind = "at_index", Items = [
                new() { Kind = "input", Source = "records" }, new() { Kind = "number", Number = 0 }] }]
        };
        var plan = new TaskPlan
        {
            Inputs = [
                new() { Name = "enabled", Type = new() { Kind = "boolean" } },
                new() { Name = "records", Type = new() { Kind = "array", MinItems = 1,
                    Items = new() { Kind = "object", Fields = [new() { Name = "message", Type = new() { Kind = "string" } }] } } }
            ],
            Root = new()
            {
                Tasks = [new()
                {
                    Id = "choose", Kind = "conditional", Objective = "Report the selected outcome",
                    Condition = new() { Kind = "input", Source = "enabled" },
                    Body = new() { Outputs = [new("outcome", new() { Kind = "string", Text = "accepted" })] },
                    Otherwise = new()
                    {
                        Outputs = [new("outcome", new()
                        {
                            Kind = "json", Items = [new() { Kind = "object", Members = [
                                new("enabled", new() { Kind = "input", Source = "enabled" }), new("message", ObservedMessage())] }]
                        })]
                    }
                }],
                Outputs = [new("result", new() { Kind = "output", Source = "choose", Port = "outcome" })]
            }
        };
        var request = PlannerFixture.Session().Request;
        request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        var compiled = new TaskPlanCompiler().Compile(plan, catalog, request);
        Assert.Empty(compiled.Diagnostics);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog, "branch outputs",
            descriptions: true, fuseBindings: true, consumerBindings: true, directProjections: true, nativeMappings: true,
            readableMappings: true, leanMappings: true, compactProjections: true, inlineConsumers: true, directValues: true,
            checkedControls: true, readableBranches: true, simplifiedLocals: true);
        var result = await LookupCompilationTests.Execute(yaml, new() { ["enabled"] = enabled,
            ["records"] = new JsonArray(new JsonObject { ["message"] = "observed" }) }, new());
        Assert.True(result.Success, result.Error?.Message);
        var value = result.Outputs!["result"]!.GetValue<string>();
        if (enabled) Assert.Equal("accepted", value);
        else Assert.Equal("observed", JsonNode.Parse(value)!["message"]!.GetValue<string>());
    }
}
