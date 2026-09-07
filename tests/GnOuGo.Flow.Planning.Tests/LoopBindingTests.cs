using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LoopBindingTests
{
    [Fact]
    public void LegacyLoopUnitIsSplitBeforeBodyContracts_WithoutLosingCandidateOrReceipts()
    {
        var loop = new PlanningNode { Key = "each", Type = "loop.sequential", Input = Obj(("items", new() { Kind = "array", Items = [Str("first")] })), Steps = [new() { Key = "child", Input = Obj(("value", Str("retained"))) }] };
        var graph = new PlanningGraph { Workflows = [new() { Key = "main", Steps = [loop] }] };
        var legacy = new PlanningConstructionUnit { Key = "old-unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["each", "child"], ContractVersion = 10, Calls = 3, RepairCalls = 2, RequestHashes = ["receipt"] };
        legacy.Candidate = PlanningConstruction.Values(graph.Workflows[0], legacy);
        var state = Session(PlanningStatus.Generating); state.Graph = graph; state.Preparation = Preparation();
        state.ConstructionUnits = [legacy, new() { Key = "consumer", Dependencies = [legacy.Key] }];
        TypedWorkflowPlanner.UpgradeLoopUnits(state);
        Assert.Equal("superseded", legacy.Status); Assert.Equal(3, legacy.Calls); Assert.Equal(2, legacy.RepairCalls); Assert.Equal("receipt", Assert.Single(legacy.RequestHashes));
        var parent = state.ConstructionUnits.Single(u => u.Key == "old-unit:0");
        var body = state.ConstructionUnits.Single(u => u.Key == "old-unit:1");
        Assert.Contains(parent.Key, body.Dependencies); Assert.Single(body.Candidate!["nodes"]!.AsObject());
        Assert.Contains("retained", body.Candidate.ToJsonString()); Assert.Equal(0, body.Calls);
        Assert.Contains(body.Key, state.ConstructionUnits.Single(u => u.Key == "consumer").Dependencies);
        TypedWorkflowPlanner.UpgradeLoopUnits(state); Assert.Equal(4, state.ConstructionUnits.Count);
    }

    [Theory]
    [InlineData("each", "send", "resources", false)]
    [InlineData("chaque", "transmettre", "ressources", false)]
    [InlineData("count", "inspect", "resources", true)]
    public async Task EveryTypedCollectionItemReachesItsOperation_AndCannotEscapeTheLoop(string loopKey, string tool, string input, bool countLoop)
    {
        var preparation = Preparation();
        var schema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")!.AsObject();
        preparation.Capabilities.Add(new() { Id = "cap", StepType = "mcp.call", Server = "renamed", Method = tool, Kind = "tool", InputSchema = schema });
        var node = new PlanningNode { Key = "consume", Type = "mcp.call", CapabilityId = "cap", Input = Obj(("request", Obj(("value", new() { Kind = "loop_item", Source = loopKey })))) };
        var loop = new PlanningNode { Key = loopKey, Type = "loop.sequential", Input = Obj(("items", new() { Kind = "input", Source = input })), Steps = [node] };
        if (countLoop)
        {
            loop.Input = Obj(("times", new() { Kind = "number", Number = 3 }));
            node.Input = Obj(("request", Obj(("value", new() { Kind = "compute", Text = "String(index)", Members = [new("index", new() { Kind = "loop_index", Source = loopKey })] }))));
        }
        var workflow = new PlanningWorkflow { Key = "main", Inputs = [new() { Name = input, Required = true, Schema = new() { Type = "array", Items = new() { Type = "string" } } }],
            Steps = [loop, new() { Key = "after", Input = Obj(("ok", new() { Kind = "boolean", Boolean = true })) }] };
        var graph = new PlanningGraph { Workflows = [workflow] };
        var item = Assert.Single(PlanningDataflow.Index(workflow, preparation, graph, node.Key).Values, b => b.Value.Kind == (countLoop ? "loop_index" : "loop_item"));
        Assert.Equal(countLoop ? "integer" : "string", item.Schema["type"]!.GetValue<string>());
        Assert.DoesNotContain(PlanningDataflow.Index(workflow, preparation, graph, "after").Values, b => b.Value.Kind is "loop_item" or "loop_index");
        var partition = PlanningConstruction.Partition(workflow, 4);
        var loopUnit = Assert.Single(partition, u => u.Kind == "implementation" && u.NodeKeys.Contains(loopKey));
        Assert.Single(loopUnit.NodeKeys);
        var unit = Assert.Single(partition, u => u.Kind == "implementation" && u.NodeKeys.Contains(node.Key));
        Assert.Contains(loopUnit.Key, unit.Dependencies);
        graph = PlanningConstruction.Apply(graph, loopUnit, PlanningConstruction.UpgradeCandidate(graph, loopUnit, PlanningConstruction.Values(workflow, loopUnit), preparation), preparation);
        workflow = graph.Workflows[0];
        var candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit), preparation);
        Assert.Empty(PlanningConstruction.ShapeFindings(candidate, PlanningConstruction.Schema(workflow, unit, preparation, graph), unit));
        graph = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        var factory = new InMemoryMcpClientFactory(); var received = new List<string>();
        factory.RegisterServer("renamed", new() { Tools = [new() { Name = tool, InputSchema = schema }], ToolHandlers = new()
        { [tool] = args => { received.Add(args!["value"]!.GetValue<string>()); return new() { Content = JsonValue.Create("done") }; } } });
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)));
        var run = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { [input] = new JsonArray("alpha", "beta", "gamma") }, TestContext.Current.CancellationToken);
        Assert.True(run.Success, run.Error?.Message); Assert.Equal(countLoop ? new[] { "0", "1", "2" } : new[] { "alpha", "beta", "gamma" }, received);
        graph.Workflows[0].Steps[1].Input = Obj(("invalid", new() { Kind = "loop_item", Source = loopKey }));
        Assert.Contains(PlanningGraphValidation.Validate(graph, preparation), d => d.Code == "LOOP_BINDING_SCOPE_INVALID");
    }
}
