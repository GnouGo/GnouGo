using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class SameBlockBoundaryTransitionTests
{
    [Fact]
    public async Task ExactAcceptedExportsCloseNestedBoundariesWithoutCompletingBusinessBlock()
    {
        var (state, runtime) = await AcceptedExports();
        var block = state.Construction!.Compact!.CurrentBlockId;
        for (var index = 0; index < 4; index++)
        {
            state = await BusinessReviewProtocolTests.Advance(PlannerFixture.Clone(state), runtime);
            Assert.Empty(state.Diagnostics);
        }
        Assert.Equal(["/root"], state.Construction!.OpenScopes);
        Assert.Equal(block, state.Construction.Compact!.CurrentBlockId);
        Assert.Empty(runtime.Calls);
        Assert.Equal(3, state.Construction.Checkpoints.Count);
        Assert.All(state.Construction.Checkpoints, checkpoint => Assert.True(checkpoint.Fragment["businessReviewPending"]!.GetValue<bool>()));
        Assert.All(state.Construction.Requirements, requirement => Assert.NotEqual("closed", requirement.Status));
        var consumer = new PlanningConstructionStep { Scope = "/root", Inputs = [new() { Kind = "output", Source = "outer", Port = "result" }] };
        Assert.Single(PlanningCompactConstruction.ImportedInterfaces(state, consumer));
    }

    [Theory]
    [InlineData("different_block")]
    [InlineData("changed_baseline")]
    [InlineData("invalidated")]
    [InlineData("waiting_consumer")]
    [InlineData("changed_claim")]
    public async Task UnreviewedBoundaryClaimCannotEscapeItsExactAuthority(string change)
    {
        var (state, _) = await AcceptedExports(waiting: change == "waiting_consumer");
        switch (change)
        {
            case "different_block": state.Construction!.Checkpoints[0].Step!.BlockId = "other"; break;
            case "changed_baseline": state.Plan!.Root.Tasks.Add(new() { Id = "unrelated", Kind = "value", Objective = "Unrelated accepted contribution",
                Outputs = [new("number", new() { Kind = "number", Number = 1 })] }); break;
            case "invalidated": state.Construction!.Checkpoints[0].InvalidatedTaskIds.Add("number"); break;
            case "changed_claim": state.Construction!.Checkpoints[0].Step!.BoundaryTransitions![0].CloseScope = false; break;
        }
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.Null(PlanningCompactConstruction.PrepareAcceptedTransition(state));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
    }

    private static async Task<(PlanningSession, TestRuntime)> AcceptedExports(bool waiting = false)
    {
        var (state, runtime) = await BusinessReviewProtocolTests.Ready();
        state.Construction!.Candidate = null;
        state.Plan!.Root.Tasks = [new() { Id = "outer", Kind = "sequence", Objective = "Retain the requested ordered work", Body = new()
            { Tasks = [new() { Id = "inner", Kind = "sequence", Objective = "Retain the requested nested computation", Body = new() }] } }];
        state.Construction.OpenScopes = ["/root", "/tasks/outer/body", "/tasks/inner/body"];
        var compact = state.Construction.Compact!;
        compact.CurrentStep!.Scope = "/tasks/inner/body"; compact.CurrentStep.BaseHash = PlanningConstruction.Hash(state.Plan);
        if (waiting)
        {
            state.Diagnostics = [new("CONSTRUCTION_TENTATIVE_SELECTION", "/step", "The waiting consumer still needs its exact input")];
            PlanningCompactConstruction.Reselect(state, new PlanningConstructionReselection { Reason = "Obtain the exact input", Disposition = "suspend",
                Prerequisite = new() { Need = "The exact numeric input", ExpectedType = new() { Kind = "number" }, EvidenceAddresses = ["requirement:work"] } });
            compact.ReplacementScope = null; compact.ReplacementRequirements = null;
            compact.CurrentStep = new() { Id = "independent", BlockId = compact.CurrentBlockId, Kind = "value", Scope = "/tasks/inner/body",
                RequirementIds = ["work"], BaseHash = PlanningConstruction.Hash(state.Plan), DiscoveryComplete = true };
        }
        state.Construction.Candidate = PlanningConstruction.Apply(state, new() { Scope = "/tasks/inner/body", RequirementIds = ["work"], CompleteBlock = false,
            Tasks = [new() { Id = "number", Kind = "value", Objective = "Compute the exact original numeric result", Outputs = [new("result", new() { Kind = "number", Number = 7 })] }],
            BoundaryTransitions = [new() { Scope = "/tasks/inner/body", RequirementIds = ["work"], CloseScope = true,
                Outputs = [new("result", new() { Kind = "output", Source = "number", Port = "result" })] },
                new() { Scope = "/tasks/outer/body", RequirementIds = ["work"], CloseScope = true,
                Outputs = [new("result", new() { Kind = "output", Source = "inner", Port = "result" })] }] });
        PlanningCompactConstruction.ValidateExtension(state, state.Construction.Candidate);
        state.Diagnostics.Clear();
        state = await BusinessReviewProtocolTests.Advance(state, runtime);
        Assert.Empty(state.Diagnostics); Assert.Empty(runtime.Calls);
        return (state, runtime);
    }
}
