using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BehaviorActivationTests
{
    [Fact]
    public async Task ProvenActivationAliasesConvergeWithoutARepairRequestBeforeHumanReview()
    {
        var (plan, preparation) = Fixture("generic_");
        var decision = plan.Workflows[0].Steps[0];
        decision.Outcomes[0] = decision.Outcomes[0] with { Key = "effect_alias" };
        decision.Outcomes[1] = decision.Outcomes[1] with { Key = "no_effect_alias" };
        foreach (var node in PlanningBehaviorPlans.Enumerate(plan.Workflows[0].Steps)) node.InputDependencies = [];
        Assert.Contains(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Rule == "behavior_25");
        var state = TypedPlannerTests.Session(PlanningStatus.Created); state.Intent.Checked = true; state.Preparation = preparation; state.BehaviorPlan = plan;
        var runtime = new TypedPlannerTests.FakeRuntime();
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status); Assert.Empty(runtime.Requests);
        Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.Graph);
        var result = state.BehaviorPlan!.Workflows[0].Steps[0];
        Assert.Equal(new[] { "APPLY", "NO_EFFECT", "unknown" }, result.Outcomes.Select(o => o.Key));
        Assert.Equal("effect", Assert.Single(result.Outcomes[0].Steps).Key);
        Assert.Empty(result.Outcomes[1].Steps);
        var hash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        PlanningBehaviorPlans.CompleteLockedOutcomes(state.BehaviorPlan, preparation);
        Assert.Equal(hash, PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActivationCompletionDoesNotSwapValidValuesOrGuessBetweenEmptyOutcomes(bool swap)
    {
        var (plan, preparation) = Fixture("generic_"); var decision = plan.Workflows[0].Steps[0];
        if (swap)
        {
            decision.Outcomes[0] = decision.Outcomes[0] with { Key = "NO_EFFECT" };
            decision.Outcomes[1] = decision.Outcomes[1] with { Key = "APPLY" };
        }
        else
        {
            decision.Outcomes[1] = decision.Outcomes[1] with { Key = "alias" };
            decision.Outcomes.Add(new("another_alias", "Another business outcome", false, []));
        }
        var before = PlanningBehaviorPlans.Fingerprint(plan);
        PlanningBehaviorPlans.CompleteLockedOutcomes(plan, preparation);
        Assert.Equal(before, PlanningBehaviorPlans.Fingerprint(plan));
        Assert.NotEmpty(PlanningBehaviorPlans.Validate(plan, preparation));
    }

    [Theory]
    [InlineData("default", "Publish result")]
    [InlineData("resultat", "Publier le résultat")]
    public async Task MissingSafeDefaultIsCompletedForReviewWithoutAnotherModelCall(string caseKey, string purpose)
    {
        var (plan, preparation) = Fixture("renamed_"); preparation.Capabilities[0].Activation = null;
        var decision = plan.Workflows[0].Steps[0]; decision.Outcomes.RemoveRange(1, 2);
        decision.Outcomes[0] = decision.Outcomes[0] with { Key = caseKey, Description = purpose };
        foreach (var node in PlanningBehaviorPlans.Enumerate(plan.Workflows[0].Steps)) node.InputDependencies = [];
        var explicitCase = JsonSerializer.Serialize(decision.Outcomes[0], PlanningJsonContext.Default.PlanningBehaviorOutcome);
        var state = TypedPlannerTests.Session(PlanningStatus.Created); state.Intent.Checked = true; state.Preparation = preparation; state.BehaviorPlan = plan;
        var runtime = new TypedPlannerTests.FakeRuntime();
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status); Assert.Empty(runtime.Requests); Assert.Null(state.ApprovedBehaviorHash);
        decision = state.BehaviorPlan!.Workflows[0].Steps[0];
        Assert.Equal(explicitCase, JsonSerializer.Serialize(decision.Outcomes[0], PlanningJsonContext.Default.PlanningBehaviorOutcome));
        var fallback = Assert.Single(decision.Outcomes, o => o.IsDefault); Assert.Empty(fallback.Steps); Assert.NotEqual(caseKey, fallback.Key);
        var hash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        PlanningBehaviorPlans.CompleteReviewDefaults(state.BehaviorPlan); Assert.Equal(hash, PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan));
        // An explicitly supplied mutating fallback must remain invalid, never be erased.
        fallback.Steps.Add(new() { Key = "unsafe", Kind = "operation", Purpose = "Unexpected write", CapabilityId = "renamed_cap" });
        PlanningBehaviorPlans.CompleteReviewDefaults(state.BehaviorPlan);
        Assert.Contains(PlanningBehaviorPlans.Validate(state.BehaviorPlan, preparation), d => d.Message.Contains("non-mutating", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BehaviorSchemaSeparatesCapabilityIdentifiersAndNativeDecisionProductionFromRouting()
    {
        var preparation = TypedPlannerTests.Preparation();
        preparation.Capabilities.Add(new() { Id = "binding", CatalogId = "catalog", Resolution = "native", StepType = "decision.evaluate", EffectKind = "none", OperationIds = ["operation"] });
        var plan = TypedPlannerTests.BehaviorPlan(); plan.Workflows[0].OperationIds = ["operation"];
        var node = plan.Workflows[0].Steps[0]; node.Kind = "operation"; node.CapabilityId = "binding"; node.OperationIds = ["operation"];
        preparation.AllowedStepTypes.Add("decision.evaluate");
        var state = TypedPlannerTests.Session(PlanningStatus.Created); state.Intent.Checked = true; state.Preparation = preparation;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan) }) };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
        var schema = Assert.IsType<JsonObject>(Assert.Single(runtime.Requests).StructuredOutputSchema);
        Assert.Empty(PlanningContractValidation.ValidateSchema(schema, strict: true));
        IReadOnlyList<string> Findings() => PlanningContractValidation.ValidateInstance(JsonSerializer.SerializeToNode(plan, PlanningJsonContext.Default.PlanningBehaviorPlan), schema);
        Assert.Empty(Findings());
        node.CapabilityId = "operation::catalog"; Assert.NotEmpty(Findings());
        node.CapabilityId = "binding"; node.Kind = "decision"; Assert.NotEmpty(Findings());
        node.Kind = "operation"; node.OperationIds = ["invented"]; Assert.NotEmpty(Findings());
    }

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
            Id = prefix + "cap",
            StepType = "mcp.call",
            EffectKind = "write",
            Required = true,
            OperationIds = [prefix + "op"],
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
        plan.Workflows.Insert(0, new()
        {
            Key = "main",
            Purpose = "Outer choice",
            Steps = [new()
        {
            Key = "outer", Kind = "decision", Purpose = "Readiness", Outcomes = [
                new("stop", "Stop", false, []),
                new("otherwise", "Continue", true, [new() { Key = "call", Kind = "workflow", WorkflowKey = "child", Purpose = "Call child" }])]
        }]
        });
        Assert.Contains(PlanningBehaviorPlans.Validate(plan, preparation), d => d.Location == "/workflows/0/steps/0/outcomes/1/steps" && d.Rule == "behavior_19");
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
