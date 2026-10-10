using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PhysicalProjectionCompilationTests(Xunit.ITestOutputHelper output)
{
    private static PlanningValue Ref(string source, params string[] path) => new() { Kind = "output", Source = source, Path = [.. path] };
    private static PlanningValue Input(string name) => new() { Kind = "input", Source = name };
    private static PlanningValue Obj(string name, PlanningValue value) => new() { Kind = "object", Members = [new(name, value)] };
    private static PlanningSchema Schema(string text) => new() { Contract = JsonNode.Parse(text)!.AsObject() };
    private static string Id(string key) => "n_" + PlanningGraphCompiler.Fingerprint(key)[..16];
    private static PlanningValue Select(PlanningValue source, params string[][] paths) => new() { Kind = "projection", Members = [
        new("value", source), new("paths", new() { Kind = "array", Items = paths.Select(p => new PlanningValue { Kind = "array",
            Items = p.Select(s => new PlanningValue { Kind = "string", Text = s }).ToList() }).ToList() })] };
    private static PlanningGraph Graph() => new() { Workflows = [new() {
        Inputs = [new() { Name = "route", Schema = new() { Type = "boolean" } },
            new() { Name = "signal", Schema = new() { Type = "boolean" } }],
        Steps = [new() { Key = "decision", Type = "switch", Expr = Input("route"), Cases = [new("true", null,
            [new() { Key = "inside", Type = "sequence", Steps = [new() { Key = "left", Input = Obj("flag", Input("signal")) }] }])],
            Default = [new() { Key = "right", Input = Obj("flag", Input("signal")) }] },
            new() { Key = "checked", Input = Select(Ref("decision"), ["inside", "left", "flag"], ["right", "flag"]),
                OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}""") }],
        Outputs = [new() { Name = "accepted", Schema = new() { Type = "boolean" }, Value = Ref("checked", "value") }]
    }] };
    private static async Task<PlanningCatalog> Catalog() => await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask)
        .DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
    private static string Compile(PlanningGraph graph, PlanningCatalog catalog, bool direct)
        => new PlanningGraphCompiler().Compile(graph, catalog, "projection", true, true, true, direct);
    private static string Selection(string yaml) => WorkflowParser.Parse(yaml).Workflows["main"].Steps.Single(s => s.Id == Id("checked")).Input!.GetValue<string>()[2..^1];
    private static JsonObject Context(JsonNode? first, bool includeFirst, JsonNode? second, int characters = 0)
    {
        var branch = new JsonObject { [Id("right")] = new JsonObject { ["flag"] = second?.DeepClone() },
            ["observations"] = new JsonArray(Enumerable.Range(0, 8).Select(_ => (JsonNode)new JsonObject { ["body"] = new string('x', characters) }).ToArray()) };
        if (includeFirst) branch[Id("inside")] = new JsonObject { [Id("left")] = new JsonObject { ["flag"] = first?.DeepClone() } };
        return new JsonObject { ["steps"] = new JsonObject { [Id("decision")] = branch } };
    }

    [Fact]
    public async Task NativeProfileUsesReadableCheckedPropertyAccessAndHistoricalV6IsExact()
    {
        var graph = Graph(); var catalog = await Catalog(); var compiler = new PlanningGraphCompiler();
        graph.Workflows[0].Steps[1].Input = Select(Ref("decision"), ["inside", "left", "flag"]);
        var historical = Compile(graph, catalog, true);
        var request = new PlanningRequest { Options = new() { ["compilation_profile"] = "compact-bindings-v6" } };
        Assert.False(TaskPlanCompiler.UsesNativeMappings(request));
        Assert.Equal(historical, compiler.Compile(graph, catalog, "projection", true, true, true,
            TaskPlanCompiler.UsesDirectProjections(request), TaskPlanCompiler.UsesNativeMappings(request)));
        var current = compiler.Compile(graph, catalog, "projection", true, true, true, true, true);
        Assert.Contains("input: |", current);
        var expression = Selection(current);
        Assert.Contains("`\n", expression); Assert.DoesNotContain("m.select", expression);
        var evaluator = new ExpressionEvaluator();
        foreach (var value in new[] { "true", "null", "\"invalid\"" })
        {
            var context = Context(JsonNode.Parse(value), true, null, 150000);
            Assert.Equal(evaluator.Evaluate(Selection(historical), context)!.ToJsonString(), evaluator.Evaluate(expression, context)!.ToJsonString());
        }
        Assert.Throws<WorkflowRuntimeException>(() => evaluator.Evaluate(expression, Context(null, false, null)));
        graph.Workflows[0].Steps[1].Input = Select(Ref("decision"), ["inside", "left", "flag"], ["right", "flag"]);
        var alternatives = compiler.Compile(graph, catalog, "projection", true, true, true, true, true);
        Assert.Contains("m.select", Selection(alternatives));
        Assert.Null(evaluator.Evaluate(Selection(alternatives), Context(null, true, JsonValue.Create(true)))!["value"]);
        output.WriteLine($"v6_yaml_bytes={Encoding.UTF8.GetByteCount(historical)}; v7_yaml_bytes={Encoding.UTF8.GetByteCount(current)}; selection_helpers=1→0");
    }

    [Theory]
    [InlineData(null, false, false, false)]
    [InlineData("compact-bindings-v1", false, false, false)]
    [InlineData("compact-bindings-v2", true, false, false)]
    [InlineData("compact-bindings-v3", true, false, false)]
    [InlineData("compact-bindings-v4", true, true, false)]
    [InlineData("compact-bindings-v5", true, true, true)]
    public async Task HistoricalOptionsRetainPreviousLowering(string? profile, bool descriptions, bool fusion, bool consumers)
    {
        var request = new PlanningRequest();
        if (profile is not null) request.Options["compilation_profile"] = profile;
        var graph = Graph(); var catalog = await Catalog(); var compiler = new PlanningGraphCompiler();
        var previous = compiler.Compile(graph, catalog, "projection", descriptions, fusion, consumers);
        var selected = compiler.Compile(graph, catalog, "projection", TaskPlanCompiler.UsesNormalExports(request),
            TaskPlanCompiler.UsesFusedBindings(request), TaskPlanCompiler.UsesConsumerBindings(request), TaskPlanCompiler.UsesDirectProjections(request));
        Assert.Equal(previous, selected);
        Assert.Contains("Object.fromEntries", selected);
        Assert.False(TaskPlanCompiler.UsesDirectProjections(request));
    }

    [Fact]
    public async Task ScalarSelectionDoesNotImportEightUnrelatedLargeObservations()
    {
        var graph = Graph(); var catalog = await Catalog();
        var old = Compile(graph, catalog, false); var current = Compile(graph, catalog, true);
        var oldExpression = Selection(old); var expression = Selection(current);
        Assert.Contains("Object.fromEntries", oldExpression);
        Assert.DoesNotContain("Object.fromEntries", expression);
        Assert.True(Encoding.UTF8.GetByteCount(current) < Encoding.UTF8.GetByteCount(old));
        var evaluator = new ExpressionEvaluator();
        evaluator.Evaluate(expression, Context(JsonValue.Create(true), true, JsonValue.Create(false)));
        long small = 0;
        foreach (var size in new[] { 0, 200_000, 1_250_000 })
        {
            var context = Context(JsonValue.Create(true), true, JsonValue.Create(false), size);
            var original = PlanningGraphCompiler.Fingerprint(context.ToJsonString());
            var start = GC.GetAllocatedBytesForCurrentThread();
            var result = evaluator.Evaluate(expression, context);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(result!["value"]!.GetValue<bool>());
            Assert.Equal(original, PlanningGraphCompiler.Fingerprint(context.ToJsonString()));
            if (size == 0) small = allocated;
            Assert.InRange(allocated, 0, small + 16_384);
            output.WriteLine($"unrelated_chars={8L * size}; scalar_allocation_bytes={allocated}; result_bytes={Encoding.UTF8.GetByteCount(result.ToJsonString())}");
            if (size == 1_250_000)
                Assert.Equal("CONTRACT_UNSATISFIED", Assert.Throws<WorkflowRuntimeException>(() => evaluator.Evaluate(oldExpression, context)).Code);
        }
        output.WriteLine($"yaml_bytes={Encoding.UTF8.GetByteCount(old)}→{Encoding.UTF8.GetByteCount(current)}; expression_bytes={Encoding.UTF8.GetByteCount(oldExpression)}→{Encoding.UTF8.GetByteCount(expression)}");
        Assert.Equal(old, Compile(graph, catalog, false));
    }

    [Theory]
    [InlineData("true", true, "false", "true")]
    [InlineData("null", true, "true", "null")]
    [InlineData("\"invalid\"", true, "true", "\"invalid\"")]
    [InlineData("false", false, "true", "true")]
    public async Task FirstPresentPathsNeverSkipNullOrInvalidValues(string first, bool present, string second, string expected)
    {
        var graph = Graph(); var catalog = await Catalog(); var evaluator = new ExpressionEvaluator();
        var context = Context(JsonNode.Parse(first), present, JsonNode.Parse(second));
        foreach (var direct in new[] { false, true })
            Assert.Equal(expected, evaluator.Evaluate(Selection(Compile(graph, catalog, direct)), context)!["value"]?.ToJsonString() ?? "null");
        context["steps"]![Id("decision")]!.AsObject().Remove(Id("inside"));
        context["steps"]![Id("decision")]!.AsObject().Remove(Id("right"));
        foreach (var direct in new[] { false, true })
            Assert.Equal("CONTRACT_UNSATISFIED", Assert.Throws<WorkflowRuntimeException>(() => evaluator.Evaluate(Selection(Compile(graph, catalog, direct)), context)).Code);
    }

    [Fact]
    public async Task PrefixPathsResolveNestedIdentitiesAndWholeContainersKeepLogicalKeys()
    {
        var graph = Graph(); var catalog = await Catalog();
        graph.Workflows[0].Steps[0] = new() { Key = "decision", Type = "sequence", Steps = graph.Workflows[0].Steps[0].Cases[0].Steps };
        graph.Workflows[0].Steps[1].Input = Select(Ref("decision", "inside"), ["left", "flag"]);
        var expression = Selection(Compile(graph, catalog, true));
        Assert.DoesNotContain("Object.fromEntries", expression);
        Assert.True(new ExpressionEvaluator().Evaluate(expression, Context(JsonValue.Create(true), true, null))!["value"]!.GetValue<bool>());
        graph.Workflows[0].Steps[1].Input = Select(Ref("decision"), ["inside"]);
        graph.Workflows[0].Steps[1].OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"object"}},"required":["value"]}""");
        graph.Workflows[0].Outputs.Clear();
        var old = Compile(graph, catalog, false); var current = Compile(graph, catalog, true);
        Assert.Equal(old, current);
        var result = new ExpressionEvaluator().Evaluate(Selection(current), Context(JsonValue.Create(true), true, null));
        Assert.True(result!["value"]!["left"]!["flag"]!.GetValue<bool>());
        Assert.Null(result["value"]![Id("left")]);
    }

    [Theory]
    [InlineData(true, true)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(false, false)]
    public async Task RealFlowRetainsBranchesAssertionsCleanupAndDurableInputs(bool route, bool allowed)
    {
        var requests = new List<JsonNode?>(); var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("arbitrary", new() { Tools = [new() { Name = "consume", EffectKind = "none",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}""") }],
            ToolHandlers = new() { ["consume"] = request => { requests.Add(request?.DeepClone()); return new() { Content = new JsonObject { ["accepted"] = true } }; } } });
        var store = new InMemoryWorkflowRunStore();
        var engine = new WorkflowEngine { McpClientFactory = factory, RunStore = store, Limits = new() { TenantId = "tenant", RunId = "scalar" } };
        var catalog = await TaskPlanCompilerTests.Catalog(new(engine, (_, _) => Task.CompletedTask));
        var graph = Graph(); graph.Workflows[0].Outputs.Clear();
        graph.Workflows[0].Steps.Add(new() { Key = "guard", Input = Select(Ref("checked", "value"), Array.Empty<string>()),
            OutputSchema = Schema("""{"type":"object","properties":{"value":{"type":"boolean","enum":[true]}},"required":["value"],"additionalProperties":false}""") });
        graph.Workflows[0].Steps.Add(new() { Key = "consume", Type = "mcp.call", CapabilityId = catalog.Capabilities.Single(c => c.Method == "consume").Id,
            Input = Obj("request", Obj("value", Ref("guard", "value"))) });
        graph.Workflows[0].Finally.Add(new() { Key = "cleanup", Input = Obj("closed", new() { Kind = "boolean", Boolean = true }) });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(Compile(graph, catalog, true)));
        var result = await engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject { ["route"] = route, ["signal"] = allowed }, PlannerFixture.Ct);
        Assert.Equal(allowed, result.Success); Assert.Equal(allowed ? 1 : 0, requests.Count);
        Assert.Contains(result.StepResults, r => r.Output?["closed"]?.GetValue<bool>() == true);
        if (!allowed) { Assert.Equal("INPUT_VALIDATION", result.Error!.Code); Assert.Equal("guard", result.Error.Details?["location"]?.ToString()); }
        var saved = (await store.ReadAsync("tenant", "scalar", PlannerFixture.Ct))!;
        Assert.True(saved.FinalizationCompleted); Assert.Equal(1, saved.FinalizationStepsStarted);
        Assert.DoesNotContain(saved.Invocations.Values, i => i.Status == "needs_reconciliation");
        var recovered = await new WorkflowEngine { McpClientFactory = factory, RunStore = store }.ResumeAsync("tenant", "scalar", saved.Revision, compiled.Workflows["main"], PlannerFixture.Ct);
        Assert.Equal(allowed, recovered.Success); Assert.Equal(allowed ? 1 : 0, requests.Count);
    }
}
