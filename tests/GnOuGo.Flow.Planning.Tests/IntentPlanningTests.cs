using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Planning.Tests;
public sealed class IntentPlanningTests
{
    [Fact]
    public async Task CompleteWorkflowUsesOneInterpretationCallAndExecutes()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("\n", state.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Single(runtime.Calls); Assert.Equal(1, runtime.Discoveries); Assert.Null(state.ApprovedHash);
        Assert.All(state.Scenarios, s => Assert.Equal("passed", s.Outcome));
        var document = new GnOuGo.Flow.Core.Compilation.WorkflowCompiler().Compile(GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(state.Yaml!));
        var result = await new WorkflowEngine().ExecuteAsync(document.Workflows[document.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal("Hello", result.Outputs!["message"]!.ToString());
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Approved, state.Status); Assert.Single(runtime.Calls);
    }
    [Fact]
    public async Task SingletonOutputHoleUsesNoExtraModelCall()
    {
        var plan = PlannerFixture.Greeting(); plan.Workflows[0].Outputs[0].Value = new() { Kind = "hole" };
        var runtime = new TestRuntime(plan); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal("output", state.Graph!.Workflows[0].Outputs[0].Value.Kind);
        Assert.Equal("hole", state.IntentPlan!.Workflows[0].Outputs[0].Value.Kind);
    }
    [Fact]
    public async Task AmbiguousOutputUsesBoundedIssuedChoices()
    {
        var plan = PlannerFixture.Greeting(); plan.Workflows[0].Steps[0].Input.Members.Add(new("other", new() { Kind = "string", Text = "Other" }));
        plan.Workflows[0].Outputs[0].Value = new() { Kind = "hole" };
        var runtime = new TestRuntime(plan); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, runtime.Calls.Count);
        var schema = runtime.Calls[1].StructuredOutputSchema!;
        Assert.All(schema["properties"]!.AsObject(), p => Assert.Equal(2, p.Value!["enum"]!.AsArray().Count));
    }
    [Fact]
    public async Task NoChoiceProducesDiagnosticAndBoundedRepair()
    {
        var plan = PlannerFixture.Greeting(); plan.Workflows[0].Outputs[0].Value = new() { Kind = "hole" }; plan.Workflows[0].Outputs[0].Schema.Type = "number";
        var runtime = new TestRuntime(plan); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Contains(state.Diagnostics, d => d.Code == "HOLE_UNRESOLVED");
        Assert.Equal(2, runtime.Calls.Count); Assert.Contains(state.Diagnostics, d => d.Code == "REPAIR_NO_PROGRESS");
    }
    [Fact]
    public async Task RepairMayReplaceStructureAndReturnsToFullValidation()
    {
        var bad = PlannerFixture.Greeting(); bad.Workflows[0].Steps[0].Kind = "missing";
        var runtime = new TestRuntime(bad); runtime.Plans.Enqueue(PlannerFixture.Greeting("Fixed"));
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(1, state.RepairAttempts); Assert.Equal(2, state.ModelCalls);
        Assert.Contains("STEP_TYPE_DENIED", runtime.Calls[1].Prompt); Assert.Contains("Fixed", state.Yaml);
    }
    [Fact]
    public async Task InvalidJsonStopsAfterTwoRepairs()
    {
        var runtime = new TestRuntime { Respond = _ => new() { Text = "not json" } };
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(3, runtime.Calls.Count); Assert.Equal(2, state.RepairAttempts);
    }
    [Fact]
    public async Task ModelCannotSmuggleHostPolicyInItsResponse()
    {
        var runtime = new TestRuntime { Respond = r =>
        {
            var response = PlannerFixture.Response(PlannerFixture.Greeting(), r.StructuredOutputSchema!.AsObject()).AsObject();
            response["policy"] = new JsonObject { ["requireExternalConfirmation"] = false }; return new() { Json = response };
        }};
        var state = await PlannerFixture.RunAsync(runtime); Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "INTENT_SCHEMA_INVALID");
    }
    [Fact]
    public async Task BudgetsAndApprovalSurviveRevisionAndRejectStaleCommands()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); var planner = new TypedWorkflowPlanner();
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision - 1, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, TestContext.Current.CancellationToken));
        state = await planner.AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision, Text = "Change the greeting" }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, state.ModelCalls); Assert.Null(state.ApprovedHash); Assert.Null(state.Yaml); Assert.NotNull(state.Request.Baseline);
    }
    [Fact]
    public async Task AlteredYamlCannotBeApproved()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime); state.Yaml += "\n# altered";
        await Assert.ThrowsAsync<PlanningConflictException>(() => new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ChangedCatalogInvalidatesReview()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        runtime.CatalogChanges = [new("CATALOG_CHANGED", "$", "Changed")];
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "approve", ExpectedRevision = state.Revision, ArtifactHash = PlanningArtifactApproval.Hash(state) }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.ApprovedHash);
    }
    [Fact]
    public async Task ClarificationIsTypedAndRetainsCallBudget()
    {
        var question = PlannerFixture.Greeting(); question.Questions.Add(new("language", "Which language?", new() { Enum = ["English", "French"] }));
        var runtime = new TestRuntime(question); runtime.Plans.Enqueue(PlannerFixture.Greeting()); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        var planner = new TypedWorkflowPlanner();
        await Assert.ThrowsAsync<ArgumentException>(() => planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["language"] = 7 } }, runtime, TestContext.Current.CancellationToken));
        state = await planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["language"] = "French" } }, runtime, TestContext.Current.CancellationToken);
        state = await PlannerFixture.RunAsync(runtime, state); Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls);
    }
    [Fact]
    public void CoreAndPlannerRemainIndependentlyPublishable()
    {
        Assert.DoesNotContain(typeof(IWorkflowPlanner).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("GnOuGo."));
        Assert.Equal(["GnOuGo.Flow.Core"], typeof(TypedWorkflowPlanner).Assembly.GetReferencedAssemblies().Where(a => a.Name!.StartsWith("GnOuGo.")).Select(a => a.Name));
        Assert.DoesNotContain(typeof(PlanningSession).GetProperties(), p => p.Name.Contains("Proof") || p.Name.Contains("Fingerprint") || p.Name.Contains("Behavior"));
    }
}
