using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CombinedPlanningDiagnosticsTests
{
    [Fact]
    public async Task IndependentOutputAndLineageFailuresAreReportedBeforeAnyRepair()
    {
        var (runtime, state) = await Fixture();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Single(runtime.Calls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED" && d.Location == "/outputs/report");
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENT_RESULT_DISCONNECTED" && d.Location == "/root/outputs/path");
        Assert.Contains(state.ValidationResults, r => r.Outcome == "violation");
        Assert.Null(state.Yaml);
        Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task OneScopedRepairCanFixBothWithoutChangingIndependentWork()
    {
        var (runtime, state) = await Fixture();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        var original = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        runtime.Proposal.Plan!.Root.Outputs[0] = new("report", CommitmentCoverageTests.Output("observe", "verified"));
        runtime.Proposal.Plan.Root.Outputs[2] = new("path", CommitmentCoverageTests.Output("observe", "verified"));
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(1, state.ReplanAttempts);
        Assert.Equal(original, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        var progress = state.Request.Options["automatic_correction_progress"]!;
        Assert.Equal("8", progress["version"]!.ToString());
        var locations = progress["locations"]!.AsArray().Select(n => n!.ToString()).ToArray();
        Assert.Equal(new[] { "/root/outputs/path", "/root/outputs/report" }, locations);
        Assert.Equal("success", state.Plan!.Root.Outputs[1].Value.Port);
        PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task HistoricalSessionKeepsSequentialDiagnosisAndDoesNotAcquireProfiles()
    {
        var (runtime, state) = await Fixture();
        PlannerFixture.WithoutCapturedProfiles(state);
        state.Request.Options["compilation_profile"] = TaskPlanCompiler.CompactProfile;
        state.Revision = 1;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_OUTPUTS_CHANGED");
        Assert.DoesNotContain(state.Diagnostics, d => d.Code == "REQUIREMENT_RESULT_DISCONNECTED");
        Assert.Null(state.Request.Options["planning_diagnostics_version"]);
        Assert.Null(state.Request.Options["business_judge_review_version"]);
    }

    [Fact]
    public async Task CombinedScopeCannotChangeAnIndependentStatus()
    {
        var (runtime, state) = await Fixture();
        state.Request.MaxReplanAttempts = 1;
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        runtime.Proposal.Plan!.Root.Outputs[0] = new("report", CommitmentCoverageTests.Output("observe", "verified"));
        runtime.Proposal.Plan.Root.Outputs[2] = new("path", CommitmentCoverageTests.Output("observe", "verified"));
        runtime.Proposal.Plan.Root.Outputs[1] = new("finished", new() { Kind = "boolean", Boolean = true });
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Null(state.Yaml);
        Assert.Equal("success", state.Plan!.Root.Outputs[1].Value.Port);
        Assert.Contains(state.Diagnostics, d => d.Code is "PLANNING_RESPONSE_INVALID" or "REVISION_SCOPE_CHANGED");
    }

    private static async Task<(TestRuntime Runtime, PlanningSession State)> Fixture()
    {
        var (runtime, state) = await OutputBindingRoutingTests.Fixture();
        runtime.Proposal.Requirements!.Outputs!.Add(new() { Name = "path", Type = new() { Kind = "string" } });
        var operation = runtime.Proposal.Plan!.Root.Tasks[0].Operation!;
        runtime.Proposal.Requirements.Outcomes = [new("path", "Return the producer-owned path")
            { Commitments = [new(operation) { Result = new("verified", "path") }] }];
        runtime.Proposal.Plan.Root.Outputs.Add(new("path", new() { Kind = "string", Text = "unverified-path" }));
        return (runtime, state);
    }
}
