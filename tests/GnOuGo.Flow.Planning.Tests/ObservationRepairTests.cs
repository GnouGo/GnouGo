using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ObservationRepairTests
{
    [Theory]
    [InlineData("items", "emit", "permission")]
    [InlineData("éléments", "publier", "consentement")]
    public void CollectionLoopsDoNotInventObservationSequencesFromStaticPermission(string loopKey, string effectKey, string permissionKey)
    {
        var effect = new PlanningNode { Key = effectKey, Type = "mcp.call" };
        var loop = new PlanningNode { Key = loopKey, Type = "loop.sequential", Steps = [effect], Input = Obj(
            ("items", new() { Kind = "input", Source = "findings" }),
            ("while", new() { Kind = "output", Source = permissionKey, Path = ["response"] })) };
        var workflow = new PlanningWorkflow { Steps = [new() { Key = permissionKey, Type = "human.input" }, loop] };
        Assert.Empty(TypedWorkflowPlanner.ScenarioObservationSources(workflow, loop));
        loop.Input.Members.RemoveAt(1);
        loop.Input.Members.Add(new("while", new() { Kind = "compute", Text = "previous == null || previous.more", Members =
            [new("previous", new() { Kind = "loop_previous", Source = loopKey, Path = [effectKey, "response"] })] }));
        Assert.Equal(effect, Assert.Single(TypedWorkflowPlanner.ScenarioObservationSources(workflow, loop)));
        var alias = new PlanningNode { Key = "alias", Type = "set", Input = new() { Kind = "output", Source = effectKey } };
        loop.Steps.Add(alias);
        loop.Input.Members[1] = new("while", new() { Kind = "loop_previous", Source = loopKey, Path = [alias.Key] });
        Assert.Equal(effect, Assert.Single(TypedWorkflowPlanner.ScenarioObservationSources(workflow, loop)));
    }

    [Theory]
    [InlineData("pages", "read", "more")]
    [InlineData("lots", "lire", "suite")]
    public async Task ObservationShortfallRepairsIterationControlsAndPreservesTheRead(string loopKey, string readKey, string continuation)
    {
        var graph = Graph(); var preparation = Preparation(); var workflow = graph.Workflows[0]; workflow.Outputs.Clear();
        var output = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { [continuation] = new JsonObject { ["type"] = "boolean" } }, ["required"] = new JsonArray(continuation) };
        preparation.Capabilities.Add(new() { Id = "producer", StepType = "mcp.call", Server = "renamed", Method = "observe", Kind = "tool",
            InputSchema = new() { ["type"] = "object" }, OutputSchema = output });
        var read = new PlanningNode { Key = readKey, Type = "mcp.call", CapabilityId = "producer", Input = Obj(("request", Obj())) };
        var loop = new PlanningNode { Key = loopKey, Type = "loop.sequential", Steps = [read], Input = Obj(
            ("items", new() { Kind = "array", Items = [new() { Kind = "null" }] }),
            ("max_times", new() { Kind = "number", Number = 5 }),
            ("while", new() { Kind = "compute", Text = "previous == null || previous." + continuation, Members =
                [new("previous", new() { Kind = "loop_previous", Source = loopKey, Path = [readKey, "response"] })] })) };
        workflow.Steps = [loop];
        var factory = new InMemoryMcpClientFactory(); var calls = 0;
        factory.RegisterServer("renamed", new() { Tools = [new() { Name = "observe", InputSchema = new JsonObject { ["type"] = "object" }, OutputSchema = output }],
            ToolHandlers = new() { ["observe"] = _ => new McpCallResult { Content = new JsonObject { [continuation] = ++calls < 2 } } } });
        async Task Run(PlanningGraph candidate)
        {
            calls = 0;
            var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(new PlanningGraphCompiler().Compile(candidate, preparation)));
            var run = await new WorkflowEngine { McpClientFactory = factory }.ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
            Assert.True(run.Success, run.Error?.Message);
        }
        await Run(graph); Assert.Equal(1, calls);
        var original = new PlanningDiagnostic("SCENARIO_OBSERVATIONS_UNCONSUMED", "/workflows/0/steps/0/steps/0", "Two observations were declared; only one was consumed.");
        var finding = TypedWorkflowPlanner.ConstructionRepairDiagnostic(original, graph);
        Assert.Equal("/workflows/0/steps/0/input", finding.Location);
        var runtimeFailure = original with { Code = "SCENARIO_EXECUTION_FAILED" };
        Assert.Equal(runtimeFailure, TypedWorkflowPlanner.ConstructionRepairDiagnostic(runtimeFailure, graph));
        var unit = new PlanningConstructionUnit { Key = "repair", WorkflowKey = workflow.Key, Kind = "implementation", NodeKeys = [loopKey], ContractVersion = PlanningDataflow.ContractVersion, Diagnostics = [finding] };
        unit.Candidate = PlanningConstruction.UpgradeCandidate(graph, unit, PlanningConstruction.Values(workflow, unit, preparation), preparation);
        var patch = PlanningUnitPatches.Create(graph, unit, PlanningConstruction.Schema(workflow, unit, preparation, graph), preparation);
        var coordinate = Assert.Single(patch.Context(unit.Candidate)).Key;
        Assert.Equal("nodes/" + loopKey + "/input", coordinate);
        var fixedInput = unit.Candidate["nodes"]![loopKey]!["input"]!.DeepClone();
        var members = fixedInput["members"]!.AsArray(); members.Remove(members.Single(m => m!["name"]!.ToString() == "items"));
        var candidate = patch.Apply(unit.Candidate, new() { ["changes"] = new JsonObject { [coordinate] = fixedInput }, ["remove"] = new JsonArray() });
        var repaired = PlanningConstruction.Apply(graph, unit, candidate, preparation);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(read, PlanningJsonContext.Default.PlanningNode),
            System.Text.Json.JsonSerializer.Serialize(repaired.Workflows[0].Steps[0].Steps[0], PlanningJsonContext.Default.PlanningNode));
        await Run(repaired); Assert.Equal(2, calls);
    }
}
