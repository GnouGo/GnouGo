using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class FusedBindingCompilationTests
{
    [Theory]
    [InlineData("exact ` ${literal} é", true, null)]
    [InlineData("", false, "first")]
    [InlineData("observed", false, "guard")]
    public async Task DirectScalarLocalsRemoveWrappersButRetainFourOrderedChecks(string label, bool authorized, string? failure)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        workflow.Inputs.Add(new() { Name = "pattern" });
        workflow.Steps.Add(new() { Key = "render", Type = "template.render", Input = new() { Kind = "object", Members = [
            new("template", new() { Kind = "input", Source = "pattern" }), new("data", Obj("chosen", Ref("export", "chosen")))] } });
        workflow.Outputs.Add(new() { Name = "text", Value = Ref("render", "text") });
        var catalog = await Catalog(); var compiler = new PlanningGraphCompiler();
        var before = compiler.Compile(graph, catalog, "direct", true, true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "direct", true, true, true, true, true, true, true, true, true, true);
        var readable = compiler.Compile(graph, catalog, "direct", true, true, true, true, true, true, true, true, true, true, true, true);
        SaveV14Comparison("multiple-inputs", compiler.Compile(graph, catalog, "direct", true, true, true, true, true, true, true, true, true, true, true), readable);
        Assert.Contains("const label = data.inputs.label;", after);
        Assert.DoesNotContain("const chosen", after); Assert.DoesNotContain("label.value", after);
        var step = Assert.Single(new WorkflowCompiler().Compile(WorkflowParser.Parse(after)).Workflows["main"].Steps);
        Assert.Equal(4, step.Source.ExpressionContracts![""]!.AsObject().Count);
        string? message = null;
        foreach (var yaml in new[] { before, after, readable })
        {
            var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                new JsonObject { ["label"] = label, ["authorized"] = authorized, ["pattern"] = "{{{chosen}}}" }, PlannerFixture.Ct);
            Assert.True((failure is null) == result.Success, $"{result.Error?.Code}: {result.Error?.Message}; {result.Error?.Details}");
            if (failure is null) Assert.Equal(label, result.Outputs!["text"]!.GetValue<string>());
            else
            {
                Assert.Equal(failure, result.Error!.Details!["location"]!.GetValue<string>());
                if (message is not null) Assert.Equal(message, result.Error.Message);
                message = result.Error.Message;
            }
        }
        Assert.Equal(before, compiler.Compile(graph, catalog, "direct", true, true, true, true, true, true, true, true, true));
        if (failure is null && Environment.GetEnvironmentVariable("GNOU_GO_V12_EXAMPLES") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "multiple-inputs-v11.yaml"), before);
            File.WriteAllText(Path.Combine(folder, "multiple-inputs-v12.yaml"), after);
        }
    }

    [Theory]
    [InlineData("oneOf")]
    [InlineData("reference")]
    public async Task CoupledOrReferencedSchemasKeepTheirWrapperAndExactFailures(string kind)
    {
        var graph = Graph(); var schema = graph.Workflows[0].Steps[0].OutputSchema!.Contract!;
        if (kind == "oneOf")
        {
            var first = schema.DeepClone(); var second = schema.DeepClone();
            second["properties"]!["value"] = JsonNode.Parse("""{"type":"string","maxLength":0}""");
            schema["oneOf"] = new JsonArray(first, second);
        }
        else
        {
            schema["$defs"] = new JsonObject { ["shape"] = schema.DeepClone() };
            schema["$ref"] = "#/$defs/shape";
        }
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        var before = compiler.Compile(graph, catalog, "retained", true, true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "retained", true, true, true, true, true, true, true, true, true, true);
        var readable = compiler.Compile(graph, catalog, "retained", true, true, true, true, true, true, true, true, true, true, false, true);
        Assert.Contains("const label = {", after);
        foreach (var label in new[] { "observed", "" })
        {
            string? previous = null;
            foreach (var yaml in new[] { before, after, readable })
            {
                var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
                    new JsonObject { ["label"] = label, ["authorized"] = true }, PlannerFixture.Ct);
                var outcome = result.Success ? result.Outputs!.ToJsonString() : result.Error!.Code + result.Error.Message;
                if (previous is not null) Assert.Equal(previous, outcome);
                previous = outcome;
            }
        }
    }
}
