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
    public async Task LoopsParallelBranchesSubworkflowsAndDefaultsSurvivePlanning()
    {
        var number = new PlanningSchema { Type = "number" };
        var intent = new WorkflowIntentPlan { Summary = "Compute using nested control flow", Workflows = [new() {
            Inputs = [new() { Name = "count", Schema = new() { Type = "integer" }, Required = false, Default = new() { Kind = "number", Number = 3 } }],
            Steps = [new() { Key = "parallel", Kind = "parallel", Branches = [new([new() { Key = "left", Kind = "set", Input = Obj("value", Num(2)) }]), new([new() { Key = "right", Kind = "set", Input = Obj("value", Num(4)) }])] },
                new() { Key = "loop", Kind = "loop.sequential", Input = Obj("times", new() { Kind = "input", Source = "count" }), Steps = [new() { Key = "iteration", Kind = "set", Input = Obj("value", new() { Kind = "loop_index", Source = "loop" }) }] },
                new() { Key = "choose", Kind = "switch", Expr = new() { Kind = "boolean", Boolean = true }, Cases = [new("true", null, [new() { Key = "selected", Kind = "set", Input = Obj("value", Num(1)) }])], Default = [new() { Key = "fallback", Kind = "set", Input = Obj("value", Num(0)) }] },
                new() { Key = "call", Kind = "workflow.call", Input = new() { Kind = "object", Members = [new("ref", new() { Kind = "workflow", Source = "helper" }), new("args", Obj("value", new() { Kind = "output", Source = "parallel", Path = ["branches", "1", "right", "value"] }))] } }],
            Outputs = [new() { Name = "result", Schema = number, Value = new() { Kind = "output", Source = "call", Path = ["result"] } }]
        }, new() { Key = "helper", Inputs = [new() { Name = "value", Schema = number }], Steps = [new() { Key = "double", Kind = "set", Input = Obj("value", new() { Kind = "compute", Text = "value * 2", Members = [new("value", new() { Kind = "input", Source = "value" })] }), OutputSchema = new() { Type = "object", Properties = [new() { Name = "value", Schema = number }] } }], Outputs = [new() { Name = "result", Schema = number, Value = new() { Kind = "output", Source = "double", Path = ["value"] } }] }] };
        var state = await PlannerFixture.RunAsync(new TestRuntime(intent));
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Equal(1, state.ModelCalls); Assert.Contains(state.Scenarios, s => s.Id.StartsWith("branch:", StringComparison.Ordinal));
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var engine = new WorkflowEngine(); var main = doc.Workflows[doc.Entrypoint!];
        var result = await engine.ExecuteAsync(main, new JsonObject(), Ct);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("8", result.Outputs!["result"]!.ToJsonString());
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => engine.ExecuteAsync(main, new JsonObject { ["count"] = null }, Ct));
        var imported = PlanningGraphImporter.Import(state.Yaml!, state.Catalog!);
        var recompiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(imported, state.Catalog!)));
        var revised = await engine.ExecuteAsync(recompiled.Workflows[recompiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(revised.Success, revised.Error?.Message); Assert.Equal("8", revised.Outputs!["result"]!.ToJsonString());
    }
    [Fact]
    public async Task ArtifactHolesSelectOriginalProducerAndCatalogBindingsOverrideModelValues()
    {
        var runtime = new TestRuntime(); var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, Ct);
        catalog.Policy.RequireExternalConfirmation = false;
        catalog.Capabilities = [new() { Id = "create", StepType = "mcp.call", Kind = "tool", Server = "test", Method = "create", InputSchema = Schema("""{"mode":{"type":"string","enum":["create"]}}""", "mode"), OutputSchema = Schema("""{"id":{"type":"string"}}""", "id"), ArtifactContract = new(1, [new("resource", "/id", "materialize")], []) },
            new() { Id = "consume", StepType = "mcp.call", Kind = "tool", Server = "test", Method = "consume", InputSchema = Schema("""{"id":{"type":"string"},"mode":{"type":"string"}}""", "id", "mode"), OutputSchema = Schema("{}"), RequestBindings = [new("/mode", JsonValue.Create("locked"))], ArtifactContract = new(1, [], [new("resource", "/id", true)]) }];
        var intent = new WorkflowIntentPlan { Workflows = [new() { Steps = [new() { Key = "create", CapabilityId = "create" }, new() { Key = "consume", CapabilityId = "consume", Input = Obj("mode", new() { Kind = "string", Text = "untrusted" }) }] }] };
        var graph = PlanningGraphBuilder.Build(intent, catalog);
        var holes = PlanningHoleEligibility.Find(graph, catalog); Assert.Equal(2, holes.Count);
        var first = holes.Single(h => h.NodeKey == "create"); var dependent = holes.Single(h => h.NodeKey == "consume");
        Assert.Empty(PlanningHoleEligibility.Choices(graph, catalog, dependent));
        graph = PlanningHoleEligibility.Assign(graph, catalog, first, Assert.Single(PlanningHoleEligibility.Choices(graph, catalog, first)).Value);
        var hole = Assert.Single(PlanningHoleEligibility.Find(graph, catalog));
        var choice = Assert.Single(PlanningHoleEligibility.Choices(graph, catalog, hole));
        graph = PlanningHoleEligibility.Assign(graph, catalog, hole, choice.Value);
        Assert.Empty(PlanningExecutableValidation.Validate(graph, catalog));
        var request = graph.Workflows[0].Steps[1].Input.Members[0].Value;
        Assert.Equal("locked", request.Members.Single(m => m.Name == "mode").Value.Text);
        request.Members.Single(m => m.Name == "id").Value.Kind = "string";
        request.Members.Single(m => m.Name == "id").Value.Text = "invented";
        Assert.Contains(PlanningExecutableValidation.Validate(graph, catalog), d => d.Code == "ARTIFACT_BINDING_INVALID");
        static JsonObject Schema(string properties, params string[] required) => new() { ["type"] = "object", ["properties"] = JsonNode.Parse(properties), ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()) };
    }
    [Fact]
    public async Task StructuredResultsRemainTypedAcrossCompilationAndScenarioExecution()
    {
        var schema = new PlanningSchema { Type = "object", Properties = [new() { Name = "value", Schema = new() { Type = "number" } }] };
        var plan = new WorkflowIntentPlan { Workflows = [new() {
            Steps = [new() { Key = "model", Kind = "llm.call", Input = new() { Kind = "object", Members = [new("model", new() { Kind = "string", Text = "test" }), new("prompt", new() { Kind = "string", Text = "Return a number" })] }, StructuredOutput = new(schema) }],
            Outputs = [new() { Name = "result", Schema = new() { Type = "number" }, Value = new() { Kind = "output", Source = "model", ResultChannel = "structured", Path = ["value"] } }]
        }], Fixtures = new() { Observations = [new("main", "model", [Obj("value", Num(7))])] } };
        var runtime = new TestRuntime(plan); var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Single(runtime.Calls); Assert.All(state.Scenarios, scenario => Assert.Equal("passed", scenario.Outcome));
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(state.Yaml!));
        var modelStep = Assert.Single(doc.Workflows[doc.Entrypoint!].Source.Steps);
        Assert.Equal("object", modelStep.Input!["structured_output"]!["schema_inline"]!["type"]!.ToString());
    }
    private static PlanningValue Obj(string name, PlanningValue value) => new() { Kind = "object", Members = [new(name, value)] };
    private static PlanningValue Num(decimal number) => new() { Kind = "number", Number = number };
}
