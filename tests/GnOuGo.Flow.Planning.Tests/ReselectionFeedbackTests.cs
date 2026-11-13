using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ReselectionFeedbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedChoiceRemainsVisibleAtDiscoveryAndDraftAfterReload(bool diagnostic)
    {
        var (state, _) = FocusedDiscoveryTests.Ready(new("renamed-source", 48));
        if (diagnostic) state.Request.Options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        var step = state.Construction!.Compact!.CurrentStep!; step.Kind = "value";
        const string reason = "Copying the prepared record does not publish it through an authorized communication operation.";
        state.Diagnostics = [new("CONSTRUCTION_RESELECTION", "/step", reason)];
        PlanningCompactConstruction.Reselect(state, reason);
        Assert.Equal(reason, PlanningCompactConstruction.ImplementationContext(state)["reselection"]?["reason"]?.ToString());
        var selected = JsonSerializer.Deserialize(JsonSerializer.Serialize(step, PlanningJsonContext.Default.PlanningConstructionStep), PlanningJsonContext.Default.PlanningConstructionStep)!;
        selected.Id = "next-step"; selected.Kind = null; selected.DiscoveryComplete = false;
        state.Construction.Compact.CurrentStep = selected;
        state = PlannerFixture.Clone(state);
        Assert.Equal(reason, PlanningCompactConstruction.DiscoveryContext(state)["reselection"]?["reason"]?.ToString());
        Assert.Equal("rejected:0", PlanningCompactConstruction.DiscoveryContext(state)["reselection"]?["evidence"]?.ToString());
        selected = state.Construction!.Compact!.CurrentStep!; selected.Kind = "value"; selected.DiscoveryComplete = true;
        Assert.Equal(reason, PlanningCompactConstruction.Context(state)["reselection"]?["reason"]?.ToString());
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("block")]
    [InlineData("scope")]
    [InlineData("requirements")]
    public void UnrelatedRejectionCannotBecomeCurrentFeedback(string mismatch)
    {
        var (state, _) = FocusedDiscoveryTests.Ready(new("unrelated-source", 1));
        var step = state.Construction!.Compact!.CurrentStep!;
        state.Diagnostics = [new("CONSTRUCTION_RESELECTION", "/step", "Previous finding")];
        PlanningCompactConstruction.Reselect(state, "Previous finding");
        state.Construction.Compact.CurrentStep = step;
        switch (mismatch)
        {
            case "baseline": step.BaseHash = "different-baseline"; break;
            case "block": step.BlockId = "different-block"; break;
            case "scope": step.Scope = "/different-scope"; break;
            default: step.RequirementIds = ["different-requirement"]; break;
        }
        Assert.Null(PlanningCompactConstruction.DiscoveryContext(state)["reselection"]);
    }
}
