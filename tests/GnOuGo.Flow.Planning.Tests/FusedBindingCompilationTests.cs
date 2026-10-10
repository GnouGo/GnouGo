using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class FusedBindingCompilationTests(Xunit.ITestOutputHelper output)
{
    [Theory]
    [InlineData("é ` ${literal} \\ source", true, null, false)]
    [InlineData("", false, "first", false)]
    [InlineData("observed", false, "guard", false)]
    [InlineData("é ` ${literal} \\ source", true, null, true)]
    [InlineData("", false, "first", true)]
    [InlineData("observed", false, "guard", true)]
    public async Task NativeTemplateBindingsPreserveOrderedChecksAndLiteralValues(string label, bool authorized, string? failure, bool readable)
    {
        var yaml = new PlanningGraphCompiler().Compile(Graph(), await Catalog(), "test", true, true, true, true, true, readable);
        Assert.Contains("input: |", yaml, StringComparison.Ordinal);
        if (readable)
        {
            Assert.Contains("expression_contracts:", yaml); Assert.DoesNotContain("checkedMapping(", yaml);
            Assert.Contains("const chosen=", yaml); Assert.Contains("const chosen_2=", yaml);
        }
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows["main"], new JsonObject { ["label"] = label, ["authorized"] = authorized }, PlannerFixture.Ct);
        Assert.Equal(failure is null, result.Success);
        if (failure is null) Assert.Equal(label, result.Outputs!["chosen"]!.GetValue<string>());
        else { Assert.Equal("INPUT_VALIDATION", result.Error!.Code); Assert.Equal(failure, result.Error.Details!["location"]!.GetValue<string>()); }
    }

    [Theory]
    [InlineData("observed", true, null, false)]
    [InlineData("", false, "first", false)]
    [InlineData("observed", false, "guard", false)]
    [InlineData("observed", true, null, true)]
    [InlineData("", false, "first", true)]
    [InlineData("observed", false, "guard", true)]
    public async Task ConsumerBindingsCheckBeforeDispatchAndReplayDurableInputs(string label, bool authorized, string? failure, bool readable)
    {
        var requests = new List<JsonNode?>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "publish", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"},"fixed":{"type":"string"}},"required":["text","fixed"],"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"done":{"type":"boolean"}},"required":["done"],"additionalProperties":false}""") }],
            ToolHandlers = new() { ["publish"] = request => { requests.Add(request?.DeepClone()); return new() { Content = new JsonObject { ["done"] = true } }; } } });
        var store = new InMemoryWorkflowRunStore(); var engine = new WorkflowEngine { McpClientFactory = factory, RunStore = store,
            Limits = new() { TenantId = "tenant", RunId = "consumer" } };
        var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        var capability = catalog.Capabilities.Single(c => c.Method == "publish"); capability.RequestBindings.Add(new("/fixed", JsonValue.Create("host")));
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        workflow.Steps.Add(new() { Key = "publish", Type = "mcp.call", CapabilityId = capability.Id,
            Input = Obj("request", Obj("text", Ref("export", "chosen"))) });
        workflow.Finally.Add(new() { Key = "cleanup", Input = Obj("cleaned", new() { Kind = "boolean", Boolean = true }) });
        var compiler = new PlanningGraphCompiler(); var previous = compiler.Compile(graph, catalog, "test", true, true);
        var yaml = compiler.Compile(graph, catalog, "test", true, true, true, readable, readable, readable);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var step = Assert.Single(document.Workflows["main"].Steps);
        Assert.Equal("mcp.call", step.Type); Assert.Equal("host", step.Source.Input!["request"]!["fixed"]!.ToString());
        Assert.Equal("arbitrary", step.Source.Input["server"]!.ToString());
        if (readable)
        {
            Assert.DoesNotContain("checkedMapping", yaml);
            Assert.True(step.Source.ExpressionContracts!.ContainsKey("/request/text"));
        }
        else
        {
            Assert.Contains("checkedMapping", step.Source.Input["request"]!["text"]!.ToString());
            Assert.True(yaml.Length < previous.Length, $"before={previous.Length}, after={yaml.Length}");
        }
        Assert.Equal(previous, compiler.Compile(graph, catalog, "test", true, true));
        var values = new JsonObject { ["label"] = label, ["authorized"] = authorized };
        var result = await engine.ExecuteAsync(document.Workflows["main"], values, PlannerFixture.Ct);
        Assert.Equal(failure is null, result.Success);
        Assert.Contains(result.StepResults, r => r.Output?["cleaned"]?.GetValue<bool>() == true);
        if (failure is not null)
        {
            Assert.Empty(requests); Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
            Assert.Equal(failure, result.Error.Details?["location"]?.ToString());
        }
        else
        {
            Assert.Equal(label, Assert.Single(requests)!["text"]!.ToString());
            var replay = new WorkflowEngine { McpClientFactory = factory, RunStore = store, Limits = engine.Limits };
            var committed = (await store.ReadAsync("tenant", "consumer", PlannerFixture.Ct))!;
            Assert.True((await replay.ResumeAsync("tenant", "consumer", committed.Revision, document.Workflows["main"], PlannerFixture.Ct)).Success);
            Assert.Single(requests);
            var oldStore = new InMemoryWorkflowRunStore();
            var oldEngine = new WorkflowEngine { McpClientFactory = factory, RunStore = oldStore, Limits = engine.Limits };
            Assert.True((await oldEngine.ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(previous)).Workflows["main"], values, PlannerFixture.Ct)).Success);
            var oldJournal = (await oldStore.ReadAsync("tenant", "consumer", PlannerFixture.Ct))!;
            var currentJournal = (await store.ReadAsync("tenant", "consumer", PlannerFixture.Ct))!;
            Assert.True(currentJournal.Invocations.Count < oldJournal.Invocations.Count);
            int Bytes(WorkflowRun journal) => System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(journal, WorkflowRunJsonContext.Default.WorkflowRun));
            Assert.True(Bytes(currentJournal) < Bytes(oldJournal));
            output.WriteLine($"consumer v4→v5: invocations={oldJournal.Invocations.Count}→{currentJournal.Invocations.Count}; logical journal bytes={Bytes(oldJournal)}→{Bytes(currentJournal)}; yaml bytes={previous.Length}→{yaml.Length}");
        }
        var run = (await store.ReadAsync("tenant", "consumer", PlannerFixture.Ct))!;
        Assert.Equal(1, run.FinalizationStepsStarted);
        Assert.DoesNotContain(run.Invocations.Values, i => i.Status == "needs_reconciliation");
        output.WriteLine($"v4_steps=2; v5_steps=1; normal_sets=1→0; yaml={previous.Length}→{yaml.Length}; invocations={run.Invocations.Count}");
    }

    [Theory]
    [InlineData("shared")][InlineData("presence")][InlineData("conditional")][InlineData("retry")][InlineData("failure")]
    public async Task ConsumerInliningRetainsIndependentBoundaries(string boundary)
    {
        var graph = Graph(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        workflow.Steps.Add(new() { Key = "render", Type = "template.render", Input = new() { Kind = "object", Members = [
            new("template", new() { Kind = "string", Text = "{{chosen}}" }), new("data", Obj("chosen", Ref("export", "chosen")))] } });
        var consumer = workflow.Steps[^1];
        if (boundary == "shared") workflow.Outputs.Add(new() { Name = "chosen", Value = Ref("export", "chosen") });
        if (boundary == "presence") consumer.If = new() { Kind = "present", Source = "export" };
        if (boundary == "conditional") consumer.If = new() { Kind = "input", Source = "authorized" };
        if (boundary == "retry") consumer.Retry = new() { Max = 1 };
        if (boundary == "failure") workflow.Finally.Add(new() { Key = "save", If = new() { Kind = "present", Source = "export" }, Input = Obj("saved", Ref("export", "chosen")) });
        var yaml = new PlanningGraphCompiler().Compile(graph, await Catalog(), "test", true, true, true);
        Assert.True(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"].Steps.Count > 1);
    }

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
