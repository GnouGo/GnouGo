using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessBlockCompletionAffordanceTests
{
    [Fact]
    public async Task AcceptedBusinessContributionCanClaimReviewWithoutAddingWorkOrClosingRoot()
    {
        var (state, runtime) = await Partial();
        var plan = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var block = state.Construction!.Compact!.CurrentBlockId;
        Assert.True(OffersClose(PlanningCompactConstruction.DiscoverySchema(state)));
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningCompactConstruction.DiscoveryPurpose, purpose);
            return new() { Json = new JsonObject
            {
                ["discoveryRequests"] = null, ["evidenceRequests"] = null, ["reselectStep"] = null, ["clarifications"] = null,
                ["binding"] = new JsonObject { ["kind"] = "close", ["operationIds"] = new JsonArray(), ["groupId"] = null, ["closeScopes"] = new JsonArray() }
            } };
        };
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Equal("close", state.Construction!.Compact!.CurrentStep!.Kind);
        Assert.Empty(state.Construction.Compact.CurrentStep.CloseScopes!);
        var calls = runtime.Calls.Count;
        runtime.Respond = (_, _) => throw new InvalidOperationException("Selected unchanged-plan closure needs no drafting echo.");
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Equal(calls, runtime.Calls.Count);
        var candidate = Assert.IsType<PlanningConstructionCandidate>(state.Construction!.Candidate);
        Assert.True(candidate.Extension.CompleteBlock);
        Assert.False(candidate.Extension.Complete);
        Assert.Empty(candidate.TaskIds);
        Assert.Empty(candidate.Extension.CloseScopes);
        Assert.True(HybridWorkflowPlanner.NeedsBusinessReview(state, candidate));
        Assert.Contains(PlanningBusinessProjection.Build(state, global: false)["actions"]!.AsArray(), action => action!["id"]?.ToString() == "compute");
        Assert.Equal(plan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(plan, JsonSerializer.Serialize(candidate.Plan, PlanningJsonContext.Default.TaskPlan));
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal(PlanningConstruction.LocalPurpose, purpose);
            Assert.Contains("multiply", request.Prompt, StringComparison.Ordinal);
            return new() { Json = BusinessReviewProtocolTests.Report("ok") };
        };
        state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Compact!.CurrentBlockId);
        Assert.NotNull(state.Construction.Compact.Blocks!.Single(row => row.Id == block).CompletionCheckpoint);
        Assert.Contains("/root", state.Construction.OpenScopes);
        Assert.All(state.Construction.Requirements, requirement => Assert.False(PlanningCompactConstruction.RequirementCompletionClaimed(state, requirement.Id)));
        Assert.Null(state.ApprovedHash);
        Assert.Equal(plan, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("invalidated")]
    [InlineData("empty")]
    [InlineData("wrong_owner")]
    [InlineData("already_complete")]
    [InlineData("stale")]
    public async Task CompletionChoiceRequiresAnUnreviewedOwnedAcceptedContribution(string defect)
    {
        var (state, _) = await Partial();
        var compact = state.Construction!.Compact!;
        var checkpoint = Assert.Single(state.Construction.Checkpoints);
        switch (defect)
        {
            case "unrelated": checkpoint.Step!.BlockId = "other"; break;
            case "invalidated": checkpoint.InvalidatedTaskIds.Add("compute"); break;
            case "empty": checkpoint.TaskIds.Clear(); break;
            case "wrong_owner": compact.CurrentStep!.BlockId = "other"; break;
            case "already_complete": compact.Blocks![0].CompletionCheckpoint = checkpoint.Sequence; break;
            case "stale": compact.CurrentStep!.BaseHash = "stale"; break;
        }
        Assert.False(OffersClose(PlanningCompactConstruction.DiscoverySchema(state)));
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Partial()
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate!.Extension.CompleteBlock = false;
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Single(state.Construction!.Checkpoints);
        state.Construction.Compact!.CurrentStep = new()
        {
            Id = "finish_business_block", BlockId = state.Construction.Compact.CurrentBlockId, Scope = "/root",
            BaseHash = PlanningConstruction.Hash(state.Plan), Objective = "Request review of the accepted doubled result.",
            RequirementIds = ["work"], ExpectedOutputs = ["The accepted doubled result"], CloseRequirementIds = []
        };
        return (state, runtime);
    }

    private static bool OffersClose(JsonNode schema)
    {
        bool Visit(JsonNode? node) => node switch
        {
            JsonArray array => array.Any(Visit),
            JsonObject obj => obj["properties"]?["kind"]?["enum"] is JsonArray kinds && kinds.Any(kind => kind?.ToString() == "close") || obj.Any(pair => Visit(pair.Value)),
            _ => false
        };
        return Visit(schema["properties"]!["binding"]);
    }
}
