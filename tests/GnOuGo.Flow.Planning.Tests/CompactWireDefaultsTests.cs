using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CompactWireDefaultsTests
{
    [Fact]
    public async Task OmittedRepresentationConstantsRetainCompilationAndApprovalMaterial()
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        var schema = PlanningSchemas.Proposal(state);
        var json = JsonNode.Parse("""
            {"discoveryRequests":null,"plan":{"inputs":[{"name":"text","type":{"kind":"string"}}],"groups":[],"choices":[],
            "root":{"tasks":[{"id":"interpret","kind":"transform","objective":"Interpret the supplied text","dependsOn":[],
            "inputs":[{"name":"text","value":{"kind":"input","source":"text"}}],
            "resultType":{"kind":"object","fields":[{"name":"items","type":{"kind":"array","items":{"kind":"string","nullable":true}}}]}}],
            "always":[],"outputs":[{"name":"items","value":{"kind":"output","source":"interpret","port":"items"}}]}}}
            """)!;
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        Assert.Empty(PlanningContractValidation.ValidateInstance(json, schema));
        Assert.True(JsonNode.DeepEquals(json, PlanningCorpus.Transport(json, schema, schema)));
        var plan = json["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        Assert.True(plan.Inputs[0].Required); Assert.Null(plan.Inputs[0].Default); Assert.False(plan.Inputs[0].Type.Nullable);
        var clone = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.TaskPlan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        var catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        var compiler = new TaskPlanCompiler(); var a = compiler.Compile(plan, catalog); var b = compiler.Compile(clone, catalog);
        Assert.Empty(a.Diagnostics); Assert.Empty(b.Diagnostics);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a.Graph, PlanningJsonContext.Default.PlanningGraph), JsonSerializer.SerializeToNode(b.Graph, PlanningJsonContext.Default.PlanningGraph)));
        var yaml = new PlanningGraphCompiler().Compile(a.Graph!, catalog);
        Assert.Equal(yaml, new PlanningGraphCompiler().Compile(b.Graph!, catalog));
        state.Plan = plan; state.Graph = a.Graph; state.Catalog = catalog; state.Yaml = yaml;
        var hash = state.ComputeArtifactHash();
        state.Plan = PlanningJsonTransport.TaskPlanPrompt(plan)!.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        Assert.Equal(hash, state.ComputeArtifactHash());
        state.Plan.Root.Tasks[0].ResultType!.Fields[0].Type.Items!.Nullable = false;
        Assert.NotEqual(hash, state.ComputeArtifactHash());
    }

    [Fact]
    public void OptionalObjectFieldsAndWorkflowInputsKeepDifferentDefaultRules()
    {
        var schema = PlanningSchemas.Proposal(PlannerFixture.Session());
        var input = JsonNode.Parse("""{"name":"value","type":{"kind":"string","nullable":true},"required":false}""")!;
        IReadOnlyList<string> Errors(string type) { var contract = PlanningSchemas.Ref(type); contract["$defs"] = schema["$defs"]!.DeepClone(); return PlanningContractValidation.ValidateInstance(input, contract); }
        Assert.Empty(Errors("field")); Assert.NotEmpty(Errors("input"));
        input["default"] = new JsonObject { ["kind"] = "null" };
        Assert.Empty(Errors("input")); Assert.Empty(Errors("field"));
        var parsed = new JsonObject { ["inputs"] = new JsonArray(input.DeepClone()) }.Deserialize(PlanningJsonContext.Default.TaskPlan)!.Inputs[0];
        Assert.NotNull(parsed.Default); Assert.Equal("null", parsed.Default.Kind); Assert.False(parsed.Required);
        input["default"] = new JsonObject { ["kind"] = "input", ["source"] = "elsewhere" };
        Assert.NotEmpty(Errors("input")); Assert.NotEmpty(Errors("field"));
        input["default"] = null; Assert.NotEmpty(Errors("input"));
    }

    [Fact]
    public async Task InvalidDefaultsAndNullableIterationRemainDiagnosedWithoutRewritingInputs()
    {
        var state = PlannerFixture.Session(); state.Requirements = PlannerFixture.Requirements();
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Inputs = [
            new() { Name = "active", Type = new() { Kind = "boolean" }, Default = new() { Kind = "null" } },
            new() { Name = "entries", Type = new() { Kind = "array", Nullable = true, Items = new() { Kind = "string" } } }
        ], Root = new() { Tasks = [new() { Id = "iterate", Kind = "foreach", Objective = "Retain each supplied entry",
            Items = new() { Kind = "input", Source = "entries" }, MaxItems = 4, MaxConcurrency = 1,
            Body = new() { Outputs = [new("entry", new() { Kind = "item" })] } }],
            Outputs = [new("result", new() { Kind = "output", Source = "iterate", Port = "entry" })] } };
        var compiler = new TaskPlanCompiler();
        state.Diagnostics = compiler.Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_DEFAULT_INVALID" && d.Location == "/inputs/active");
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_ITEMS_INVALID" && d.Location == "/tasks/iterate/items");
        state.RevisionScope = TaskPlanRevisions.Scope(state.Plan, state.Diagnostics).ToList();
        Assert.Equal(new[] { "/inputs/active", "/inputs/entries/type/nullable", "/tasks/iterate/items" }, state.RevisionScope);
        Assert.Single(RepairPatchTests.Slots(state), s => s.Location == "/inputs/entries/type/nullable");
        // Requiredness/default omission and nullability are separate business decisions.
        // A diagnosed nullable leaf requires an explicit edit; other input fields remain fixed.
        state.Plan.Inputs[0].Default = null; state.Plan.Inputs[1].Type.Nullable = false;
        var result = compiler.Compile(state.Plan, state.Catalog); Assert.Empty(result.Diagnostics);
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(
            GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(new PlanningGraphCompiler().Compile(result.Graph!, state.Catalog)));
        var workflow = document.Workflows[document.Entrypoint!]; var engine = new GnOuGo.Flow.Core.Runtime.WorkflowEngine();
        var values = new JsonArray("b", "a", "b");
        var successful = await engine.ExecuteAsync(workflow, new JsonObject { ["active"] = true, ["entries"] = values.DeepClone() }, PlannerFixture.Ct);
        Assert.True(successful.Success); Assert.True(JsonNode.DeepEquals(values, successful.Outputs!["result"]));
        foreach (var invalid in new[] { new JsonObject { ["active"] = true }, new JsonObject { ["active"] = true, ["entries"] = null }, new JsonObject { ["entries"] = values.DeepClone() } })
        {
            var failed = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => engine.ExecuteAsync(workflow, invalid, PlannerFixture.Ct));
            Assert.Equal("INPUT_VALIDATION", failed.Code);
        }
    }

    [Theory]
    [InlineData("local")]
    [InlineData("conditional")]
    [InlineData("nullable_defaults")]
    [InlineData("collections")]
    [InlineData("read_transform")]
    [InlineData("protected_cleanup")]
    [InlineData("review_french")]
    [InlineData("review_distractors")]
    public async Task PromptCompactionRoundTripsEveryBusinessScenario(string scenario)
    {
        var environment = new PlanningBenchmarkCases.Environment(scenario);
        var engine = new GnOuGo.Flow.Core.Runtime.WorkflowEngine { McpClientFactory = environment.Factory() };
        var catalog = await TaskPlanCompilerTests.Catalog(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask));
        var plan = PlanningCorpus.Tasks(scenario, catalog);
        var before = JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan);
        var compact = PlanningJsonTransport.TaskPlanPrompt(plan)!;
        var after = compact.Deserialize(PlanningJsonContext.Default.TaskPlan)!;
        Assert.Equal(before, JsonSerializer.Serialize(after, PlanningJsonContext.Default.TaskPlan));
        var schema = PlanningSchemas.Proposal(PlannerFixture.Session());
        var wire = TestRuntime.Response(new() { StructuredOutputSchema = schema }, new() { Plan = plan, Requirements = PlannerFixture.Requirements() }).Json!;
        Assert.Empty(PlanningContractValidation.ValidateInstance(wire, schema));
        Assert.Equal(before, JsonSerializer.Serialize(wire["plan"]!.Deserialize(PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan));
    }
}
