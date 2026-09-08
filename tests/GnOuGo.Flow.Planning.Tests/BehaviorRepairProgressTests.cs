using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BehaviorRepairProgressTests
{
    [Fact]
    public async Task RevisionDiscardsCandidateRepairProgressButRetainsUsageAnswersAndHistory()
    {
        var state = Fixture();
        state = await Send(state, "revise", "Return the revised greeting");
        Assert.Equal(PlanningStatus.Created, state.Status);
        Assert.Equal(0, state.RepairAttempt); Assert.Equal(0, state.NonImprovingAttempts);
        Assert.Empty(state.BestDiagnostics); Assert.Null(state.PreviousDiagnosticHash);
        Assert.Equal(37, state.Usage!.Calls); Assert.Single(state.Answers); Assert.Single(state.Attempts);
        Assert.Null(state.ApprovedBehaviorHash);
    }

    [Fact]
    public async Task ApprovingANewBehaviorStartsConstructionInsteadOfOldRepairs()
    {
        var state = Fixture(); state.Status = PlanningStatus.BehaviorReview;
        state.ArtifactHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan!);
        state = await Send(state, "accept_behavior");
        Assert.Equal(0, state.RepairAttempt);
        state = await Send(state, "advance");
        Assert.NotEmpty(state.ConstructionUnits);
        Assert.All(state.ConstructionUnits, u => Assert.Equal(0, u.Calls));
        Assert.Equal(37, state.Usage!.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderEmptyRepairRecoveryResumesConstructionAfterRestart(bool retry)
    {
        var state = Fixture(); state.Status = retry ? PlanningStatus.Recovery : PlanningStatus.Generating;
        state.CurrentPhase = "repair_fragment";
        state = JsonSerializer.Deserialize(JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;
        if (retry) state = await Send(state, "retry");
        Assert.Equal(PlanningStatus.Generating, state.Status);
        state = await Send(state, "advance");
        Assert.Equal(0, state.RepairAttempt); Assert.NotEmpty(state.ConstructionUnits);
        Assert.Equal(37, state.Usage!.Calls); Assert.NotNull(state.ApprovedBehaviorHash);
    }

    [Fact]
    public async Task ManualYamlRetryStillValidatesTheEditedArtifact()
    {
        var state = Fixture(); state.Status = PlanningStatus.Recovery; state.Yaml = "retained manual artifact";
        state = await Send(state, "retry");
        Assert.Equal(PlanningStatus.Validating, state.Status); Assert.Empty(state.ConstructionUnits);
        Assert.Equal("retained manual artifact", state.Yaml);
    }

    private static PlanningSnapshot Fixture()
    {
        var state = ConstructionUnitTests.ApprovedSkeleton(); state.RepairAttempt = 3; state.NonImprovingAttempts = 2;
        state.PreviousDiagnosticHash = "old"; state.BestDiagnostics = [new("OLD", "/workflows", "Old candidate")];
        state.Usage = new() { Calls = 37 }; state.Answers.Add(new("Retained question", new JsonObject { ["answer"] = "confirmed policy" }));
        state.Attempts.Add(new("old", "repair_unit", 0, false, state.BestDiagnostics.ToList()));
        return state;
    }

    private static Task<PlanningSnapshot> Send(PlanningSnapshot state, string command, string? text = null) => new TypedWorkflowPlanner().AdvanceAsync(state,
        new() { Kind = command, Text = text, ArtifactHash = state.ArtifactHash, ExpectedRevision = state.Revision },
        new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("No model request is needed for this transition.") }, TestContext.Current.CancellationToken);
}
