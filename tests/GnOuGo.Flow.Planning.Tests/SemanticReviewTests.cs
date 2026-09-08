using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SemanticReviewTests
{
    [Theory]
    [InlineData("Restore each affected component", "Remove all temporary resources")]
    [InlineData("Restaurer chaque composant concerné", "Supprimer toutes les ressources temporaires")]
    public async Task EvidenceRepairCannotDropFindingsOrTreatGeneratedQuestionsAsIntent(string requirement, string cleanup)
    {
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Request.Prompt = requirement + "\n" + cleanup;
        state.Answers.Add(new("Invented materialized context", new JsonObject { ["answer"] = "yes" }));
        var calls = 0; var runtime = new FakeRuntime { OnCall = (_, request, _) =>
        {
            calls++;
            if (calls == 2)
            {
                Assert.Single(request.StructuredOutputSchema!["properties"]!.AsObject());
                var allowed = request.StructuredOutputSchema["properties"]!["finding_1_evidence"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>());
                Assert.DoesNotContain("Invented materialized context", allowed); Assert.Contains(requirement, allowed);
                Assert.DoesNotContain("Graph:", request.Prompt);
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["finding_1_evidence"] = requirement } });
            }
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(
                Finding("CLEANUP", cleanup), Finding("OBSERVATION", "Invented materialized context")) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Contains(state.Diagnostics, d => d.Code == "CLEANUP"); Assert.Contains(state.Diagnostics, d => d.Code == "OBSERVATION");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code.StartsWith("SEMANTIC_REVIEW", StringComparison.Ordinal));
        JsonObject Finding(string code, string evidence) => new() { ["code"] = code, ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/input",
            ["message"] = code + " must preserve the requirement.", ["evidence"] = evidence, ["blocking"] = true };
    }

    [Fact]
    public async Task CollectionCleanupAndEnumFindingsSurviveBehaviorReassessmentAndRestart()
    {
        // Sanitized mixed findings from live final review: changing iteration must
        // retain the independent implementation defects and the answered intent.
        var state = Session(PlanningStatus.Validating); state.Graph = Graph(); state.Preparation = Preparation();
        state.Request.Prompt = "Read the complete collection, remove every created directory, and preserve the chosen enum value.";
        state.BehaviorPlan = BehaviorPlan(); state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Answers.Add(new("Require confirmation?", new JsonObject { ["answer"] = "yes" }));
        state.ClarificationForms = 1; state.ClarificationQuestions = 1;
        var findings = new JsonArray(new[]
        {
            (Code: "COLLECTION_INCOMPLETE", Field: "behavior", Evidence: "complete collection", Message: "Only the first page is read; repeat until the complete collection is observed."),
            (Code: "CLEANUP_INCOMPLETE", Field: "input", Evidence: "every created directory", Message: "Cleanup must cover every created directory, including the parent allocation."),
            (Code: "ENUM_MEANING_CHANGED", Field: "input", Evidence: "chosen enum value", Message: "The mapping replaces a valid source outcome with the default instead of preserving its meaning.")
        }.Select(f => (JsonNode)new JsonObject
        { ["code"] = f.Code, ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/" + f.Field, ["evidence"] = f.Evidence, ["message"] = f.Message, ["blocking"] = true }).ToArray());
        var runtime = new FakeRuntime { OnCall = (phase, _, _) =>
        {
            Assert.Equal("semantic_review", phase);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = findings.DeepClone() } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        state = System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Equal(PlanningPhase.Behavior, state.CurrentPhase); Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.ApprovedHash);
        Assert.Equal(3, state.Diagnostics.Count); Assert.Equal(3, state.Attempts.Last().Diagnostics.Count);
        foreach (var finding in state.Diagnostics) Assert.Contains(finding.Message, state.Feedback);
        Assert.NotNull(state.PreviousGraph); Assert.Single(state.Answers); Assert.Equal(1, state.ClarificationQuestions);
    }

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
            if (calls == 2 && !invalidWorkflow)
                return Task.FromResult(new LLMResponse { Json = new JsonObject { ["finding_0_evidence"] = exhausted ? "invented request evidence" : "Return a greeting" } });
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
