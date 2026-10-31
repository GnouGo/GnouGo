using System.Text.Json;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CombinedPlanningDiagnosticsTests
{
    [Fact]
    public async Task IndependentOutputAndLineageFailuresAreReportedBeforeAnyRepair()
    {
        var state = await Fixture();
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && diagnostic.Location == "/outputs/report");
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REQUIREMENT_RESULT_DISCONNECTED" && diagnostic.Location == "/root/outputs/path");
        Assert.Contains(PlanningCommitments.Assess(state), result => result.Outcome == "violation");
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash); Assert.Equal(0, state.ModelCalls);
    }

    [Fact]
    public async Task OneTypedRepairCanFixBothWithoutChangingIndependentWork()
    {
        var state = await Fixture(); var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var request = RepairPatchTests.Request(state);
        Assert.Equal(new[] { "/root/outputs/path", "/root/outputs/report" },
            PlanningRepairPatch.RequestContext(request)["repair"]!["slots"]!.AsArray().Select(slot => slot!["location"]!.ToString()));
        var corrected = OutputBindingRoutingTests.Apply(state,
            RepairPatchTests.Edit(state, "/root/outputs/path", "replace", OutputBindingRoutingTests.Node(OutputBindingRoutingTests.Output("observe", "verified"))),
            RepairPatchTests.Edit(state, "/root/outputs/report", "replace", OutputBindingRoutingTests.Node(OutputBindingRoutingTests.Output("observe", "verified"))));
        var fixedState = PlannerFixture.Clone(state); fixedState.Plan = corrected;
        Assert.Empty(OutputBindingRoutingTests.Findings(fixedState));
        Assert.Equal(original, JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal("success", corrected.Root.Outputs[1].Value.Port);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task CombinedScopeCannotChangeAnIndependentStatus()
    {
        var state = await Fixture(); var modified = PlannerFixture.Clone(state).Plan!;
        modified.Root.Outputs[0] = new("report", OutputBindingRoutingTests.Output("observe", "verified"));
        modified.Root.Outputs[2] = new("path", OutputBindingRoutingTests.Output("observe", "verified"));
        modified.Root.Outputs[1] = new("finished", new() { Kind = "boolean", Boolean = true });
        Assert.Contains(TaskPlanRevisions.Validate(state.Plan, modified, state.RevisionScope, state.Catalog), diagnostic => diagnostic.Code == "REVISION_SCOPE_CHANGED");
        Assert.Equal("success", state.Plan!.Root.Outputs[1].Value.Port);
    }

    private static async Task<PlanningSession> Fixture()
    {
        var state = await OutputBindingRoutingTests.Fixture();
        state.Requirements!.Outputs!.Add(new() { Name = "path", Type = new() { Kind = "string" } });
        var operation = state.Plan!.Root.Tasks[0].Operation!;
        state.Requirements.Outcomes = [new("path", "Return the producer-owned path") { Commitments = [new(operation) { Result = new("verified", "path") }] }];
        state.Plan.Root.Outputs.Add(new("path", new() { Kind = "string", Text = "unverified-path" }));
        state.Diagnostics = OutputBindingRoutingTests.Findings(state); OutputBindingRoutingTests.AuthorizeOutputRepairs(state);
        return state;
    }
}
