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
