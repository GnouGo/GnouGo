using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class FusedBindingCompilationTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ExplicitContainerSelectionsKeepObservedIdentitiesWithoutBlockingPrivatePreparation(bool whole, bool authorized)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        var owner = new PlanningNode { Key = "scope", Type = "sequence", Steps = workflow.Steps };
        var source = Ref("scope");
        workflow.Steps = [owner, new() { Key = "selected", Input = new() { Kind = "projection", Members = [
            new("value", source), new("paths", new() { Kind = "array", Items = [new() { Kind = "array", Items = [
                new() { Kind = "string", Text = "export" }, new() { Kind = "string", Text = "chosen" }] }] })] },
            OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""") }];
        workflow.Outputs = [new() { Name = "chosen", Value = Ref("selected", "value") }];
        if (whole) workflow.Outputs.Add(new() { Name = "whole", Schema = new() { Contract = new JsonObject {
            ["type"] = "object", ["properties"] = new JsonObject(owner.Steps.Select(n => new KeyValuePair<string, JsonNode?>(n.Key, n.OutputSchema!.Contract!.DeepClone()))),
            ["required"] = new JsonArray(owner.Steps.Select(n => (JsonNode?)JsonValue.Create(n.Key)).ToArray()) } }, Value = source });
        var catalog = await Catalog();
        foreach (var inline in new[] { false, true })
        {
            var yaml = new PlanningGraphCompiler().Compile(graph, catalog, "test", true, true, true, true, true, true, true, true, inline);
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            Assert.Equal(inline && !whole ? 1 : 4, document.Workflows["main"].Steps[0].Source.Steps!.Count);
            var input = new JsonObject { ["label"] = new string('x', 1_200_000), ["authorized"] = authorized };
            var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], input, PlannerFixture.Ct);
            Assert.Equal(authorized, result.Success);
            if (authorized) Assert.Equal(input["label"]!.ToString(), result.Outputs!["chosen"]!.ToString());
            else Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
        }
    }

    [Theory]
    [InlineData("observed", true, null)]
    [InlineData("", false, "first")]
    [InlineData("observed", false, "guard")]
    public async Task MultipleDynamicFieldsCheckOnceBeforeConsumerResolution(string label, bool authorized, string? failure)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        workflow.Inputs.Add(new() { Name = "pattern" });
        workflow.Steps.Add(new() { Key = "render", Type = "template.render", Input = new() { Kind = "object", Members = [
            new("template", new() { Kind = "input", Source = "pattern" }),
            new("data", Obj("chosen", Ref("export", "chosen")))] } });
        workflow.Outputs.Add(new() { Name = "text", Value = Ref("render", "text") });
        var catalog = await Catalog();
        foreach (var inline in new[] { false, true })
        {
            var yaml = new PlanningGraphCompiler().Compile(graph, catalog, "test", true, true, true, true, true, true, true, true, inline);
            var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
            if (failure is null && Environment.GetEnvironmentVariable("GNOU_GO_V11_EXAMPLES") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, inline ? "multiple-inputs-v11.yaml" : "multiple-inputs-v10.yaml"), yaml);
            }
            Assert.Equal(inline ? 1 : 2, doc.Workflows["main"].Steps.Count);
            if (inline) Assert.True(doc.Workflows["main"].Steps[0].Source.ExpressionContracts!.ContainsKey(""));
            var result = await new WorkflowEngine().ExecuteAsync(doc.Workflows["main"], new JsonObject {
                ["label"] = label, ["authorized"] = authorized, ["pattern"] = "{{chosen}}" }, PlannerFixture.Ct);
            Assert.Equal(failure is null, result.Success);
            if (failure is null) Assert.Equal(label, result.Outputs!["text"]!.ToString());
            else { Assert.Equal("INPUT_VALIDATION", result.Error!.Code); Assert.Equal(failure, result.Error.Details!["location"]!.ToString()); }
        }
    }

    [Fact]
    public async Task SharedPrefixDoesNotMaterializeSingleConsumerSuffix()
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        workflow.Outputs.Add(new() { Name = "original", Value = Ref("first", "value") });
        workflow.Steps.Add(new() { Key = "render", Type = "template.render", Input = new() { Kind = "object", Members = [
            new("template", new() { Kind = "string", Text = "{{chosen}}" }), new("data", Obj("chosen", Ref("export", "chosen")))] } });
        workflow.Outputs.Add(new() { Name = "text", Value = Ref("render", "text") });
        var yaml = new PlanningGraphCompiler().Compile(graph, await Catalog(), "test", true, true, true, true, true, true, true, true, true);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        Assert.Equal(2, doc.Workflows["main"].Steps.Count);
        Assert.Equal("n_" + PlanningGraphCompiler.Fingerprint("first")[..16], doc.Workflows["main"].Steps[0].Id);
        var result = await new WorkflowEngine().ExecuteAsync(doc.Workflows["main"], new JsonObject { ["label"] = "exact", ["authorized"] = true }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("exact", result.Outputs!["original"]!.ToString()); Assert.Equal("exact", result.Outputs["text"]!.ToString());
        Assert.Contains("guard", doc.Workflows["main"].Steps[1].Source.ExpressionContracts!.ToJsonString());
    }

    [Fact]
    public async Task MultipleMcpFieldsRetainProtectedLiteralEnvelopeAndFailureOrder()
    {
        var calls = 0; var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "publish", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"},"other":{"type":"string"},"fixed":{"type":"string"}},"required":["text","other","fixed"],"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"done":{"type":"boolean"}},"required":["done"],"additionalProperties":false}""") }],
            ToolHandlers = new() { ["publish"] = _ => { calls++; return new() { Content = new JsonObject { ["done"] = true } }; } } });
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        var capability = catalog.Capabilities.Single(c => c.Method == "publish");
        capability.RequestBindings.Add(new("/fixed", JsonValue.Create("host")));
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        workflow.Steps.Add(new() { Key = "publish", Type = "mcp.call", CapabilityId = capability.Id,
            Input = Obj("request", new() { Kind = "object", Members = [new("text", Ref("export", "chosen")), new("other", new() { Kind = "input", Source = "label" })] }) });
        var yaml = new PlanningGraphCompiler().Compile(graph, catalog, "test", true, true, true, true, true, true, true, true, true);
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        // One shared expression would conceal the host-owned /fixed field.
        // Retain preparation instead of weakening literal-binding validation.
        Assert.Equal(2, doc.Workflows["main"].Steps.Count);
        Assert.Equal("host", doc.Workflows["main"].Steps[^1].Source.Input!["request"]!["fixed"]!.ToString());
        var failed = await engine.ExecuteAsync(doc.Workflows["main"], new JsonObject { ["label"] = "", ["authorized"] = false }, PlannerFixture.Ct);
        Assert.False(failed.Success); Assert.Equal(0, calls); Assert.Equal("first", failed.Error!.Details!["location"]!.ToString());
    }
}
