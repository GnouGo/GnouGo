using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessIntentRevisionTests
{
    [Fact]
    public async Task ExplicitIntentRevisionRetainsHistoryAndStartsANewReviewedBusinessBlockAfterRestart()
    {
        var model = new CurrentPlanningFixture.LiteralModel();
        var runtime = new TestRuntime { Respond = (request, _) => model.CallAsync(request, PlannerFixture.Ct).GetAwaiter().GetResult() };
        var state = PlannerFixture.Capture(new() { Request = new()
        {
            TenantId = "business-intent-revision", Prompt = "Return the required public numeric result named result with value 42.",
            MaxModelCalls = 64, MaxReplanAttempts = 4, Generation = new("medium", 32000, 8192)
        } });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        PlanningArtifactApproval.Verify(state);
        var originalPrompt = state.Request.Prompt;
        var artifact = state.ComputeArtifactHash();
        var originalPlan = PlanningConstruction.Hash(state.Plan);
        var originalBlock = Assert.Single(state.Construction!.Compact!.Blocks!);
        var blockSnapshot = JsonSerializer.Serialize(originalBlock, PlanningJsonContext.Default.PlanningConstructionBlock);
        var originalStepCounter = state.Construction.Compact.NextStep;
        var priorCheckpoints = state.Construction.Checkpoints.Select(checkpoint => new
        {
            checkpoint.Sequence, checkpoint.PlanHash,
            Fragment = checkpoint.Fragment.ToJsonString(), Reviews = new JsonArray(checkpoint.Reviews.Select(review => (JsonNode?)review.DeepClone()).ToArray()).ToJsonString()
        }).ToArray();
        var priorMeasurements = JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!["requestMeasurements"]!.DeepClone();
        var calls = state.ModelCalls; var repairs = state.ReplanAttempts;
        const string revision = "Change the public result to 43 instead of 42; retain its name and numeric type.";
        state = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "revise", ExpectedRevision = state.Revision, Text = revision }, runtime, PlannerFixture.Ct);
        state = PlannerFixture.Clone(state);
        PlanningCompactConstruction.ValidateBusinessContract(state);
        Assert.StartsWith(originalPrompt, state.Request.Prompt, StringComparison.Ordinal);
        Assert.Contains(revision, state.Request.Prompt, StringComparison.Ordinal);
        Assert.Null(state.Plan); Assert.Null(state.Requirements); Assert.Null(state.ApprovedHash); Assert.Null(state.ApprovalReview);
        Assert.Equal(originalPlan, PlanningConstruction.Hash(state.Request.Baseline));
        Assert.Equal(blockSnapshot, JsonSerializer.Serialize(Assert.Single(state.Construction!.Compact!.Blocks!), PlanningJsonContext.Default.PlanningConstructionBlock));
        Assert.Equal(originalStepCounter, state.Construction.Compact.NextStep);
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(repairs, state.ReplanAttempts); Assert.Equal(calls, runtime.Calls.Count);
        Assert.True(JsonNode.DeepEquals(priorMeasurements, JsonSerializer.SerializeToNode(state, PlanningJsonContext.Default.PlanningSession)!["requestMeasurements"]));
        Assert.All(state.Construction.Checkpoints.Take(priorCheckpoints.Length), checkpoint => Assert.NotEmpty(checkpoint.InvalidatedTaskIds));
        var reset = state.Construction.Checkpoints[^1];
        Assert.Equal(originalPlan, reset.BaseHash); Assert.Equal(PlanningConstruction.Hash(null), reset.PlanHash);
        Assert.Null(reset.Step); Assert.Empty(reset.TaskIds); Assert.Empty(reset.RequirementIds); Assert.Empty(reset.ClosedRequirementIds!); Assert.Empty(reset.Reviews);
        Assert.Equal("intent_revision", reset.Fragment["transition"]!.ToString());
        Assert.Equal(revision, reset.Fragment["command"]!["text"]!.ToString());
        Assert.Equal(state.Revision, reset.Fragment["revision"]!.GetValue<long>());
        Assert.False(HybridWorkflowPlanner.ConstructionArtifactReviewValid(state));
        var revisedCalls = new List<string>();
        runtime.Respond = (request, purpose) =>
        {
            revisedCalls.Add(purpose);
            var context = BusinessProjectionDeclarationsTests.Context(request);
            if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                Assert.Empty(context["availableResults"]!.AsArray());
                Assert.Contains(revision, context["requirements"]!.ToJsonString(), StringComparison.Ordinal);
            }
            var response = model.CallAsync(request, PlannerFixture.Ct).GetAwaiter().GetResult();
            if (purpose == PlanningCompactConstruction.SelectPurpose)
            {
                response.Json!["block"]!["objective"] = "Return the revised public numeric result.";
                response.Json["block"]!["expectedResults"] = new JsonArray("The public numeric result named result has value 43.");
                response.Json["block"]!["successCriteria"] = new JsonArray("Return result = 43 without external effects.");
            }
            if (purpose == PlanningConstruction.DraftPurpose)
            {
                response.Json!["extension"]!["tasks"]![0]!["outputs"]![0]!["value"]!["number"] = 43;
                response.Json["extension"]!["tasks"]![0]!["objective"] = "Return the revised required number.";
            }
            if (purpose == PlanningConstruction.GlobalPurpose)
            {
                Assert.Contains(revision, request.Prompt, StringComparison.Ordinal);
                var pending = runtime.Checkpoints.Last(checkpoint => checkpoint.PendingCall?.Purpose == PlanningConstruction.GlobalPurpose);
                Assert.False(HybridWorkflowPlanner.ConstructionArtifactReviewValid(pending));
                Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(pending));
                response.Json!["decision"]!["reason"] = "The actual public result is 43 as explicitly revised; the original name and numeric type are retained.";
            }
            return response;
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(new[] { PlanningCompactConstruction.IntentPurpose, PlanningCompactConstruction.SelectPurpose,
            PlanningCompactConstruction.ImplementationPurpose, PlanningConstruction.DraftPurpose, PlanningConstruction.GlobalPurpose }, revisedCalls);
        Assert.Equal(calls + 5, state.ModelCalls); Assert.Equal(repairs, state.ReplanAttempts);
        Assert.NotEqual(artifact, state.ComputeArtifactHash()); Assert.Null(state.ApprovedHash);
        var blocks = state.Construction!.Compact!.Blocks!;
        Assert.Equal(2, blocks.Count); Assert.NotEqual(originalBlock.Id, blocks[1].Id);
        Assert.Equal(blockSnapshot, JsonSerializer.Serialize(blocks[0], PlanningJsonContext.Default.PlanningConstructionBlock));
        Assert.True(state.Construction.Compact.NextStep > originalStepCounter);
        foreach (var prior in priorCheckpoints)
        {
            var retained = state.Construction.Checkpoints.Single(checkpoint => checkpoint.Sequence == prior.Sequence);
            Assert.Equal(prior.PlanHash, retained.PlanHash); Assert.Equal(prior.Fragment, retained.Fragment.ToJsonString());
            Assert.Equal(prior.Reviews, new JsonArray(retained.Reviews.Select(review => (JsonNode?)review.DeepClone()).ToArray()).ToJsonString());
            Assert.NotEmpty(retained.InvalidatedTaskIds);
        }
        Assert.Equal(43, state.Plan!.Root.Tasks.Single().Outputs.Single().Value.Number);
        PlanningArtifactApproval.Verify(state);
        foreach (var corruption in new[] { "before", "after", "omitted" })
        {
            var changed = PlannerFixture.Clone(state);
            var bridge = changed.Construction!.Checkpoints.Single(checkpoint => checkpoint.Sequence == reset.Sequence);
            if (corruption == "before") bridge.BaseHash = new string('0', 64);
            if (corruption == "after") bridge.PlanHash = new string('0', 64);
            if (corruption == "omitted") changed.Construction.Checkpoints.Remove(bridge);
            Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(changed));
        }
        var available = PlanningCompactConstruction.SelectionContext(state)["availableResults"]!.AsArray();
        Assert.DoesNotContain(available, result => result!["id"]!.ToString() == "result:" + originalBlock.Id);
        Assert.Equal("result:" + blocks[1].Id, Assert.Single(available)!["id"]!.ToString());
    }
}
