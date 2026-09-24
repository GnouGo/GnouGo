using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class TaskPlanRevisionTests
{
    [Fact]
    public void DependenciesInvalidateOnlyAffectedTasksAndPreserveCompiledStages()
    {
        var plan = PlanningCorpus.Greeting();
        plan.Root.Tasks.Add(new() { Id = "producer", Kind = "value", Objective = "Produce a value", Outputs = [new("value", PlanningCorpus.Number(1))] });
        plan.Root.Tasks.Add(new() { Id = "consumer", Kind = "value", Objective = "Consume the value", Outputs = [new("value", PlanningCorpus.Business("output", "producer", "value"))] });
        var scope = TaskPlanRevisions.Scope(plan, [new("INVALID", "/tasks/producer/inputs/value", "Invalid value")]);
        Assert.Contains("producer", scope); Assert.Contains("consumer", scope); Assert.DoesNotContain("greet", scope);
        var revised = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        revised.Root.Tasks[1].Outputs[0].Value.Number = 2;
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope));
        var before = new TaskPlanCompiler().Compile(plan, new()).Graph!.Workflows[0].Steps[0];
        var after = new TaskPlanCompiler().Compile(revised, new()).Graph!.Workflows[0].Steps[0];
        Assert.Equal(JsonSerializer.Serialize(before, PlanningJsonContext.Default.PlanningNode), JsonSerializer.Serialize(after, PlanningJsonContext.Default.PlanningNode));
        revised.Root.Tasks[0].Outputs[0].Value.Text = "Unapproved change";
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, revised, scope));
    }
    [Fact]
    public async Task RejectedScopeExpansionCannotReplaceTheRepairBaselineOrDiscovery()
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Plan!.Root.Tasks.Add(new() { Id = "bad", Kind = "value", Objective = "Return an unavailable value", Outputs = [new("value", PlanningCorpus.Business("output", "absent", "value"))] });
        var planner = new HybridWorkflowPlanner();
        var state = await planner.AdvanceAsync(PlannerFixture.Session(), new(), runtime, PlannerFixture.Ct);
        Assert.Contains("bad", state.RevisionScope); Assert.DoesNotContain("greet", state.RevisionScope);
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        runtime.Proposal.Plan.Root.Tasks[0].Outputs[0].Value.Text = "Unapproved change";
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Contains(state.Diagnostics, d => d.Code != "REVISION_SCOPE_CHANGED");
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Null(state.Yaml); Assert.Equal(1, runtime.Discoveries); Assert.Equal(2, state.ModelCalls);
    }
    [Fact]
    public void IdentityPrefixesDoNotExpandRepairScope()
    {
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "work" }, new() { Id = "work_long" }] } };
        var scope = TaskPlanRevisions.Scope(plan, [new("INVALID", "/tasks/work_long/inputs/value", "Bad input")]);
        Assert.Contains("work_long", scope); Assert.DoesNotContain("work", scope);
    }
    [Theory]
    [InlineData("compute", "6 * 7")]
    [InlineData("expression", "data.inputs.value.map(x => x * 2)")]
    [InlineData("template", "{{secret}}")]
    public async Task GeneratedGlueCannotHideScriptsInsideInputObjects(string kind, string text)
    {
        var graph = PlannerFixture.Greeting();
        graph.Workflows[0].Steps[0].Input.Members[0].Value.Kind = kind; graph.Workflows[0].Steps[0].Input.Members[0].Value.Text = text;
        var catalog = await new TestRuntime().DiscoverAsync(PlannerFixture.Session().Request, PlannerFixture.Ct);
        Assert.Contains(PlanningGeneratedGraph.Validate(graph, catalog), d => d.Code.StartsWith("GENERATED_", StringComparison.Ordinal));
    }
}
