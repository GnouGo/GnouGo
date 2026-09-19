using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class GraphExecutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task BusinessControlFlowLowersAndExecutesWithoutTechnicalIntent()
    {
        var intent = new WorkflowIntentPlan
        {
            Inputs = [new("numbers", new() { Type = "array", Items = new() { Type = "number" } }, true, new() { Kind = "array", Items = [Num(1), Num(2), Num(4)] })],
            Operations = [
                new ParallelIntentOperation { Id = "parallel", Branches = [new("left", new([], Num(2))), new("right", new([], Num(4)))] },
                new EachIntentOperation { Id = "loop", Parallel = true, Items = Ref("input", "numbers"), Body = new([
                    new CallIntentOperation { Id = "double", Flow = "double", Arguments = [new("value", Ref("item", "loop"))] }
                ], Ref("result", "double", "result")) },
                new ChooseIntentOperation { Id = "choose", Condition = new() { Kind = "boolean", Boolean = true }, Then = new([], Ref("result", "parallel", "right")), Otherwise = new([], Num(0)) },
                new CallIntentOperation { Id = "call", Flow = "double", Arguments = [new("value", Ref("result", "choose"))] }
            ],
            Outputs = [new("result", Ref("result", "call", "result")), new("items", Ref("result", "loop"))],
            Subflows = [new("double", [new("value", new() { Type = "number" })], [new CalculateIntentOperation { Id = "calculate", Value = new() { Kind = "compute", Text = "value * 2", Members = [new("value", Ref("input", "value"))] } }], [new("result", Ref("result", "calculate"))])]
        };
        var state = await PlannerFixture.RunAsync(new TestRuntime(intent));
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Equal(1, state.ModelCalls); Assert.Contains(state.Scenarios, s => s.Id.StartsWith("branch:", StringComparison.Ordinal));
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var engine = new WorkflowEngine(); var main = doc.Workflows[doc.Entrypoint!];
        var result = await engine.ExecuteAsync(main, new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("8", result.Outputs!["result"]!.ToJsonString());
        Assert.Equal("[2,4,8]", result.Outputs["items"]!.ToJsonString());
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => engine.ExecuteAsync(main, new JsonObject { ["numbers"] = null }, Ct));
    }
    [Fact]
    public async Task ArtifactHolesSelectOriginalProducerAndCatalogBindingsOverrideModelValues()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        catalog.Policy.RequireExternalConfirmation = false;
        catalog.Capabilities = [new() { Id = "create", StepType = "mcp.call", Kind = "tool", Server = "test", Method = "create", InputSchema = Schema("""{"mode":{"type":"string","enum":["create"]}}""", "mode"), OutputSchema = Schema("""{"id":{"type":"string"}}""", "id"), ArtifactContract = new(1, [new("resource", "/id", "materialize")], []) },
            new() { Id = "consume", StepType = "mcp.call", Kind = "tool", Server = "test", Method = "consume", InputSchema = Schema("""{"id":{"type":"string"},"mode":{"type":"string"}}""", "id", "mode"), OutputSchema = Schema("{}"), RequestBindings = [new("/mode", JsonValue.Create("locked"))], ArtifactContract = new(1, [], [new("resource", "/id", true)]) }];
        var intent = new WorkflowIntentPlan { Operations = [new InvokeIntentOperation { Id = "create", Capability = "create" }, new InvokeIntentOperation { Id = "consume", Capability = "consume", Arguments = [new("mode", new() { Kind = "string", Text = "untrusted" })] }] };
        var graph = PlanningGraphBuilder.Build(intent, catalog);
        var holes = PlanningHoleEligibility.Find(graph, catalog); Assert.Equal(2, holes.Count);
        var first = holes.Single(h => h.NodeKey == "create"); var dependent = holes.Single(h => h.NodeKey == "consume");
        Assert.Empty(PlanningHoleEligibility.Choices(graph, catalog, dependent));
        graph = PlanningHoleEligibility.Assign(graph, catalog, first, Assert.Single(PlanningHoleEligibility.Choices(graph, catalog, first)).Value);
        var hole = Assert.Single(PlanningHoleEligibility.Find(graph, catalog));
        graph = PlanningHoleEligibility.Assign(graph, catalog, hole, Assert.Single(PlanningHoleEligibility.Choices(graph, catalog, hole)).Value);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        var request = graph.Workflows[0].Steps[1].Input.Members[0].Value;
        Assert.Equal("locked", request.Members.Single(m => m.Name == "mode").Value.Text);
        request.Members.Single(m => m.Name == "id").Value.Kind = "string";
        request.Members.Single(m => m.Name == "id").Value.Text = "invented";
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "ARTIFACT_BINDING_INVALID");
        static JsonObject Schema(string properties, params string[] required) => new() { ["type"] = "object", ["properties"] = JsonNode.Parse(properties), ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
    }
    [Fact]
    public async Task NovelModelResultDerivesItsStructuredEnvelopeAndSamples()
    {
        var plan = new WorkflowIntentPlan { Operations = [new TransformIntentOperation { Id = "model", Instruction = "Return a number", ResultType = new() { Type = "number" } }], Outputs = [new("result", Ref("result", "model"))] };
        var runtime = new TestRuntime(plan); var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Single(runtime.Calls); Assert.All(state.Scenarios, scenario => Assert.Equal("passed", scenario.Outcome));
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var modelStep = Assert.Single(doc.Workflows[doc.Entrypoint!].Source.Steps);
        Assert.Equal("number", modelStep.Input!["structured_output"]!["schema_inline"]!["properties"]!["value"]!["type"]!.ToString());
    }
    [Fact]
    public async Task NullDefaultAndExplicitNullRemainDistinctFromOmission()
    {
        var plan = new WorkflowIntentPlan { Inputs = [new("value", new() { Nullable = true }, true, new())], Outputs = [new("result", Ref("input", "value"))] };
        var state = await PlannerFixture.RunAsync(new TestRuntime(plan));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var workflow = document.Workflows[document.Entrypoint!];
        foreach (var input in new[] { new JsonObject(), new JsonObject { ["value"] = null }, new JsonObject { ["value"] = "null" } })
        {
            var result = await new WorkflowEngine().ExecuteAsync(workflow, input, Ct);
            Assert.True(result.Success, result.Error?.Message);
            Assert.True(JsonNode.DeepEquals(input["value"], result.Outputs!["result"]));
        }
    }
    [Fact]
    public async Task SimpleRevisionImportRetainsBusinessBehaviorAndRejectsTechnicalEscapeHatches()
    {
        var graph = PlanningGraphImporter.ImportBaseline("""
            version: 1
            workflows:
              main:
                inputs:
                  name: { type: string }
                steps:
                  - id: greeting
                    type: set
                    input: { message: '${data.inputs.name}' }
                outputs:
                  message: { type: string, expr: '${data.steps.greeting.message}' }
            """);
        var intent = PlanningIntentImporter.Import(graph);
        Assert.IsType<CalculateIntentOperation>(Assert.Single(intent.Operations));
        var state = await PlannerFixture.RunAsync(new TestRuntime(intent));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        graph.Functions = "function helper() { return 1; }";
        Assert.Throws<InvalidOperationException>(() => PlanningIntentImporter.Import(graph));
    }
    [Fact]
    public async Task CatalogConstraintsAndDefaultsSurvivePortsWithoutModelSchemaCopies()
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"amount":{"type":"number","minimum":3,"maximum":10,"default":5}},"required":["amount"],"additionalProperties":false}""");
        server.Tools.Add(new() { Name = "echo", EffectKind = "read", InputSchema = schema, OutputSchema = schema, ExampleResponse = new JsonObject { ["amount"] = 5 } });
        server.ToolHandlers["echo"] = args => new() { Content = args?.DeepClone() }; factory.RegisterServer("fixture", server);
        var runtime = new TestRuntime(mcp: factory); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        runtime.Plans.Clear(); runtime.Plans.Enqueue(new() { Inputs = [new("amount")], Operations = [new InvokeIntentOperation { Id = "echo", Capability = catalog.Capabilities[0].Id, Arguments = [new("amount", Ref("input", "amount"))] }], Outputs = [new("result", Ref("result", "echo", "amount"))] });
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message))); Assert.Single(runtime.Calls);
        Assert.Null(state.IntentPlan!.Inputs[0].Type); Assert.Null(state.IntentPlan.Inputs[0].Default);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!)); var main = document.Workflows[document.Entrypoint!];
        Assert.Equal("3", main.Source.Inputs!["amount"].Schema!["minimum"]!.ToString()); Assert.Equal("10", main.Source.Outputs!["result"].Schema!["maximum"]!.ToString());
        var engine = new WorkflowEngine { McpClientFactory = factory };
        var result = await engine.ExecuteAsync(main, new JsonObject(), Ct); Assert.True(result.Success, result.Error?.Message); Assert.Equal("5", result.Outputs!["result"]!.ToString());
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => engine.ExecuteAsync(main, new JsonObject { ["amount"] = 2 }, Ct));
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => engine.ExecuteAsync(main, new JsonObject { ["amount"] = 11 }, Ct));
        var imported = PlanningGraphImporter.Import(state.Yaml!, state.Catalog!); Assert.NotNull(imported.Workflows[0].Inputs[0].Schema.CapabilityId);
    }
    [Fact]
    public async Task GeneratedScopesCannotCollideWithUserSubflowsOrUnderscoreSeparatedNames()
    {
        var intent = new WorkflowIntentPlan
        {
            Operations = [new ParallelIntentOperation { Id = "a_b", Branches = [new("c", new([], Num(1)))] }, new ParallelIntentOperation { Id = "a", Branches = [new("b_c", new([], Num(2)))] }],
            Subflows = [new("main___planning_branch_a_b_c", [], [], [new("value", Num(3))])],
            Outputs = [new("first", Ref("result", "a_b", "c")), new("second", Ref("result", "a", "b_c"))]
        };
        var state = await PlannerFixture.RunAsync(new TestRuntime(intent));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        Assert.Equal(state.Graph!.Workflows.Count, state.Graph.Workflows.Select(w => w.Key).Distinct().Count());
        Assert.Contains(state.Graph.Workflows, w => w.Key == "__planning_flow_4_main_6_branch_3_a_b_1_c");
        Assert.Contains(state.Graph.Workflows, w => w.Key == "__planning_flow_4_main_6_branch_1_a_3_b_c");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProducerBusinessFailureIsDataButTransportFailureStillStops(bool transportError)
    {
        var factory = new InMemoryMcpClientFactory(); var server = new MockMcpServerConfig();
        server.Tools.Add(new() { Name = "check", EffectKind = "read", InputSchema = JsonNode.Parse("""{"type":"object","properties":{}}"""), OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"status":{"type":"string"}},"required":["status"]}"""), ExampleResponse = new JsonObject { ["status"] = "passed" }, Meta = JsonNode.Parse("""{"gnougo":{"result":{"detect_errors":false}}}""") });
        server.ToolHandlers["check"] = _ => new() { IsError = transportError, Content = new JsonObject { ["status"] = "failed" } }; factory.RegisterServer("fixture", server);
        var runtime = new TestRuntime(mcp: factory); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        runtime.Plans.Clear(); runtime.Plans.Enqueue(new() { Operations = [new InvokeIntentOperation { Id = "check", Capability = catalog.Capabilities[0].Id }], Outputs = [new("status", Ref("result", "check", "status"))] });
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(";", state.Diagnostics.Select(d => d.Message)));
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), Ct);
        Assert.Equal(!transportError, result.Success);
        if (!transportError) Assert.Equal("failed", result.Outputs!["status"]!.ToString());
        var imported = PlanningGraphImporter.Import(state.Yaml!, state.Catalog!); Assert.Equal(catalog.Capabilities[0].Id, imported.Workflows[0].Steps[0].CapabilityId);
    }
    internal static IntentValue Ref(string kind, string source, params string[] path) => new() { Kind = kind, Source = source, Path = path.ToList() };
    internal static IntentValue Num(decimal number) => new() { Kind = "number", Number = number };
}
