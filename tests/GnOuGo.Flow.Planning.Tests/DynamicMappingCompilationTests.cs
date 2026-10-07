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
    [Fact]
    public async Task AdaptiveProfileIsCompilerOwnedReviewedAndFingerprintRelevant()
    {
        var plan = EachPlan();
        var catalog = await new WorkflowPlanningRuntime(new(), (_, _) => Task.CompletedTask).DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var request = new PlanningRequest { Options = new() { ["mapping_profile"] = TaskPlanCompiler.AdaptiveMappingProfile } };
        var compiler = new TaskPlanCompiler();
        var historical = compiler.Compile(plan, catalog);
        var current = compiler.Compile(plan, catalog, request);
        Assert.Empty(current.Diagnostics); Assert.Empty(PlanningExecutableValidation.Validate(current.Graph!, catalog));
        var yaml = new PlanningGraphCompiler().Compile(current.Graph!, catalog);
        Assert.Contains("adaptive_each: true", yaml);
        Assert.DoesNotContain("adaptive_each", new PlanningGraphCompiler().Compile(historical.Graph!, catalog));
        Assert.DoesNotContain("adaptive_each", JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan));
        var session = new PlanningSession { Plan = plan, Graph = current.Graph, Request = request, Catalog = catalog };
        Assert.DoesNotContain("two-attempt allowance", PlanningSchemas.FullProposal(session, compact: false).ToJsonString());
        Assert.Contains("two-attempt allowance", PlanningSchemas.FullProposal(new() { Catalog = catalog }, compact: false).ToJsonString());
        Assert.Contains(PlanningReviewFormatter.Operations(session), v => v.Description.Contains("per-item inference", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("multiple_fields")]
    [InlineData("scalar_field")]
    [InlineData("nullable_array")]
    public void IndependentGenerationSchemaRejectsInvalidResultShapes(string variant)
    {
        var root = PlanningSchemas.FullProposal(new() { Catalog = new() }, compact: false);
        var schema = PlanningSchemas.Ref("task"); schema["$defs"] = root["$defs"]!.DeepClone();
        var task = JsonNode.Parse("""
            {"id":"extract","kind":"transform","objective":"Extract one value per observation","dependsOn":[],"requires":null,
             "mode":"extract","each":{"input":"observations","output":"rows"},
             "inputs":[{"name":"observations","value":{"kind":"input","source":"records"}}],
             "resultType":{"kind":"object","fields":[{"name":"rows","type":{"kind":"array","items":{"kind":"string"}}}]}}
            """)!.AsObject();
        Assert.Empty(PlanningContractValidation.ValidateInstance(task, schema));
        var fields = task["resultType"]!["fields"]!.AsArray();
        if (variant == "multiple_fields") fields.Add(JsonNode.Parse("""{"name":"extra","type":{"kind":"string"}}"""));
        if (variant == "scalar_field") fields[0]!["type"] = JsonNode.Parse("""{"kind":"string"}""");
        if (variant == "nullable_array") fields[0]!["type"]!["nullable"] = true;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(task, schema));
        task.Remove("each");
        Assert.Empty(PlanningContractValidation.ValidateInstance(task, schema)); // Historical extraction is unchanged.
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task IndependentExtractionKeepsBusinessDeclarationAndTypedInputsNeedNoInference(bool typed, bool adaptive)
    {
        var plan = EachPlan();
        if (typed) plan.Inputs[0].Type.Items = new() { Kind = "string" };
        var model = new EachModel(); var engine = new WorkflowEngine { LLMUsageBudget = new(new() { MaxCalls = 2 }), LLMClient = model, LlmDefaults = new() { Model = "fixture" } };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var catalog = await runtime.DiscoverAsync(new() { Policy = new() { RequireExternalConfirmation = false } }, PlannerFixture.Ct);
        var compiled = adaptive ? new TaskPlanCompiler().Compile(plan, catalog, new PlanningRequest { Options = new() { ["mapping_profile"] = TaskPlanCompiler.AdaptiveMappingProfile } }) : new TaskPlanCompiler().Compile(plan, catalog);
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
