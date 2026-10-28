using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionDefectProgressTests
{
    [Theory]
    [InlineData("objective", "unchanged")]
    [InlineData("unrelated", "unchanged")]
    [InlineData("binding", "changed")]
    [InlineData("renamed", "unverified")]
    [InlineData("source", "basis_changed")]
    [InlineData("claim", "claims_changed")]
    public async Task WitnessTracksAffectedBehaviorInsteadOfCandidateNovelty(string change, string expected)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        Reject(state);
        var candidate = state.Construction!.Candidate!;
        var task = candidate.Plan.Root.Tasks.Single();
        var before = candidate.Hash;
        if (change == "objective") task.Objective = "Cosmetic label only";
        if (change == "unrelated")
        {
            candidate.Plan.Root.Tasks.Add(new() { Id = "unrelated", Kind = "value", Objective = "Produce an unrelated constant.", Outputs = [new("other", new() { Kind = "number", Number = 7 })] });
            candidate.TaskIds.Add("unrelated");
        }
        if (change == "binding") task.Outputs[0].Value.Items[1].Number = 3;
        if (change == "renamed") { task.Id = "renamed"; candidate.TaskIds = ["renamed"]; }
        if (change == "source") state.Construction.Compact!.Sources[0].Version = "different";
        if (change == "claim") state.Construction.Compact!.CurrentStep!.CloseRequirementIds.Clear();
        candidate.Hash = PlanningConstruction.Hash(candidate.Plan);
        var progress = Assert.Single(PlanningConstruction.DefectProgressContext(state, PlanningBusinessProjection.Build(state, false))!)!;
        Assert.Equal(expected, progress["status"]!.ToString());
        if (change is "objective" or "unrelated") Assert.NotEqual(before, candidate.Hash);
        Assert.Null(progress["resolved"]);
        Assert.False(candidate.BusinessReviewed);
    }

    [Theory]
    [InlineData("extract")]
    [InlineData("interpret")]
    public async Task TransformInstructionChangesRemainExecutableEvidence(string mode)
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var task = state.Construction!.Candidate!.Plan.Root.Tasks.Single();
        task.Kind = "transform"; task.Mode = mode; task.Objective = "Preserve every supplied observation";
        state.Construction.Candidate.Hash = PlanningConstruction.Hash(state.Construction.Candidate.Plan);
        Reject(state);
        task.Objective = "Return only the first observation";
        state.Construction.Candidate.Hash = PlanningConstruction.Hash(state.Construction.Candidate.Plan);
        var progress = Assert.Single(PlanningConstruction.DefectProgressContext(state, PlanningBusinessProjection.Build(state, false))!)!;
        Assert.Equal("changed", progress["status"]!.ToString());
        Assert.Equal("Return only the first observation", progress["current"]!["value"]!["instruction"]!.ToString());
    }

    [Fact]
    public async Task RepeatedRepairRetainsTheOriginalWitnessAndUnchangedBinding()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        Reject(state);
        var before = state.Construction!.Candidate!;
        var witness = before.Reviews.Single()["defectWitness"]!.ToJsonString();
        var after = new PlanningConstructionCandidate { BaseHash = before.BaseHash, Hash = before.Hash,
            Plan = PlanningConstruction.Clone(before.Plan), Extension = before.Extension, TaskIds = before.TaskIds, OpenScopes = before.OpenScopes };
        PlanningConstruction.CarryDefectWitnesses(state, before, after);
        state.Construction.Candidate = after;
        state = BusinessReviewProtocolTests.Clone(state);
        Assert.Equal(witness, state.Construction!.Candidate!.Reviews.Single()["defectWitness"]!.ToJsonString());
        Assert.Equal("unchanged", Assert.Single(PlanningConstruction.DefectProgressContext(state, PlanningBusinessProjection.Build(state, false))!)!["status"]!.ToString());
    }

    private static void Reject(PlanningSession state)
    {
        var context = PlanningBusinessProjection.Build(state, false);
        var finding = new JsonObject { ["kind"] = "business_fulfillment", ["location"] = "/actions/0",
            ["evidence"] = new JsonArray("/requirements/0", "/actions/0"), ["explanation"] = "Check the actual numerical source and multiplier.", ["uncertainty"] = "" };
        var witness = PlanningConstruction.DefectWitness(state, context, new JsonArray(finding));
        Assert.NotNull(witness);
        state.Construction!.Candidate!.Reviews.Add(new() { ["outcome"] = "needs_change", ["defectWitness"] = witness });
    }
}
