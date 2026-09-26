using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class TaskOptionalOutputTests
{
    [Theory]
    [InlineData("value", true)]
    [InlineData("null", true)]
    [InlineData("missing", false)]
    [InlineData("invalid", false)]
    public async Task OptionalPortUsesCheckedProjectionWithoutInventingMissingValues(string response, bool success)
    {
        var (plan, catalog, engine) = await Setup(response);
        var graph = Compile(plan, catalog);
        Assert.Single(graph.Workflows.SelectMany(w => w.Steps.Concat(w.Finally)).Where(n => n.Type == "value.project"));
        var first = JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph);
        Assert.Equal(first, JsonSerializer.Serialize(Compile(plan, catalog), PlanningJsonContext.Default.PlanningGraph));
        var result = await Run(graph, catalog, engine, new());
        Assert.Equal(success, result.Success);
        if (response == "value") Assert.Equal("observed", result.Outputs!["result"]!.ToString());
        if (response == "null") Assert.Null(result.Outputs!["result"]);
        if (!success) Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BranchCaptureChecksThePortOnlyInsideTheSelectedConsumer(bool selected)
    {
        var (plan, catalog, engine) = await Setup("missing");
        plan.Inputs.Add(new() { Name = "selected", Type = new() { Kind = "boolean" } });
        plan.Root.Tasks.Add(new() { Id = "branch", Kind = "conditional", Objective = "Consume only when selected",
            Condition = new() { Kind = "input", Source = "selected" },
            Body = new() { Outputs = [new("result", Ref())] },
            Otherwise = new() { Outputs = [new("result", new() { Kind = "string", Text = "unused" })] } });
        plan.Root.Outputs = [new("result", new() { Kind = "output", Source = "branch", Port = "result" })];
        var graph = Compile(plan, catalog);
        var result = await Run(graph, catalog, engine, new() { ["selected"] = selected });
        Assert.Equal(!selected, result.Success);
        if (!selected) Assert.Equal("unused", result.Outputs!["result"]!.ToString());
    }

    [Fact]
    public async Task UnusedOptionalPortsNeedNoCheckAndWholeResultsKeepTheirOriginalContract()
    {
        var (plan, catalog, engine) = await Setup("missing");
        plan.Root.Outputs = [new("result", new() { Kind = "output", Source = "read" })];
        var graph = Compile(plan, catalog);
        Assert.DoesNotContain(graph.Workflows.SelectMany(w => w.Steps.Concat(w.Finally)), n => n.Type == "value.project");
        Assert.True((await Run(graph, catalog, engine, new())).Success);
    }

    [Fact]
    public async Task RequiredNullablePortRemainsADirectReference()
    {
        var (plan, catalog, _) = await Setup("null");
        catalog.Capabilities.Single(c => c.Kind == "tool").OutputSchema["required"] = new JsonArray("selected");
        var graph = Compile(plan, catalog);
        Assert.DoesNotContain(graph.Workflows.SelectMany(w => w.Steps.Concat(w.Finally)), n => n.Type == "value.project");
    }

    private static TaskValue Ref() => new() { Kind = "output", Source = "read", Port = "selected" };
    private static PlanningGraph Compile(TaskPlan plan, PlanningCatalog catalog)
    {
        var compilation = new TaskPlanCompiler().Compile(plan, catalog);
        Assert.Empty(compilation.Diagnostics); Assert.NotNull(compilation.Graph);
        Assert.Empty(PlanningGeneratedGraph.Validate(compilation.Graph, catalog));
        PlanningConfirmationGuards.Apply(compilation.Graph, catalog);
        Assert.Empty(PlanningExecutableValidation.Validate(compilation.Graph, catalog));
        return compilation.Graph;
    }
    private static async Task<GnOuGo.Flow.Core.Models.RunResult> Run(PlanningGraph graph, PlanningCatalog catalog, WorkflowEngine engine, JsonObject input)
    {
        var doc = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, catalog)));
        return await engine.ExecuteAsync(doc.Workflows[doc.Entrypoint!], input, PlannerFixture.Ct);
    }
    private static async Task<(TaskPlan Plan, PlanningCatalog Catalog, WorkflowEngine Engine)> Setup(string response)
    {
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("renamed-source", new() { Tools = [new() { Name = "inspect", Description = "Return observations", EffectKind = "read",
            InputSchema = JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}"""),
            OutputSchema = JsonNode.Parse("""{"type":"object","properties":{"selected":{"type":["string","null"]}},"required":[],"additionalProperties":false}""") }],
            ToolHandlers = new() { ["inspect"] = _ => new() { Content = response switch
                { "value" => new JsonObject { ["selected"] = "observed" }, "null" => new JsonObject { ["selected"] = null }, "invalid" => new JsonObject { ["selected"] = 42 }, _ => new JsonObject() } } } });
        var engine = new WorkflowEngine { McpClientFactory = factory, HumanInputProvider = new PlanningCorpus.Human() };
        var catalog = await TaskPlanCompilerTests.Catalog(new WorkflowPlanningRuntime(engine, (_, _) => Task.CompletedTask));
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "read", Kind = "operation", Objective = "Read observations", Operation = catalog.Capabilities.Single(c => c.Kind == "tool").Id }], Outputs = [new("result", Ref())] } };
        return (plan, catalog, engine);
    }
}
