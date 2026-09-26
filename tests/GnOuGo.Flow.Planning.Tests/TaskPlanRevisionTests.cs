using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class TaskPlanRevisionTests
{
    [Fact]
    public void RepairingAProducerRevalidatesConsumersWithoutGrantingEditPermission()
    {
        var plan = PlanningCorpus.Greeting();
        plan.Root.Tasks.Add(new() { Id = "producer", Kind = "value", Objective = "Produce a value", Outputs = [new("value", PlanningCorpus.Number(1))] });
        plan.Root.Tasks.Add(new() { Id = "consumer", Kind = "value", Objective = "Consume the value", Outputs = [new("value", PlanningCorpus.Business("output", "producer", "value"))] });
        var scope = TaskPlanRevisions.Scope(plan, [new("INVALID", "/tasks/producer/outputs/value", "Invalid value")]);
        Assert.Equal(new[] { "/tasks/producer/outputs/value" }, scope);
        var revised = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        revised.Root.Tasks[1].Outputs[0].Value.Number = 2;
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope));
        var before = new TaskPlanCompiler().Compile(plan, new()).Graph!.Workflows[0].Steps[0];
        var after = new TaskPlanCompiler().Compile(revised, new()).Graph!.Workflows[0].Steps[0];
        Assert.Equal(JsonSerializer.Serialize(before, PlanningJsonContext.Default.PlanningNode), JsonSerializer.Serialize(after, PlanningJsonContext.Default.PlanningNode));
        revised.Root.Tasks[2].Outputs[0] = new("value", PlanningCorpus.Number(100));
        Assert.NotEmpty(TaskPlanRevisions.Validate(plan, revised, scope));
        revised.Root.Tasks[2].Outputs[0] = new("value", PlanningCorpus.Business("output", "producer", "value"));
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
        Assert.Equal(new[] { "/tasks/bad/outputs/value" }, state.RevisionScope);
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        runtime.Proposal.Plan.Root.Tasks[0].Outputs[0].Value.Text = "Unapproved change";
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "REVISION_SCOPE_CHANGED");
        Assert.Contains(state.Diagnostics, d => d.Code != "REVISION_SCOPE_CHANGED");
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Null(state.Yaml); Assert.Equal(1, runtime.Discoveries); Assert.Equal(2, state.ModelCalls);
    }
    [Fact]
    public void InvalidatingAContainerPreservesItsUnaffectedNestedTasks()
    {
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "container", Kind = "sequence", Objective = "Run tasks", Body = PlanningCorpus.Greeting().Root }] } };
        plan.Root.Tasks[0].Body!.Tasks.Add(new() { Id = "bad", Kind = "value", Objective = "Repair this task", Outputs = [new("value", PlanningCorpus.Number(1))] });
        var scope = TaskPlanRevisions.Scope(plan, [new("INVALID", "/tasks/bad/outputs/value", "Bad value")]);
        Assert.Equal(new[] { "/tasks/bad/outputs/value" }, scope);
        var revised = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, PlanningJsonContext.Default.TaskPlan), PlanningJsonContext.Default.TaskPlan)!;
        revised.Root.Tasks[0].Body!.Tasks[1].Outputs[0].Value.Number = 2;
        Assert.Empty(TaskPlanRevisions.Validate(plan, revised, scope));
        revised.Root.Tasks[0].Body!.Tasks[0].Outputs[0].Value.Text = "Changed unaffected nested task";
        Assert.Contains(TaskPlanRevisions.Validate(plan, revised, scope), d => d.Code == "REVISION_SCOPE_CHANGED");
        revised.Root.Tasks[0].Body!.Tasks[0].Outputs[0].Value.Text = plan.Root.Tasks[0].Body!.Tasks[0].Outputs[0].Value.Text;
        revised.Root.Tasks[0].Kind = "value";
        Assert.Contains(TaskPlanRevisions.Validate(plan, revised, scope), d => d.Code == "REVISION_SCOPE_CHANGED");
    }
    [Fact]
    public void IdentityPrefixesDoNotExpandRepairScope()
    {
        var plan = new TaskPlan { Root = new() { Tasks = [new() { Id = "work" }, new() { Id = "work_long" }] } };
        var scope = TaskPlanRevisions.Scope(plan, [new("INVALID", "/tasks/work_long/objective", "Bad input")]);
        Assert.Equal(new[] { "/tasks/work_long/objective" }, scope);
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
