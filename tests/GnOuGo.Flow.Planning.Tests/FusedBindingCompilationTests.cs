using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FusedBindingCompilationTests(Xunit.ITestOutputHelper output)
{
    private static PlanningValue Ref(string source, params string[] path) => new() { Kind = "output", Source = source, Path = [.. path] };
    private static PlanningValue Obj(string field, PlanningValue value) => new() { Kind = "object", Members = [new(field, value)] };
    private static PlanningValue Project(PlanningValue value) => new() { Kind = "projection", Members = [new("value", value), new("paths", new() { Kind = "array", Items = [new() { Kind = "array" }] })] };
    private static PlanningSchema Schema(string text) => new() { Contract = JsonNode.Parse(text)!.AsObject() };
    private static PlanningGraph Graph() => new() { Workflows = [new() {
        Inputs = [new() { Name = "label" }, new() { Name = "authorized", Schema = new() { Type = "boolean" } }],
        Steps = [
            new() { Key = "first", Input = Project(new() { Kind = "input", Source = "label" }), OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"string","minLength":1}},"required":["value"],"additionalProperties":false}""") },
            new() { Key = "guard", Input = Project(new() { Kind = "input", Source = "authorized" }), OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"boolean","enum":[true]}},"required":["value"],"additionalProperties":false}""") },
            new() { Key = "assembly", Input = Obj("chosen", Ref("first", "value")), OutputSchema = Schema("""{"type":"object","properties":{"chosen":{"type":"string"}},"required":["chosen"],"additionalProperties":false}""") },
            new() { Key = "export", Input = Obj("chosen", Ref("assembly", "chosen")), OutputSchema = Schema("""{"type":"object","properties":{"chosen":{"type":"string"}},"required":["chosen"],"additionalProperties":false}""") }
        ], Outputs = [new() { Name = "chosen", Value = Ref("export", "chosen") }]
    }] };
    private static async Task<PlanningCatalog> Catalog() => await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask)
        .DiscoverAsync(new(), PlannerFixture.Ct);

    [Theory]
    [InlineData("observed", true, null)]
    [InlineData("", false, "first")]
    [InlineData("observed", false, "guard")]
    public async Task FusionPreservesIntermediateChecksOrderAndAtomicPublication(string label, bool authorized, string? failure)
    {
        var graph = Graph(); var catalog = await Catalog(); var compiler = new PlanningGraphCompiler();
        var previous = compiler.Compile(graph, catalog, "test", true);
        var fused = compiler.Compile(graph, catalog, "test", true, true);
        var before = new WorkflowCompiler().Compile(WorkflowParser.Parse(previous));
        var after = new WorkflowCompiler().Compile(WorkflowParser.Parse(fused));
        Assert.Equal(4, before.Workflows["main"].Steps.Count); Assert.Single(after.Workflows["main"].Steps);
        Assert.True(fused.Length < previous.Length, $"before={previous.Length}, after={fused.Length}");
        Assert.Equal(previous, compiler.Compile(graph, catalog, "test", true));
        foreach (var document in new[] { before, after })
        {
            var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject() { ["label"] = label, ["authorized"] = authorized }, PlannerFixture.Ct);
            Assert.Equal(failure is null, result.Success);
            if (failure is null) Assert.Equal(label, result.Outputs!["chosen"]!.ToString());
            else
            {
                Assert.Null(result.Outputs); Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
                if (document == after) Assert.Equal(failure, result.Error.Details?["location"]?.ToString());
            }
        }
        output.WriteLine($"sets=4→1; yaml_chars={previous.Length}→{fused.Length}; mergeable_pairs=0");
    }

    [Theory]
    [InlineData("presence")][InlineData("failure_preservation")][InlineData("retry")][InlineData("branch")]
    public async Task ObservableIdentitiesAndBoundariesRemainSeparate(string reason)
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        if (reason == "presence") workflow.Steps[1].If = new() { Kind = "present", Source = "first" };
        if (reason == "failure_preservation") workflow.Finally.Add(new() { Key = "preserve", If = new() { Kind = "present", Source = "first" }, Input = Obj("saved", Ref("first", "value")) });
        if (reason == "retry") workflow.Steps[0].Retry = new() { Max = 1 };
        if (reason == "branch") workflow.Steps.Insert(1, new() { Key = "boundary", Type = "switch", Expr = new() { Kind = "boolean", Boolean = true }, Cases = [new("true", null, [])], Default = [] });
        var yaml = new PlanningGraphCompiler().Compile(graph, await Catalog(), "test", true, true);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        Assert.True(document.Workflows["main"].Steps.Count > 1);
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject() { ["label"] = "original", ["authorized"] = reason != "failure_preservation" }, PlannerFixture.Ct);
        if (reason == "failure_preservation")
        { Assert.False(result.Success); Assert.Contains(result.StepResults, r => r.Output?["saved"]?.ToString() == "original"); }
        else { Assert.True(result.Success, result.Error?.Message); Assert.Equal("original", result.Outputs!["chosen"]?.ToString()); }
    }

    [Fact]
    public async Task SharedConsumersRetainOnlyTheirCheckedIntermediateFields()
    {
        var graph = Graph();
        graph.Workflows[0].Outputs.Add(new() { Name = "original", Value = Ref("first", "value") });
        var yaml = new PlanningGraphCompiler().Compile(graph, await Catalog(), "test", true, true);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        Assert.Single(document.Workflows["main"].Steps);
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"],
            new JsonObject { ["label"] = "same", ["authorized"] = true }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("same", result.Outputs!["original"]!.ToString());
        Assert.Equal("same", result.Outputs["chosen"]!.ToString());
        var published = Assert.Single(result.StepResults).Output!.AsObject();
        Assert.Equal(new[] { "v0", "v3" }, published.Select(p => p.Key));
    }

    [Fact]
    public void CheckedSequenceSharesLimitsAndPreservesExactJson()
    {
        const string program = "(()=>{const a=({value:data.inputs.value});const b=({value:a.value});return ({value:b.value});})()";
        var contracts = new JsonObject { ["a"] = new JsonObject(), ["b"] = new JsonObject() };
        var expression = "checkedMapping(" + JsonValue.Create(program)!.ToJsonString() + ",data," + contracts.ToJsonString() + ")";
        var source = JsonNode.Parse("""{"inputs":{"value":[7922816251426433759354395033.5,null,["same","same"]]}}""");
        var result = new ExpressionEvaluator().Evaluate(expression, source);
        Assert.Equal(source!["inputs"]!["value"]!.ToJsonString(), result!["value"]!.ToJsonString());
        var failure = Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator(null, 4, TimeSpan.FromSeconds(15)).Evaluate(expression, source));
        Assert.Equal("statements", failure.Details?["exhausted_resource"]?.ToString());
        Assert.Throws<WorkflowRuntimeException>(() => new GnOuGo.Flow.Core.Scripting.JintSandbox().ExecuteMapping(expression, source, PlannerFixture.Ct));
    }
}
