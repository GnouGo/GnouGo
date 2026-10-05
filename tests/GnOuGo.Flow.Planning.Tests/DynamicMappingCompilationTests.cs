using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DynamicMappingCompilationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentExtractionKeepsBusinessDeclarationAndTypedInputsNeedNoInference(bool typed)
    {
        var plan = EachPlan();
        if (typed) plan.Inputs[0].Type.Items = new() { Kind = "string" };
        var model = new EachModel(); var engine = new WorkflowEngine { LLMClient = model, LlmDefaults = new() { Model = "fixture" } };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compiled.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
        var serialized = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        Assert.DoesNotContain("mapping.dynamic", serialized); Assert.DoesNotContain("script", serialized);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph!, catalog);
        var result = await engine.ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"],
            JsonNode.Parse(typed ? "{\"records\":[\"a\",\"b\"]}" : "{\"records\":[{\"label\":\"a\"},{\"label\":\"b\"}]}")!, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(typed ? 0 : 1, model.Calls);
        Assert.Equal(new[] { "a", "b" }, result.Outputs!["rows"]!.AsArray().Select(v => v!.GetValue<string>()));
        if (!typed)
        {
            catalog.Policy.RequireExternalConfirmation = true; PlanningConfirmationGuards.Apply(compiled.Graph!, catalog);
            engine.HumanInputProvider = new PlanningCorpus.Human(false);
            var denied = await engine.ExecuteAsync(new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog))).Workflows["main"],
                JsonNode.Parse("{\"records\":[{\"label\":\"a\"}]}")!, PlannerFixture.Ct);
            Assert.False(denied.Success); Assert.Equal(1, model.Calls);
        }
    }

    [Theory]
    [InlineData("interpret")]
    [InlineData("operation")]
    [InlineData("missing_input")]
    [InlineData("missing_output")]
    [InlineData("non_array")]
    [InlineData("extra_result")]
    public async Task InvalidIndependentDeclarationsCannotReachCompilation(string variant)
    {
        var plan = EachPlan(); var task = plan.Root.Tasks[0];
        if (variant == "interpret") task.Mode = null;
        if (variant == "operation") task.Kind = "operation";
        if (variant == "missing_input") task.Each = new("missing", "rows");
        if (variant == "missing_output") task.Each = new("observations", "missing");
        if (variant == "non_array") plan.Inputs[0].Type = new();
        if (variant == "extra_result") task.ResultType!.Fields.Add(new() { Name = "extra", Type = new() });
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new(), PlannerFixture.Ct);
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Diagnostics, d => d.Code == "TASK_TRANSFORM_EACH");
    }

    [Fact]
    public void RepairCannotIntroduceOrChangeIndependentSemantics()
    {
        var baseline = EachPlan(); var revised = JsonSerializer.Deserialize(JsonSerializer.Serialize(baseline, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        revised.Root.Tasks[0].Each = null;
        Assert.Contains(TaskPlanRevisions.Validate(baseline, revised, ["/tasks/extract/each"]), d => d.Code == "REVISION_SCOPE_CHANGED");
        baseline.Root.Tasks[0].Each = null; revised.Root.Tasks[0].Each = new("observations", "rows");
        Assert.Contains(TaskPlanRevisions.Validate(baseline, revised, []), d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.DoesNotContain("\"each\"", JsonSerializer.Serialize(baseline, PlanningJsonContext.Default.TaskPlan));
    }

    private static TaskPlan EachPlan() => new()
    {
        Inputs = [new() { Name = "records", Type = new() { Kind = "array", Items = ProductTransformationPlan.Obj(("label", new() { Kind = "string" })) } }],
        Root = new() { Tasks = [new() { Id = "extract", Kind = "transform", Mode = "extract", Each = new("observations", "rows"),
            Objective = "Extract each observed label in order", Inputs = [new("observations", new() { Kind = "input", Source = "records" })],
            ResultType = ProductTransformationPlan.Obj(("rows", new() { Kind = "array", Items = new() { Kind = "string" } })) }],
            Outputs = [new("rows", new() { Kind = "output", Source = "extract", Port = "rows" })] }
    };

    private sealed class EachModel : ILLMClient, ILLMCapabilityResolver
    {
        public int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new LLMResponse { Json = JsonNode.Parse("{\"script\":\"source.observations.label\"}") }); }
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(12000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
    }
    [Theory]
    [InlineData(true, "renamed")]
    [InlineData(false, "renamed")]
    [InlineData(false, "other")]
    public async Task ExtractionIsBusinessIntentAndOnlyOpaqueAdaptationInvokesModel(bool typed, string name)
    {
        var model = new Model(name); var engine = new WorkflowEngine { LLMClient = model, LlmDefaults = new() { Model = "fixture" } };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var plan = new TaskPlan { Inputs = [new() { Name = "observation", Type = new() }], Root = new()
        {
            Tasks = [new() { Id = "extract", Kind = "transform", Mode = "extract", Objective = "Extract the observed name", Inputs = [new(typed ? name : "text", new() { Kind = "input", Source = "observation" })],
                ResultType = ProductTransformationPlan.Obj((name, new())) }], Outputs = [new("result", ProductTransformationPlan.Ref("extract"))]
        } };
        var json = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        Assert.DoesNotContain("mapping.dynamic", json); Assert.DoesNotContain("script", json);
        var compiled = new TaskPlanCompiler().Compile(plan, catalog); Assert.Empty(compiled.Diagnostics);
        var node = Assert.Single(compiled.Graph!.Workflows[0].Steps, n => n.Input.Kind is "dynamic_mapping" or "projection");
        Assert.Equal("set", node.Type); Assert.Equal(typed ? "projection" : "dynamic_mapping", node.Input.Kind);
        Assert.DoesNotContain(catalog.Capabilities, c => c.StepType == "mapping.dynamic");
        var fingerprint = PlanningGraphCompiler.Fingerprint(compiled.Graph);
        var yaml = new PlanningGraphCompiler().Compile(compiled.Graph, catalog);
        Assert.DoesNotContain("value.project", yaml);
        Assert.Equal(fingerprint, PlanningGraphCompiler.Fingerprint(compiled.Graph));
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml)).Workflows["main"];
        var result = await engine.ExecuteAsync(workflow, new JsonObject { ["observation"] = typed ? "observed" : "Name: observed" }, PlannerFixture.Ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("observed", result.Outputs!["result"]![name]!.GetValue<string>());
        Assert.Equal(typed ? 0 : 1, model.Calls);
        if (!typed)
        {
            catalog.Policy.RequireExternalConfirmation = true;
            PlanningConfirmationGuards.Apply(compiled.Graph, catalog);
            engine.HumanInputProvider = new PlanningCorpus.Human(false);
            workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(compiled.Graph, catalog))).Workflows["main"];
            var denied = await engine.ExecuteAsync(workflow, new JsonObject { ["observation"] = "Name: observed" }, PlannerFixture.Ct);
            Assert.False(denied.Success); Assert.Equal(1, model.Calls);
        }
        plan.Root.Tasks[0].Mode = null;
        Assert.DoesNotContain("\"mode\"", JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Contains(new TaskPlanCompiler().Compile(plan, catalog).Graph!.Workflows.SelectMany(w => w.Steps), n => n.Type == "llm.call");
    }
    [Fact]
    public void DeferredChecksNarrowOnlyUnknownNestedContracts()
    {
        var opaqueItems = JsonNode.Parse("{\"type\":\"array\",\"items\":{\"x-gnougo-opaque\":true}}")!.AsObject();
        var target = JsonNode.Parse("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}")!.AsObject();
        Assert.True(PlanningContractShapes.CanDefer(opaqueItems, target));
        opaqueItems["items"] = new JsonObject { ["type"] = "number" };
        Assert.False(PlanningContractShapes.CanDefer(opaqueItems, target));
        opaqueItems["items"] = PlanningContractShapes.Opaque(); opaqueItems["type"] = "object";
        Assert.False(PlanningContractShapes.CanDefer(opaqueItems, target));
    }

    private sealed class Model(string name) : ILLMClient
    {
        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({" + name + ":m.text(source.text,'Name: (.*)')})" } }); }
    }
}
