using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PreparationRecoveryTests
{
    [Fact]
    public async Task RepeatedForbiddenHelpersCanReassessMissingObservationsBeforeExecutableCompletion()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); state.Request.Prompt = "Observe resource contents and summarize them.";
        var unit = new PlanningConstructionUnit { Key = "unit", WorkflowKey = "main", Kind = "implementation", NodeKeys = ["greeting"], Status = "invalid", Calls = 2, RepairCalls = 1,
            CandidateHash = "candidate", Candidate = new JsonObject { ["nodes"] = new JsonObject { ["greeting"] = new JsonObject() }, ["functions"] = "function read() { return require('module'); }" },
            Diagnostics = [new("UNIT_HELPER_DEPENDENCY_INVALID", "/workflows/0/functions", "Undeclared require dependency.")] };
        state.ConstructionUnits = [unit];
        var runtime = new FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("semantic_review", phase); Assert.Contains("require", request.Prompt);
            Assert.All(request.StructuredOutputSchema!["properties"]!["findings"]!["items"]!["properties"]!["location"]!["enum"]!.AsArray(), p => Assert.EndsWith("/preparation", p!.ToString()));
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(new JsonObject
            {
                ["code"] = "OBSERVATION_MISSING", ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/preparation",
                ["message"] = "The content needs an external observation before local summarization.", ["evidence"] = state.Request.Prompt, ["blocking"] = true
            }) } });
        } };
        state = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.Created, state.Status); Assert.Null(state.Preparation); Assert.Null(state.ApprovedBehaviorHash);
        Assert.Equal(new[] { "semantic_review" }, runtime.Phases); Assert.Equal(1, state.PreparationReassessments);
        Assert.Contains(state.Attempts, a => a.Phase == "construction_observation_review");
    }

    [Theory]
    [InlineData("Return a greeting", false)]
    [InlineData("Renvoyer une salutation", false)]
    [InlineData("Return a greeting", true)]
    public async Task MissingObservationReassessesPreparationWithoutRewritingIntentOrRetainingApproval(string prompt, bool exhausted)
    {
        var state = Session(PlanningStatus.Validating); state.Request.Prompt = prompt;
        state.Graph = Graph(); state.Preparation = Preparation(); state.BehaviorPlan = BehaviorPlan();
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan); state.ApprovedHash = "old"; state.ArtifactHash = "old";
        state.Answers.Add(new("Retained choice", new JsonObject { ["choice"] = "yes" })); state.ClarificationForms = 1; state.ClarificationQuestions = 1;
        state.PreparationCheckpoint = new() { ValidatedResults = new JsonObject { ["discovery"] = new JsonArray(), ["inventory"] = new JsonObject { ["old"] = true } } };
        state.PreparationReassessments = exhausted ? state.Request.MaxRepairs : 0;
        var runtime = new FakeRuntime { OnCall = (phase, _, _) =>
        {
            Assert.Equal("semantic_review", phase);
            return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray(new JsonObject
            {
                ["code"] = "OBSERVATION_MISSING", ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/preparation",
                ["message"] = "The required computation cannot observe external content through a resource handle.", ["evidence"] = prompt, ["blocking"] = true
            }, new JsonObject
            {
                ["code"] = "FAILURE_RESULT_MISSING", ["workflow"] = "main", ["location"] = "/workflows/0/steps/0/input",
                ["message"] = "Retain the unsuccessful observation outcome in the public result.", ["evidence"] = prompt, ["blocking"] = true
            }) } });
        } };
        var planner = new TypedWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(exhausted ? PlanningStatus.Recovery : PlanningStatus.Created, state.Status);
        Assert.Null(state.ApprovedHash); Assert.Null(state.ArtifactHash); Assert.Single(state.Answers);
        Assert.Equal(1, state.ClarificationForms); Assert.Equal(1, state.ClarificationQuestions); Assert.Equal(prompt, state.Request.Prompt);
        Assert.Contains(state.Attempts, a => a.Phase == "preparation_review");
        if (exhausted)
        {
            Assert.Contains(state.Diagnostics, d => d.Code == "PREPARATION_REASSESSMENT_LIMIT");
            Assert.Contains(state.Diagnostics, d => d.Code == "FAILURE_RESULT_MISSING"); Assert.NotNull(state.Graph); return;
        }
        Assert.Null(state.ApprovedBehaviorHash); Assert.Null(state.Preparation); Assert.Null(state.Graph);
        Assert.NotNull(state.PreparationCheckpoint!.ValidatedResults["discovery"]); Assert.Null(state.PreparationCheckpoint.ValidatedResults["inventory"]);
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        Assert.Contains("unsuccessful observation outcome", state.Feedback);
        runtime = new FakeRuntime { OnPrepare = request =>
        {
            Assert.Contains("Retained choice", request.Prompt); Assert.DoesNotContain("resource handle", request.Prompt);
            Assert.Single(request.PreparationFeedback); return Task.FromResult(Preparation());
        }, OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior", phase); Assert.Contains("unsuccessful observation outcome", request.Prompt);
            return Task.FromResult(new LLMResponse { Json = JsonSerializer.SerializeToNode(BehaviorPlan(), PlanningJsonContext.Default.PlanningBehaviorPlan) });
        } };
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(PlanningStatus.BehaviorReview, state.Status); Assert.Null(state.ApprovedBehaviorHash);
        Assert.Equal(1, state.PreparationReassessments); Assert.Single(state.Answers);
    }
}
