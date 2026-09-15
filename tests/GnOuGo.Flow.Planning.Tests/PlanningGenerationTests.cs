using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningGenerationTests
{

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TemplateObjectsAndArraysLowerNestedReferencesAsExpressions()
    {
        var graph = Graph(); var workflow = graph.Workflows[0];
        workflow.Inputs = [new() { Name = "name", Schema = new() { Type = "string" } }];
        workflow.Steps[0].Input = new()
        {
            Kind = "object",
            Members = [new("message", new()
        {
            Kind = "template", Text = "Context: {{payload}}", Members = [new("payload", new()
            {
                Kind = "object", Members = [new("name", new() { Kind = "input", Source = "name" }), new("nested", new()
                { Kind = "array", Items = [Str("ready"), new() { Kind = "object", Members = [new("again", new() { Kind = "input", Source = "name" })] }] })]
            })]
        })]
        };
        var yaml = new PlanningGraphCompiler().Compile(graph, Preparation());
        var compiled = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(yaml));
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject { ["name"] = "Ada" }, Ct);
        Assert.True(result.Success); Assert.Contains("Ada", result.Outputs!["message"]!.GetValue<string>());
        Assert.Contains("ready", result.Outputs["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConfigureGenerationPreservesBehaviorAnswersAndRejectsStaleCommands()
    {
        var state = ApprovedSkeleton(); state.Status = PlanningStatus.Stopped;
        state.Intent.Answers.Add(new("Choose behavior", new() { ["answer"] = "accepted" })); state.Intent.Forms = 1;
        var approval = state.ApprovedBehaviorHash; var planner = new TypedWorkflowPlanner();
        var next = await planner.AdvanceAsync(state, new() { Kind = "configure_generation", ExpectedRevision = state.Revision, Generation = new() { ReasoningProfile = new() { Routine = "low" } } }, new FakeRuntime(), Ct);
        Assert.Equal(approval, next.ApprovedBehaviorHash); Assert.Single(next.Intent.Answers); Assert.Equal(1, next.Intent.Forms);
        Assert.Single(next.GenerationHistory); Assert.Equal("low", next.Request.Generation.ReasoningProfile.Routine);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(next, new() { Kind = "configure_generation", ExpectedRevision = state.Revision, Generation = new() }, new FakeRuntime(), Ct));
    }

    internal static PlanningSnapshot ApprovedSkeleton()
    {
        var state = Session(PlanningStatus.Generating); state.Preparation = Preparation(); state.BehaviorPlan = BehaviorPlan();
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation); return state;
    }
    private static Task<PlanningSnapshot> Advance(IWorkflowPlanner planner, PlanningSnapshot state, IPlanningRuntime runtime) => planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
}
