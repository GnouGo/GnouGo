using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed partial class CompactAcceptedClosureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshProvenClosureRetainsMoreThanFourRequirementsAndRequiresReview(bool nested)
    {
        var state = await FreshReviewedClosure(nested);
        var accepted = PlanningConstruction.Hash(state.Plan);
        var runtime = ClosureJudge();
        state = await Advance(state, runtime);
        Assert.Empty(runtime.Calls);
        var candidate = Assert.IsType<PlanningConstructionCandidate>(state.Construction!.Candidate);
        Assert.Equal(accepted, candidate.Hash);
        Assert.True(candidate.Extension.RequirementIds.Count > 4);
        Assert.Equal(nested ? "/tasks/inner/body" : "/root", candidate.Extension.Scope);
        Assert.Empty(candidate.Extension.Tasks); Assert.Empty(candidate.Extension.Outputs);
        Assert.False(candidate.LocalReviewed);
        Assert.False(PlanningConstructionReviewCoverage.Complete(state, candidate));
        Assert.Single(candidate.Reviews, row => row["purpose"]?.ToString() == "construction_transition");
        if (!nested)
            Assert.Equal(state.Requirements!.Outcomes.Select(item => item.Id), candidate.Extension.RequirementIds);

        for (var attempt = 0; attempt < 8 && state.Construction!.Candidate is not null; attempt++)
            state = await Advance(PlannerFixture.Clone(state), runtime);
        Assert.NotEmpty(runtime.Calls);
        Assert.Empty(state.Diagnostics);
        Assert.Null(state.Construction!.Candidate);
        Assert.Equal(accepted, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(!nested, state.Construction.Complete);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task HistoricalProvenClosureKeepsItsFourRequirementBound(int presentation)
    {
        var state = await FreshReviewedClosure(false);
        state.Construction!.Compact!.PresentationVersion = presentation;
        state.Request.Options["construction_presentation_version"] = presentation;
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(state));
    }

    [Fact]
    public async Task FreshClosureStillRejectsLostCoverageAndMissingPublicBindings()
    {
        var state = await FreshReviewedClosure(false);
        var missingOutput = PlannerFixture.Clone(state);
        missingOutput.Plan!.Root.Outputs.Clear();
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(missingOutput));
        var missingReview = PlannerFixture.Clone(state);
        foreach (var checkpoint in missingReview.Construction!.Checkpoints)
            if (checkpoint.ClosedRequirementIds?.Contains("extra_4") == true) checkpoint.Reviews.Clear();
        Assert.Null(PlanningCompactConstruction.PrepareReviewedClosure(missingReview));
        var schema = PlanningCompactConstruction.SelectionSchema(state).ToJsonString();
        Assert.Contains("\"maxItems\":4", schema, StringComparison.Ordinal);
    }

    private static async Task<PlanningSession> FreshReviewedClosure(bool nested)
    {
        var state = nested ? await NestedState() : await State();
        if (nested)
            state.Plan!.Root.Tasks.Single(task => task.Id == "outer").Body!.Tasks.Single(task => task.Id == "inner").Body!.Outputs =
                [new("localNumber", new() { Kind = "output", Source = "producer", Port = "number" })];
        for (var i = 1; i <= 4; i++)
        {
            var checkpoint = AddReviewedRequirement(state, "extra_" + i, "Preserve original boundary obligation " + i + ".");
            if (nested) checkpoint.ImportedTaskIds = ["inner"];
        }
        state.Construction!.Compact!.PresentationVersion = 8;
        state.Request.Options["construction_presentation_version"] = 8;
        // Establish fresh, complete coverage through the normal review/accept
        // path; old fixture rows do not become authority merely by changing v8.
        foreach (var requirement in state.Construction.Requirements) requirement.Status = "in_progress";
        foreach (var checkpoint in state.Construction.Checkpoints) checkpoint.Reviews.Clear();
        var chunks = state.Requirements!.Outcomes.Select(item => item.Id).Chunk(4).ToArray();
        foreach (var ids in chunks)
        {
            var compact = state.Construction.Compact!;
            compact.CurrentStep = new()
            {
                Id = "step_" + (++compact.NextStep), BaseHash = PlanningConstruction.Hash(state.Plan),
                Scope = nested ? "/tasks/inner/body" : "/root", Kind = "close", DiscoveryComplete = true,
                Objective = "Review the existing result against the explicit original obligations.",
                RequirementIds = ids.ToList(), CloseRequirementIds = ids.ToList(), CloseScopes = []
            };
            var runtime = ClosureJudge();
            state = await Advance(state, runtime);
            Assert.NotNull(state.Construction!.Candidate); Assert.Empty(runtime.Calls);
            Assert.Empty(state.Construction.Candidate!.Diagnostics);
            for (var attempt = 0; attempt < 8 && state.Construction!.Candidate is not null; attempt++)
            {
                state = await Advance(PlannerFixture.Clone(state), runtime);
                Assert.Empty(state.Diagnostics);
            }
            Assert.Empty(state.Diagnostics); Assert.Null(state.Construction!.Candidate);
        }
        return state;
    }

    private static TestRuntime ClosureJudge() => new() { Respond = (request, purpose) =>
    {
        Assert.Contains(purpose, new[] { PlanningConstruction.LocalPurpose, PlanningConstruction.IntermediatePurpose });
        var response = new JsonObject
        {
            ["action"] = new JsonObject { ["kind"] = "findings", ["findings"] = new JsonArray() },
            ["uncertainty"] = "Runtime remains unverified."
        };
        Assert.Empty(PlanningContractValidation.ValidateInstanceFindings(response, request.StructuredOutputSchema!));
        return new() { Json = response };
    } };
}
