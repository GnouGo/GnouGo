using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessReviewTests
{
    [Theory]
    [InlineData("unique", "declared")]
    [InlineData("absent", "unverified")]
    [InlineData("ambiguous", "unverified")]
    [InlineData("repeated", "unverified")]
    public async Task OperationAssociationsComeOnlyFromAcceptedCommitments(string variant, string expected)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        var requirement = runtime.Proposal.Requirements!.Outcomes[0];
        if (variant == "absent") runtime.Proposal.Requirements.Outcomes[0] = requirement with { Commitments = null };
        if (variant == "ambiguous") runtime.Proposal.Requirements.Outcomes.Add(requirement with { Id = "other" });
        if (variant == "repeated") runtime.Proposal.Plan!.Root.Tasks.Add(new() { Id = "repeat", Objective = "Another invocation", Operation = requirement.Commitments![0].Operation });
        state.Request.Prompt = "Deliver the result and perform another independently requested action.";
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        var association = Assert.Single(state.ValidationResults, r => r.Id == "association:perform");
        Assert.Equal(expected, association.Outcome);
        Assert.Contains("write", association.Description);
        Assert.Contains("location", association.Description);
        Assert.Contains(state.ValidationResults, r => r.Id.EndsWith(":limits", StringComparison.Ordinal) && r.Outcome == "unverified");
        Assert.Single(runtime.Calls); Assert.Null(state.ApprovedHash);
    }
}
