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
