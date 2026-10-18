using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class LiteralCorrectionTests
{
    [Theory]
    [InlineData("greeting")]
    [InlineData("entrée_技术")]
    public async Task LiteralUsesOneIssuedValueSlotAndExecutesTheOriginalInputExactly(string name)
    {
        var (runtime, state) = Fixture(name);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "TASK_LITERAL_INVALID");
        Assert.Contains("For a declared input named name: {\"kind\":\"input\",\"source\":\"name\"}", runtime.Calls[0].Prompt);
        Assert.Contains("Return the smallest sufficient executable business plan", runtime.Calls[0].Prompt);
        Assert.Contains(PlanningSchemaReferences.Walk(runtime.Calls[0].StructuredOutputSchema!.AsObject(), "", 0),
            s => s.Schema["description"]?.ToString().Contains("Literal strings exclude ${}", StringComparison.Ordinal) == true);
        Assert.Equal(1, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        runtime.Proposal.Plan!.Root.Tasks[0].Outputs[0] = new("message", new() { Kind = "input", Source = name });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        var repair = PlanningRepairPatch.RequestContext(runtime.Calls[1])["repair"]!;
        Assert.Equal("/tasks/greet/outputs/message", Assert.Single(repair["slots"]!.AsArray())!["location"]!.ToString());
        Assert.Contains("never strip or escape interpolation", runtime.Calls[1].Prompt);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        var text = "Original ${unexecuted} text\n whitespace\t二";
        var result = await LookupCompilationTests.Execute(state.Yaml!, new() { [name] = text }, new WorkflowEngine());
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(text, result.Outputs!["message"]!.GetValue<string>());
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task ExhaustedLiteralSessionCannotAcquireAnotherCorrectionCall()
    {
        var (runtime, state) = Fixture("greeting"); state.Request.MaxModelCalls = 1;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts); Assert.Contains(state.Diagnostics, d => d.Code == "TASK_LITERAL_INVALID");
        await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state)); Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task LiteralInAProtectedAssertionCannotBecomeInventedSuccess()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Proposal.Plan!.Root.Tasks[0].Requires = new() { Kind = "string", Text = "${ready}" };
        // Retained malformed assertions still require explicit revision. Fresh
        // generation rejects this shape in its Boolean response domain instead.
        state.Catalog = await runtime.DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = runtime.Proposal.Plan; state.Requirements = runtime.Proposal.Requirements;
        state.Revision = 1; state.ModelCalls = 1;
        state.Diagnostics = new TaskPlanCompiler().Compile(state.Plan, state.Catalog).Diagnostics.ToList();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Empty(runtime.Calls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.Yaml); Assert.Contains(state.Diagnostics, d => d.Code == "TASK_LITERAL_INVALID" && d.Location.EndsWith("/requires", StringComparison.Ordinal));
        Assert.Equal("${ready}", state.Plan!.Root.Tasks[0].Requires!.Text);
    }

    [Theory]
    [InlineData("objective")]
    [InlineData("guard")]
    [InlineData("export")]
    [InlineData("operation")]
    public async Task LiteralCorrectionCannotChangeIndependentWork(string change)
    {
        var (runtime, state) = Fixture("greeting"); state.Request.MaxReplanAttempts = 1;
        runtime.Proposal.Plan!.Root.Tasks[0].Requires = new() { Kind = "boolean", Boolean = false };
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        runtime.Proposal.Plan.Root.Tasks[0].Outputs[0] = new("message", new() { Kind = "input", Source = "greeting" });
        if (change == "objective") runtime.Proposal.Plan.Root.Tasks[0].Objective = "Different business work";
        if (change == "guard") runtime.Proposal.Plan.Root.Tasks[0].Requires!.Boolean = true;
        if (change == "export") runtime.Proposal.Plan.Root.Outputs.Clear();
        if (change == "operation") runtime.Proposal.Plan.Root.Tasks.Add(new() { Id = "unrequested", Kind = "operation", Operation = "invented", Objective = "Invent external work" });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(before, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.Contains(state.Diagnostics, d => d.Code is "PLANNING_RESPONSE_INVALID" or "REVISION_SCOPE_CHANGED");
    }

    [Fact]
    public async Task RepeatedLiteralProposalStopsWithinTheExistingFallbackAllowance()
    {
        var (runtime, state) = Fixture("greeting");
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.Yaml);
        Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REPLAN_NO_PROGRESS");
        await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state)); Assert.Equal(3, runtime.Calls.Count);
    }

    [Fact]
    public async Task CommittedLiteralReceiptKeepsItsRequestAndAccountingAtTheLimit()
    {
        var (runtime, state) = Fixture("greeting"); state.Request.MaxModelCalls = 2;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Tasks[0].Outputs[0] = new("message", new() { Kind = "input", Source = "greeting" });
        runtime.Respond = (_, _) => throw new InvalidOperationException("Interrupted after dispatch");
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.NotNull(state.PendingCall);
        var issued = JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest);
        var receipt = TestRuntime.PatchResponse(state.PendingCall.Request, state, runtime.Proposal.Plan);
        var reads = 0;
        var replay = new TestRuntime { Respond = (request, _) =>
        {
            reads++; Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest));
            return new() { Json = receipt.DeepClone() };
        } };
        var restored = PlannerFixture.Clone(state); restored.Status = PlanningStatus.Generating;
        restored = await planner.AdvanceAsync(restored, new() { ExpectedRevision = restored.Revision }, replay, PlannerFixture.Ct);
        Assert.True(restored.Status == PlanningStatus.FinalReview, string.Join(';', restored.Diagnostics));
        Assert.Equal(2, restored.ModelCalls); Assert.Equal(1, restored.ReplanAttempts); Assert.Equal(1, reads);
        await PlannerFixture.RunAsync(replay, restored); Assert.Equal(1, reads);
        Assert.Equal(issued, JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest));
    }

    private static (TestRuntime Runtime, PlanningSession State) Fixture(string name)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["automatic_correction"] = true;
        runtime.Proposal.Plan!.Inputs = [new() { Name = name, Type = new() { Kind = "string" } }];
        runtime.Proposal.Requirements!.Inputs = runtime.Proposal.Plan.Inputs;
        runtime.Proposal.Plan.Root.Tasks[0].Outputs[0] = new("message", new() { Kind = "string", Text = "${" + name + "}" });
        return (runtime, state);
    }
}
