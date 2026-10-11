using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Core.Compilation;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class FusedBindingCompilationTests
{
    [Theory]
    [InlineData("default", "default_2", "default_3")]
    [InlineData("chosen", "chosen", "chosen_2")]
    public async Task FunctionalNamesAreStableCollisionSafeAndDoNotChangePorts(string field, string first, string second)
    {
        var graph = Graph();
        foreach (var node in graph.Workflows[0].Steps.Skip(2))
        {
            node.Input.Members[0] = new(field, node.Input.Members[0].Value);
            if (node.Key == "export") node.Input.Members[0].Value.Path = [field];
            var shape = node.OutputSchema!.Contract!;
            var properties = shape["properties"]!.AsObject();
            var contract = properties["chosen"]!.DeepClone(); properties.Clear(); properties[field] = contract;
            shape["required"] = new JsonArray(field);
        }
        graph.Workflows[0].Outputs[0].Value.Path = [field];
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        var yaml = compiler.Compile(graph, catalog, "names", true, true, true, true, true, true, true, true, true);
        Assert.Contains("const " + first + " =", yaml); Assert.Contains("const " + second + " =", yaml);
        Assert.Equal(yaml, compiler.Compile(graph, catalog, "names", true, true, true, true, true, true, true, true, true));
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
            new JsonObject { ["label"] = "exact", ["authorized"] = true }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("exact", result.Outputs!["chosen"]!.GetValue<string>());
    }

    [Fact]
    public async Task NativeFlattenUsesCheckedBusinessLocalsWithoutCaptureCopies()
    {
        var array = JsonNode.Parse("""{"type":"array","items":{"type":"array","items":{"type":["number","null"]}}}""")!.AsObject();
        var graph = new PlanningGraph { Workflows = [new() {
            Inputs = [new() { Name = "searchPages", Schema = new() { Contract = array } }],
            Steps = [
                new() { Key = "pages", Input = Project(new() { Kind = "input", Source = "searchPages" }),
                    OutputSchema = new() { Contract = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["value"] = array.DeepClone() },
                        ["required"] = new JsonArray("value"), ["additionalProperties"] = false } } },
                new() { Key = "products", InternalRole = "typed_assembly", Input = Obj("products", new() { Kind = "flatten", Items = [Ref("pages", "value")] }),
                    OutputSchema = Schema("""{"type":"object","properties":{"products":{"type":"array","items":{"type":["number","null"]}}},"required":["products"],"additionalProperties":false}""") }],
            Outputs = [new() { Name = "products", Schema = new() { Contract = array["items"]!.DeepClone().AsObject() }, Value = Ref("products", "products") }]
        }] };
        var catalog = await Catalog(); var compiler = new PlanningGraphCompiler();
        var before = compiler.Compile(graph, catalog, "readable", true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "readable", true, true, true, true, true, true, true, true, true);
        if (Environment.GetEnvironmentVariable("GNOU_GO_V11_EXAMPLES") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "flatten-v10.yaml"), before);
            File.WriteAllText(Path.Combine(folder, "flatten-v11.yaml"), after);
        }
        Assert.Contains("binding_input_value", before);
        Assert.DoesNotContain("binding_input_value", after); Assert.DoesNotContain("checkedValue", after);
        Assert.Contains("const searchPages", after); Assert.Contains("searchPages.value.flatMap(item => item)", after);
        Assert.True(after.Length < before.Length, $"{before.Length}→{after.Length}");
        Assert.Equal(before, compiler.Compile(graph, catalog, "readable", true, true, true, true, true, true, true, true));
        var inputs = JsonNode.Parse("{\"searchPages\":[[9007199254740993,null],[],[1.23000000000000000000001,9007199254740993]]}")!;
        var expected = inputs.ToJsonString(); string? previous = null;
        foreach (var yaml in new[] { before, after })
        {
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"], inputs.DeepClone(), PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
            var numbers = result.Outputs!["products"]!.ToJsonString();
            Assert.Equal("[9007199254740993,null,1.23000000000000000000001,9007199254740993]", numbers);
            if (previous is not null) Assert.Equal(previous, numbers); previous = numbers;
            Assert.Equal(expected, inputs.ToJsonString());
        }
    }
}
