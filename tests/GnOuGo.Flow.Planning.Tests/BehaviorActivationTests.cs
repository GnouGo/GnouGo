using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BehaviorActivationTests
{
    [Theory]
    [InlineData("local", true)]
    [InlineData("native", false)]
    [InlineData(null, false)]
    public void LocalProcessingIsAnObligationWhileNativeCapabilitiesSelectAnExactExecutor(string? resolution, bool valid)
    {
        var preparation = TypedPlannerTests.Preparation();
        preparation.Capabilities.Add(new() { Id = "logic", StepType = "set", EffectKind = "none", Resolution = resolution, Required = true, OperationIds = ["evaluate"] });
        var plan = TypedPlannerTests.BehaviorPlan();
        plan.Workflows[0].OperationIds = ["evaluate"];
        plan.Workflows[0].Steps = [new() { Key = "decision", Kind = "decision", CapabilityId = "logic", Purpose = "Choose a local result",
            Outcomes = [new("known", "Known outcome", false, []), new("unknown", "Unknown outcome", true, [])] }];
        Assert.Equal(valid, PlanningBehaviorPlans.Validate(plan, preparation).Count == 0);
        var graph = PlanningBehaviorPlans.Display(plan, preparation); graph.Workflows[0].Outputs.Clear();
        Assert.Equal(valid, !PlanningGraphValidation.Validate(graph, preparation).Any(d => d.Code == "CAPABILITY_BINDING_INVALID"));
        graph.Workflows[0].Steps[0].Expr = TypedPlannerTests.Str("known");
        if (valid) Assert.Equal("switch", GnOuGo.Flow.Core.Parsing.WorkflowParser.Parse(new PlanningGraphCompiler().Compile(graph, preparation)).Workflows["main"].Steps[0].Type);
        else Assert.Throws<InvalidOperationException>(() => new PlanningGraphCompiler().Compile(graph, preparation));
        preparation.Capabilities[0].Resolution = "local"; preparation.Capabilities[0].EffectKind = "write";
        Assert.Contains(PlanningGraphValidation.Validate(graph, preparation), d => d.Code == "CAPABILITY_BINDING_INVALID");
    }

    private static (PlanningBehaviorPlan Plan, PlanningPreparation Preparation) Fixture(string prefix)
    {
        var preparation = TypedPlannerTests.Preparation();
        preparation.Capabilities.Add(new()
        {
            Id = prefix + "cap", StepType = "mcp.call", EffectKind = "write", Required = true, OperationIds = [prefix + "op"],
            Activation = new("all_on_value", prefix + "group", prefix + "decision", "APPLY")
            { AllowedValues = ["APPLY", "NO_EFFECT"], NoEffectValues = ["NO_EFFECT"] }
        });
        var plan = TypedPlannerTests.BehaviorPlan();
        plan.Workflows[0].OperationIds = [prefix + "op"];
        plan.Workflows[0].Steps = [new()
        {
            Key = "choice", Kind = "decision", Purpose = "Choose whether to publish",
            Outcomes = [
                new("APPLY", "Publish", false, [new() { Key = "effect", Purpose = "Publish result", CapabilityId = prefix + "cap" }]),
                new("NO_EFFECT", "Publish nothing", false, []),
                new("unknown", "Stop when uncertain", true, [])]
        }];
        return (plan, preparation);
    }

    [Theory]
    [InlineData("renamed_")]
    [InlineData("autre_")]
    public void ExactActivationOutcomesAndNonMutatingDefaultAreValidatedBeforeReview(string prefix)
    {
        var (plan, preparation) = Fixture(prefix);
        Assert.Empty(PlanningBehaviorPlans.Validate(plan, preparation));
        plan.Workflows[0].Steps[0].Outcomes[0] = plan.Workflows[0].Steps[0].Outcomes[0] with { Key = "prose_alias" };
        Assert.Contains(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Message.Contains("exact explicit outcome", StringComparison.Ordinal));
        plan.Workflows[0].Steps[0].Outcomes[0] = plan.Workflows[0].Steps[0].Outcomes[0] with { Key = "APPLY" };
        plan.Workflows[0].Steps[0].Outcomes[2].Steps.Add(new() { Key = "unknown_write", Purpose = "Unexpected write", CapabilityId = prefix + "cap" });
        Assert.Contains(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Message.Contains("default must be non-mutating", StringComparison.Ordinal));
        Assert.Contains(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Message.Contains("must occur exactly once", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultCannotHideWriteInsideNestedDecisionOrWorkflow()
    {
        var (plan, preparation) = Fixture("capability_");
        var inner = plan.Workflows[0];
        inner.Key = "child";
        plan.Workflows.Insert(0, new() { Key = "main", Purpose = "Outer choice", Steps = [new()
        {
            Key = "outer", Kind = "decision", Purpose = "Readiness", Outcomes = [
                new("stop", "Stop", false, []),
                new("otherwise", "Continue", true, [new() { Key = "call", Kind = "workflow", WorkflowKey = "child", Purpose = "Call child" }])]
        }] });
        Assert.Contains(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Location.Contains("outer", StringComparison.Ordinal) && d.Message.Contains("non-mutating", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PlanningStatus.Recovery)]
    [InlineData(PlanningStatus.BehaviorReview)]
    public async Task RetryInvalidatedBehaviorRequiresNewReviewAndPreservesIntentAndUsage(string status)
    {
        var (plan, preparation) = Fixture("generic_");
        plan.Workflows[0].Steps[0].Outcomes[0] = plan.Workflows[0].Steps[0].Outcomes[0] with { Key = "old_alias" };
        var state = TypedPlannerTests.Session(status);
        state.Preparation = preparation; state.BehaviorPlan = plan; state.IntentChecked = true;
        state.Graph = PlanningBehaviorPlans.Display(plan, preparation);
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(plan);
        state.Answers.Add(new("Question", new JsonObject { ["answer"] = "Keep existing criteria" }));
        state.ClarificationForms = 1; state.ClarificationQuestions = 2; state.ActiveMilliseconds = 123;
        state.Diagnostics = [new("OLD", "$", "Old finding")];
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "retry", ExpectedRevision = state.Revision }, new TypedPlannerTests.FakeRuntime(), TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Created, result.Status);
        Assert.Equal(PlanningPhase.Behavior, result.CurrentPhase);
        Assert.Null(result.ApprovedBehaviorHash); Assert.Null(result.Graph); Assert.Null(result.ArtifactHash);
        Assert.NotNull(result.PreviousGraph); Assert.Empty(result.Diagnostics);
        Assert.Equal(state.Request.SessionId, result.Request.SessionId);
        Assert.Equal(state.Request.Prompt, result.Request.Prompt);
        Assert.Single(result.Answers); Assert.Single(result.IntentHistory);
        Assert.Equal(1, result.ClarificationForms); Assert.Equal(2, result.ClarificationQuestions);
        Assert.True(result.ActiveMilliseconds >= 123);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal("APPLY", restored.Preparation!.Capabilities[0].Activation!.BranchValue);
        Assert.Null(restored.ApprovedBehaviorHash);
    }

    [Fact]
    public async Task InvalidatedReviewCannotBeAccepted()
    {
        var (plan, preparation) = Fixture("generic_");
        plan.Workflows[0].Steps[0].Outcomes[0] = plan.Workflows[0].Steps[0].Outcomes[0] with { Key = "old_alias" };
        var state = TypedPlannerTests.Session(PlanningStatus.BehaviorReview);
        state.Preparation = preparation; state.BehaviorPlan = plan; state.ArtifactHash = PlanningBehaviorPlans.Fingerprint(plan);
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "accept_behavior", ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash }, new TypedPlannerTests.FakeRuntime(), TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Recovery, result.Status);
        Assert.Null(result.ApprovedBehaviorHash); Assert.Null(result.Graph);
        Assert.NotEmpty(result.Diagnostics);
    }
}
