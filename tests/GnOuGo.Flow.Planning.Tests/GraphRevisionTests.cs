using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class GraphRevisionTests
{
    [Fact]
    public void DependentStagesAndCallersAreInvalidatedWhileUnrelatedStagesStayFrozen()
    {
        var graph = new PlanningGraph { Workflows = [new() { Key = "main", Steps =
            [new() { Key = "call", Type = "workflow.call", Input = PlanningCorpus.Obj(("ref", PlanningCorpus.Ref("workflow", "child"))) },
             new() { Key = "consumer", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Ref("output", "call", "value"))) },
             new() { Key = "untouched", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(42))) }] },
            new() { Key = "child", Steps = [new() { Key = "producer", Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(1))) }],
                Outputs = [new() { Name = "value", Schema = new() { Type = "number" }, Value = PlanningCorpus.Ref("output", "producer", "value") }] }] };
        var scope = PlanningGraphRevisions.Scope(graph, [new("INVALID", "/workflows/1/steps/0/input", "Invalid value")]);
        Assert.Contains("main/call", scope); Assert.Contains("main/consumer", scope); Assert.Contains("child/producer", scope); Assert.DoesNotContain("main/untouched", scope);
        var replacement = JsonSerializer.Deserialize(JsonSerializer.Serialize(graph, PlanningJsonContext.Default.PlanningGraph), PlanningJsonContext.Default.PlanningGraph)!;
        replacement.Workflows[0].Steps[2].Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(43)));
        Assert.NotEmpty(PlanningGraphRevisions.Validate(graph, replacement, scope));
        replacement.Workflows[0].Steps[2].Input = graph.Workflows[0].Steps[2].Input;
        replacement.Workflows[1].Steps[0].Input = PlanningCorpus.Obj(("value", PlanningCorpus.Num(2)));
        Assert.Empty(PlanningGraphRevisions.Validate(graph, replacement, scope));
    }
    [Fact]
    public async Task RejectedScopeExpansionCannotBecomeNextRepairBaseline()
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Graph!.Workflows[0].Steps.Add(new() { Key = "bad", Type = "number.add", Input = PlanningCorpus.Obj(("left", PlanningCorpus.Text("invalid")), ("right", PlanningCorpus.Num(1))) });
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        Assert.Contains("main/bad", state.RevisionScope); Assert.DoesNotContain("main/greet", state.RevisionScope);
        var original = JsonSerializer.Serialize(state.Graph, PlanningJsonContext.Default.PlanningGraph);
        runtime.Proposal.Graph.Workflows[0].Steps[0].Input = PlanningCorpus.Obj(("message", PlanningCorpus.Text("unapproved change")));
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Equal(original, JsonSerializer.Serialize(state.Graph, PlanningJsonContext.Default.PlanningGraph)); Assert.Null(state.Yaml);
    }
    [Theory]
    [InlineData("compute", "6 * 7")]
    [InlineData("expression", "data.inputs.value.map(x => x * 2)")]
    [InlineData("template", "{{secret}}")]
    public async Task GeneratedGlueCannotHideScriptsInsideInputObjects(string kind, string text)
    {
        var runtime = new TestRuntime(); var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Steps[0].Input.Members[0].Value.Kind = kind; graph.Workflows[0].Steps[0].Input.Members[0].Value.Text = text;
        var catalog = await runtime.DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        Assert.Contains(PlanningGeneratedGraph.Validate(graph, PlannerFixture.Requirements(), catalog), d => d.Code.StartsWith("GENERATED_", StringComparison.Ordinal));
    }
}
