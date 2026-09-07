using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BehaviorRevisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusedIterationRevisionPreservesOtherNodesAndRequiresNewApproval(bool legacy)
    {
        var state = Session(PlanningStatus.Created); state.Preparation = Preparation(); state.IntentChecked = true;
        var plan = BehaviorPlan(); state.PreviousGraph = Graph();
        state.BehaviorRevisionSource = legacy ? null : plan;
        state.Feedback = "Repeat the greeting for the complete supplied collection.";
        state.Answers.Add(new("Retain the greeting?", new JsonObject { ["choice"] = "yes" }));
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.PreviousGraph), "semantic_review", 9, false,
            [new("CARDINALITY", "/workflows/0/steps/0/behavior", state.Feedback)]));
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision", phase); calls++;
            Assert.DoesNotContain("workflows", request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key));
            var wrapper = new PlanningBehaviorNode { Key = "iterate", Kind = "loop", Purpose = "Repeat for every entry", InputDependencies = [] };
            var patch = new JsonObject { ["main/greeting"] = new JsonObject { ["action"] = "wrap_loop", ["node"] = JsonSerializer.SerializeToNode(wrapper, PlanningJsonContext.Default.PlanningBehaviorNode) } };
            return Task.FromResult(new LLMResponse { Json = patch });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(PlanningStatus.BehaviorReview, state.Status);
        Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.ApprovedHash); Assert.Null(state.Graph);
        Assert.Equal("greeting", Assert.Single(Assert.Single(state.BehaviorPlan!.Workflows[0].Steps).Steps).Key);
        Assert.Single(state.Answers); Assert.NotNull(state.PreviousGraph);
    }

    [Fact]
    public async Task OutputCeilingIsPersistedAndDoesNotTriggerAnIdenticalWholePlanRequest()
    {
        var state = Session(PlanningStatus.Created); state.Preparation = Preparation(); state.IntentChecked = true;
        state.PreviousGraph = Graph(); state.BehaviorRevisionSource = BehaviorPlan(); state.Feedback = "Repeat the operation.";
        state.Attempts.Add(new(PlanningGraphCompiler.Fingerprint(state.PreviousGraph), "semantic_review", 9, false,
            [new("CARDINALITY", "/workflows/0/steps/0/behavior", state.Feedback)]));
        var calls = 0;
        var runtime = new FakeRuntime { OnCall = (_, _, _) => { calls++; return Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }); } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls); Assert.Equal(1, state.BehaviorAssessmentCalls); Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
        Assert.Contains(state.Attempts, a => a.Phase == "behavior_revision" && a.Diagnostics.Any(d => d.Code == "MODEL_OUTPUT_LIMIT"));
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "BEHAVIOR_SCHEMA_INVALID");
        Assert.Null(state.ApprovedBehaviorHash); Assert.NotNull(state.BehaviorRevisionSource);
    }

    [Fact]
    public void IdenticalNodeKeysInDifferentWorkflowsRemainIsolated()
    {
        var baseline = BehaviorPlan(); baseline.Workflows.Add(new() { Key = "other", Purpose = "Separate", Steps = [new() { Key = "greeting", Purpose = "Retain this node" }] });
        var wrapper = new PlanningBehaviorNode { Key = "iterate", Kind = "loop", Purpose = "Repeat the first node" };
        var patch = new JsonObject { ["main/greeting"] = new JsonObject { ["action"] = "wrap_loop", ["node"] = JsonSerializer.SerializeToNode(wrapper, PlanningJsonContext.Default.PlanningBehaviorNode) } };
        var revised = PlanningBehaviorRevisions.Apply(baseline, patch);
        Assert.Equal("iterate", revised.Workflows[0].Steps[0].Key);
        Assert.Equal("greeting", revised.Workflows[1].Steps[0].Key);
        Assert.Equal("Retain this node", revised.Workflows[1].Steps[0].Purpose);
    }

    [Fact]
    public void InvalidWrapperCannotDiscardOrDuplicateTheOriginalOperation()
    {
        var baseline = BehaviorPlan(); var original = PlanningBehaviorPlans.Fingerprint(baseline);
        var wrapper = new PlanningBehaviorNode { Key = "greeting", Kind = "loop", Purpose = "Invalid reuse" };
        var patch = new JsonObject { ["main/greeting"] = new JsonObject { ["action"] = "wrap_loop", ["node"] = JsonSerializer.SerializeToNode(wrapper, PlanningJsonContext.Default.PlanningBehaviorNode) } };
        Assert.Throws<InvalidOperationException>(() => PlanningBehaviorRevisions.Apply(baseline, patch));
        Assert.Equal(original, PlanningBehaviorPlans.Fingerprint(baseline));
    }
}
