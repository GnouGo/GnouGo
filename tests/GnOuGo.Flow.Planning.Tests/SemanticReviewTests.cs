using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SemanticReviewTests
{
    [Fact]
    public async Task IterationCorrectionRequiresNewBehaviorReviewAndRetainsAnswers()
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.BehaviorPlan = BehaviorPlan(); state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Answers.Add(new("Keep the greeting?", new JsonObject { ["answer"] = "yes" })); state.ClarificationForms = 1; state.ClarificationQuestions = 1;
        var runtime = new FakeRuntime { OnCall = (phase, _, _) =>
        {
            Assert.Equal("semantic_review", phase);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(new JsonObject
            {
                ["code"] = "COVERAGE", ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/behavior",
                ["evidence"] = "Return a greeting", ["message"] = "Make the intended repeated operation explicit in the reviewed behavior.", ["blocking"] = true
            }) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Created, state.Status); Assert.Equal(PlanningPhase.Behavior, state.CurrentPhase);
        Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.ApprovedHash); Assert.Null(state.Graph);
        Assert.NotNull(state.PreviousGraph); Assert.NotNull(state.Preparation); Assert.Single(state.Answers);
        Assert.Equal(1, state.ClarificationForms); Assert.Contains("repeated operation", state.Feedback);
        Assert.DoesNotContain(runtime.Phases, phase => phase is "repair_unit" or "repair_fragment");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidAssessmentRepairsItsEvidenceOrPausesWithoutEditingTheExecutable(bool invalidWorkflow, bool exhausted)
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        var original = PlanningGraphCompiler.Fingerprint(state.Graph); var calls = 0;
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("semantic_review", phase); calls++;
            if (calls == 2) Assert.Contains("assessment contract only", request.Prompt);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(new JsonObject
            {
                ["code"] = "SEMANTIC_NOTE", ["workflow"] = invalidWorkflow && calls == 1 ? "invented" : "main",
                ["location"] = "/workflows/0/steps/0/input",
                ["evidence"] = !invalidWorkflow && (calls == 1 || exhausted) ? "invented request evidence" : "Return a greeting",
                ["message"] = "The greeting requirement is explicitly retained.", ["blocking"] = false
            }) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls); Assert.Equal(0, state.RepairAttempt);
        Assert.Equal(original, PlanningGraphCompiler.Fingerprint(state.Graph!));
        Assert.Equal(exhausted ? PlanningStatus.Recovery : PlanningStatus.FinalReview, state.Status);
        if (exhausted)
        {
            Assert.Equal("semantic_review", state.CurrentPhase);
            Assert.Contains(state.Diagnostics, d => d.Code == "SEMANTIC_REVIEW_EVIDENCE_INVALID");
            Assert.DoesNotContain(state.Diagnostics, d => d.Code == "GRAPH_VALIDATION");
        }
    }
}
