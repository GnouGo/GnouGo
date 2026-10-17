using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class FusedBindingCompilationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputPhaseFlattenAndLookupUseNativeExpressionsWithoutTransportSets(bool unknown)
    {
        var record = JsonNode.Parse("""{"type":"object","properties":{"key":{"type":"string"},"text":{"type":["string","null"]}},"required":["key","text"],"additionalProperties":false}""")!.AsObject();
        var records = new JsonObject { ["type"] = "array", ["items"] = record };
        var pages = new JsonObject { ["type"] = "array", ["items"] = records.DeepClone() };
        var selected = JsonNode.Parse("""{"type":"array","items":{"type":"string"}}""")!.AsObject();
        var graph = new PlanningGraph { Workflows = [new() {
            Inputs = [new() { Name = "observationPages", Schema = new() { Contract = pages } }, new() { Name = "selectedKeys", Schema = new() { Contract = selected } }],
            Outputs = [new() { Name = "products", Schema = new() { Contract = records }, Value = new() { Kind = "lookup", Text = "key", Items = [
                new() { Kind = "flatten", Items = [new() { Kind = "input", Source = "observationPages" }] }, new() { Kind = "input", Source = "selectedKeys" }] } }]
        }] };
        var compiler = new PlanningGraphCompiler(); var catalog = await Catalog();
        var before = compiler.Compile(graph, catalog, "outputs", true, true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "outputs", true, true, true, true, true, true, true, true, true, true);
        Assert.Contains("expression_contracts:", after); Assert.DoesNotContain("checkedMapping(", after);
        Assert.Contains(".flatMap(", after); Assert.Contains("m.lookup(", after);
        var input = new JsonObject { ["observationPages"] = new JsonArray(new JsonArray(new JsonObject { ["key"] = "exact:1", ["text"] = new string('x', 1_200_000) }),
            new JsonArray(), new JsonArray(new JsonObject { ["key"] = "exact:2", ["text"] = null })), ["selectedKeys"] = new JsonArray("exact:2", unknown ? "absent" : "exact:1", "exact:2") };
        var original = input.ToJsonString(); string? outcome = null;
        foreach (var yaml in new[] { before, after })
        {
            var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
            Assert.Empty(workflow.Steps);
            var result = await new WorkflowEngine().ExecuteAsync(workflow, input, PlannerFixture.Ct);
            Assert.Equal(!unknown, result.Success);
            var current = result.Success ? result.Outputs!.ToJsonString() : result.Error!.Code + result.Error.Message;
            if (outcome is not null) Assert.Equal(outcome, current); outcome = current;
            Assert.Equal(original, input.ToJsonString());
            if (result.Success)
            {
                Assert.Equal(3, result.Outputs!["products"]!.AsArray().Count);
                Assert.Null(result.Outputs["products"]![0]!["text"]);
                Assert.Equal(input["observationPages"]![0]![0]!["text"]!.GetValue<string>(), result.Outputs["products"]![1]!["text"]!.GetValue<string>());
            }
        }
        if (!unknown && Environment.GetEnvironmentVariable("GNOU_GO_V12_EXAMPLES") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "checked-outputs-v11.yaml"), before);
            File.WriteAllText(Path.Combine(folder, "checked-outputs-v12.yaml"), after);
        }
    }

    [Fact]
    public async Task ExportChecksStayBeforeCleanupAndKeepTheirDurableIdentity()
    {
        var graph = Graph(); graph.Workflows[0].Finally.Add(new() { Key = "cleanup", Input = Obj("closed", new() { Kind = "boolean", Boolean = true }) });
        var catalog = await Catalog(); var compiler = new PlanningGraphCompiler();
        var before = compiler.Compile(graph, catalog, "timing", true, true, true, true, true, true, true, true, true);
        var after = compiler.Compile(graph, catalog, "timing", true, true, true, true, true, true, true, true, true, true);
        foreach (var yaml in new[] { before, after })
        {
            var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
            Assert.Equal("n_" + PlanningGraphCompiler.Fingerprint("first")[..16], Assert.Single(workflow.Steps).Id);
            var result = await new WorkflowEngine().ExecuteAsync(workflow, new JsonObject { ["label"] = "", ["authorized"] = false }, PlannerFixture.Ct);
            Assert.False(result.Success); Assert.Null(result.Outputs);
            Assert.Equal("first", result.Error!.Details!["location"]!.GetValue<string>());
            Assert.True(result.StepResults[^1].Output!["closed"]!.GetValue<bool>());
        }
        if (Environment.GetEnvironmentVariable("GNOU_GO_V12_EXAMPLES") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "retained-export-v11.yaml"), before);
            File.WriteAllText(Path.Combine(folder, "retained-export-v12.yaml"), after);
        }
    }
}
