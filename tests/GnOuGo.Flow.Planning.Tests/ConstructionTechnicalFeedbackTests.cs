using System.Text;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class ConstructionTechnicalFeedbackTests
{
    [Fact]
    public async Task RetainedTechnicalValueDoesNotGrantRepairAuthorityOrReactivateARejectedDiagnostic()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate!;
        candidate.Diagnostics = [new("TASK_INPUT_TYPE", "/tasks/compute/outputs/result", "The supplied value was invalid.")];
        PlanningConstruction.RetainTechnicalRejections(state);
        var original = candidate.Plan.Root.Tasks.Single().Outputs[0].Value.Items[1].Number;
        candidate.Diagnostics = [new("CONSTRUCTION_BUSINESS_REVIEW", "/tasks/compute", "Resolve the current business concern.")];
        var history = PlanningConstruction.TechnicalRejectionContext(state)!;
        var entry = Assert.Single(history["entries"]!.AsArray())!;
        Assert.Equal("TASK_INPUT_TYPE", entry["diagnostic"]!["value"]!["code"]!.ToString());
        Assert.Equal("arithmetic", entry["rejectedValue"]!["value"]!["kind"]!.ToString());
        Assert.Equal("historical", entry["status"]!.ToString());
        Assert.Equal(original, candidate.Plan.Root.Tasks.Single().Outputs[0].Value.Items[1].Number);
        Assert.Null(candidate.AuthorizedPaths);
        Assert.Single(candidate.Diagnostics, diagnostic => diagnostic.Code == "CONSTRUCTION_BUSINESS_REVIEW");
        Assert.DoesNotContain(candidate.Diagnostics, diagnostic => diagnostic.Code == "TASK_INPUT_TYPE");
    }

    [Fact]
    public async Task ReplacementKeepsTechnicalFeedbackWithinItsOriginalSourceAndBaseline()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var before = state.Construction!.Candidate!;
        before.Diagnostics = [new("TASK_INPUT_TYPE", "/tasks/compute/outputs/result", "Invalid value")];
        PlanningConstruction.RetainTechnicalRejections(state);
        var next = new PlanningConstructionCandidate { Plan = PlanningConstruction.Clone(before.Plan), BaseHash = before.BaseHash,
            Hash = before.Hash, TaskIds = before.TaskIds, Extension = before.Extension, OpenScopes = before.OpenScopes,
            Diagnostics = [new("CONSTRUCTION_BUSINESS_REVIEW", "/tasks/compute", "Current concern")] };
        PlanningConstruction.CarryDefectWitnesses(state, before, next);
        state.Construction.Candidate = next;
        state = BusinessReviewProtocolTests.Clone(state);
        Assert.Equal("historical", Assert.Single(PlanningConstruction.TechnicalRejectionContext(state)!["entries"]!.AsArray())!["status"]!.ToString());
        state.Construction!.Compact!.Sources[0].Text += "Changed source";
        Assert.Equal("stale_basis", Assert.Single(PlanningConstruction.TechnicalRejectionContext(state)!["entries"]!.AsArray())!["status"]!.ToString());
        Assert.Null(state.Construction.Candidate!.AuthorizedPaths);
    }

    [Fact]
    public async Task TechnicalHistoryHasExplicitBoundsAndUnknownLocationsDoNotInventValues()
    {
        var (state, _) = await BusinessReviewProtocolTests.Ready();
        var candidate = state.Construction!.Candidate!;
        for (var index = 0; index < 8; index++)
        {
            candidate.Diagnostics = [new("TASK_TEST_" + index, "/unknown/" + index, "Bounded diagnostic")];
            PlanningConstruction.RetainTechnicalRejections(state);
        }
        var history = PlanningConstruction.TechnicalRejectionContext(state)!;
        Assert.InRange(history["entries"]!.AsArray().Count, 1, 3);
        Assert.True(history["omitted"]!.GetValue<int>() > 0);
        Assert.InRange(Encoding.UTF8.GetByteCount(history.ToJsonString()), 1, 6_144);
        Assert.All(history["entries"]!.AsArray(), entry =>
        {
            Assert.False(entry!["valueAvailable"]!.GetValue<bool>());
            Assert.Null(entry["rejectedValue"]);
        });
    }
}
