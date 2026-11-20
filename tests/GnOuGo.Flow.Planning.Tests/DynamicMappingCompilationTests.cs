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
        Assert.DoesNotContain("two-attempt allowance", PlanningSchemas.TaskPlanGrammar(session).ToJsonString());
        Assert.Contains("two-attempt allowance", PlanningSchemas.TaskPlanGrammar(new() { Catalog = catalog }).ToJsonString());
        Assert.Contains(PlanningReviewFormatter.Operations(session), v => v.Description.Contains("per-item inference", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("multiple_fields")]
    [InlineData("scalar_field")]
    [InlineData("nullable_array")]
    public void IndependentGenerationSchemaRejectsInvalidResultShapes(string variant)
    {
        var root = PlanningSchemas.TaskPlanGrammar(new() { Catalog = new() });
        var schema = PlanningSchemas.Ref("task"); schema["$defs"] = root["$defs"]!.DeepClone();
        var task = JsonNode.Parse("""
            {"id":"extract","kind":"transform","objective":"Extract one value per observation","dependsOn":[],"requires":null,
             "mode":"extract","each":{"input":"observations","output":"rows"},
             "inputs":[{"name":"observations","value":{"kind":"input","source":"records"}}],
             "resultType":{"kind":"object","fields":[{"name":"rows","type":{"kind":"array","minItems":null,"maxItems":null,"items":{"kind":"string"}}}]}}
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

    [Fact]
    public async Task FreshExtractionPreservesWholePageCardinalityWhenAnArraySuppliesSelectionCriteria()
    {
        var model = new EachModel("({positions: m.indexed(source.page.records).filter(r => m.test(r.value,'^(match|other-rule)$')).map(r => r.index)})");
        var engine = new WorkflowEngine { LLMUsageBudget = new(new() { MaxCalls = 4 }), LLMClient = model, LlmDefaults = new() { Model = "fixture" } };
        var runtime = new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask);
        var state = PlannerFixture.Capture(new() { Request = new() { TenantId = "whole-page",
            Prompt = "Return the positions of all records matching the selection rules.", Policy = new() { RequireExternalConfirmation = false } } });
        var catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        var recordsType = new TaskType { Kind = "array", Items = new() { Kind = "string" } };
        var task = new PlanTask { Id = "select_positions", Kind = "transform", Mode = "extract", Objective = state.Request.Prompt,
            Inputs = [new("page", new() { Kind = "input", Source = "page" }),
                new("rules", new() { Kind = "array", Items = [new() { Kind = "string", Text = "match" }, new() { Kind = "string", Text = "other-rule" }] })],
            ResultType = ProductTransformationPlan.Obj(("positions", new() { Kind = "array", Items = new() { Kind = "integer" } })) };
        var plan = new TaskPlan { Inputs = [new() { Name = "page", Type = ProductTransformationPlan.Obj(("records", recordsType)) }],
            Root = new() { Tasks = [task], Outputs = [new("positions", new() { Kind = "output", Source = task.Id, Port = "positions" })] } };
        var legacy = new PlanningRequest { Options = state.Request.Options.DeepClone().AsObject() };
        legacy.Options["mapping_profile"] = "adaptive-each-v1";
        PlanningNode Mapping(PlanningRequest policy)
        {
            var compilation = new TaskPlanCompiler().Compile(plan, catalog, policy);
            Assert.Empty(compilation.Diagnostics);
            return Assert.Single(compilation.Graph!.Workflows[0].Steps, node => node.Input.Kind == "dynamic_mapping");
        }
        Assert.NotNull(PlanningGraphValidation.Member(Mapping(legacy).Input, "each"));
        var fresh = Mapping(state.Request);
        Assert.Null(PlanningGraphValidation.Member(fresh.Input, "each"));
        Assert.Null(PlanningGraphValidation.Member(fresh.Input, "infer_each"));
        var graph = new TaskPlanCompiler().Compile(plan, catalog, state.Request).Graph!;
        var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog))).Workflows["main"];
        foreach (var (records, expected) in new[] { (new JsonArray("none"), new JsonArray()),
            (new JsonArray("match", "none", "other-rule", "match"), new JsonArray(0, 2, 3)) })
        {
            var result = await engine.ExecuteAsync(workflow, new JsonObject { ["page"] = new JsonObject { ["records"] = records } }, PlannerFixture.Ct);
            Assert.True(result.Success, result.Error?.Message);
            Assert.True(JsonNode.DeepEquals(expected, result.Outputs!["positions"]));
        }
        task.Each = new("rules", "positions");
        Assert.NotNull(PlanningGraphValidation.Member(Mapping(state.Request).Input, "each"));
    }

    [Fact]
    public async Task RequiredTextCapturesValidateTheInputWithoutAnExtractedValidityFlag()
    {
        var state = PlannerFixture.Capture(new() { Request = new() { Prompt = "Read an entry from its supplied locator.",
            Policy = new() { RequireExternalConfirmation = false } } });
        var grammar = PlanningSchemas.TaskPlanGrammar(state).ToJsonString();
        Assert.Contains("missing required capture fails", grammar, StringComparison.Ordinal);
        var task = new PlanTask { Id = "parse", Kind = "transform", Mode = "extract", Objective = "Extract the required collection and positive entry number.",
            Inputs = [new("locator", new() { Kind = "input", Source = "locator" })],
            ResultType = ProductTransformationPlan.Obj(("collection", new() { Kind = "string" }), ("entry", new() { Kind = "integer" })) };
        var plan = new TaskPlan { Inputs = [new() { Name = "locator", Type = new() { Kind = "string" } }],
            Root = new() { Tasks = [task], Outputs = [new("entry", ProductTransformationPlan.Ref("parse", "entry"))] } };
        const string captures = "collection:m.text(source.locator,'^/([a-z]+)/[1-9][0-9]*$'),entry:m.number(m.text(source.locator,'^/[a-z]+/([1-9][0-9]*)$'))";
        async Task<GnOuGo.Flow.Core.Models.RunResult> Execute(string locator, string script)
        {
            var engine = new WorkflowEngine { LLMUsageBudget = new(new() { MaxCalls = 2 }), LLMClient = new EachModel(script),
                LlmDefaults = new() { Model = "fixture" } };
            var catalog = await new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask).DiscoverAsync(state.Request, PlannerFixture.Ct);
            var compiled = new TaskPlanCompiler().Compile(plan, catalog, state.Request);
            Assert.Empty(compiled.Diagnostics);
            Assert.Empty(PlanningExecutableValidation.Validate(compiled.Graph!, catalog));
            return await LookupCompilationTests.Execute(new PlanningGraphCompiler().Compile(compiled.Graph!, catalog),
                new JsonObject { ["locator"] = locator }, engine);
        }
        var valid = await Execute("/entries/718", "({" + captures + "})");
        Assert.True(valid.Success, valid.Error?.Message);
        Assert.Equal(718, valid.Outputs!["entry"]!.GetValue<int>());
        Assert.False((await Execute("/entries/invalid", "({" + captures + "})")).Success);
        task.ResultType.Fields.Add(new() { Name = "valid", Type = new() { Kind = "boolean" } });
        var invented = await Execute("/entries/718", "({" + captures + ",valid:m.test(source.locator,'^/entries/')})");
        Assert.False(invented.Success);
        Assert.Equal("$/valid", invented.Error!.Details!["mapping_result_path"]!.ToString());
    }

    private sealed class EachModel(string script = "source.observations.label") : ILLMClient, ILLMCapabilityResolver
    {
        public int Calls;
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = script } }); }
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

    private sealed class Model(string name) : ILLMClient, ILLMCapabilityResolver
    {
        public Task<int?> InputTokenAllowanceAsync(string? provider, string model, int outputTokens, CancellationToken ct) => Task.FromResult<int?>(128000);
        public Task<bool?> SupportsStructuredOutputAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<bool?>(true);
        public Task<IReadOnlyList<string>?> SupportedReasoningLevelsAsync(string? provider, string model, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(["medium", "low"]);

        public int Calls { get; private set; }
        public Task<LLMResponse> CallAsync(LLMRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new LLMResponse { Json = new JsonObject { ["script"] = "({" + name + ":m.text(source.text,'Name: (.*)')})" } }); }
    }
}
