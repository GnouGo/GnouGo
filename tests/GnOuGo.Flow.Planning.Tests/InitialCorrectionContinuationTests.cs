using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class InitialCorrectionContinuationTests
{
    [Theory]
    [InlineData("nullable")]
    [InlineData("reference")]
    [InlineData("coverage")]
    public async Task AcceptedInitialContractsPermitOneRemainingScopedCorrection(string defect)
    {
        var (runtime, state) = await Fixture(defect);
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        Assert.Null(state.Plan); Assert.Null(state.Requirements);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.NotNull(state.Plan); Assert.NotNull(state.Requirements); Assert.NotEmpty(state.Diagnostics);
        var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics));
        Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts); Assert.Equal(3, runtime.Calls.Count);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.True(PlanningRepairPatch.Issued(runtime.Calls[2].StructuredOutputSchema!.AsObject()));
        Assert.Equal("/root/outputs/report", Assert.Single(PlanningRepairPatch.RequestContext(runtime.Calls[2])["repair"]!["slots"]!.AsArray())!["location"]!.ToString());
        var progress = state.Request.Options["automatic_correction_progress"]!;
        Assert.Equal(new[] { "regenerate", "targeted_revision" }, progress["attempts"]!.AsArray().Select(a => a!["action"]!.ToString()));
        Assert.Null(state.ApprovedHash); PlanningArtifactApproval.Verify(state);
    }

    [Theory]
    [InlineData("repeat")]
    [InlineData("unrelated")]
    [InlineData("uncertain")]
    public async Task FollowupCannotRegenerateAgainOrPublishAnInvalidPatch(string failure)
    {
        var (runtime, state) = await Fixture("nullable", failure);
        state.Request.MaxReplanAttempts = 5;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(3, runtime.Calls.Count);
        Assert.Equal(3, state.ModelCalls); Assert.Equal(2, state.ReplanAttempts); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.Equal("observe", state.Request.Baseline!.Root.Outputs[1].Value.Source);
        if (failure == "uncertain")
        { Assert.NotNull(state.PendingCall); Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_DISPATCH_UNVERIFIABLE"); }
        else Assert.Null(state.PendingCall);
        var serialized = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        var restored = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state), new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(serialized, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(3, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("repairs")]
    [InlineData("historical")]
    [InlineData("unaccepted")]
    [InlineData("structure")]
    public async Task FollowupRequiresCapacityAcceptedIntentAndAnExistingValueScope(string blocker)
    {
        var (runtime, state) = await Fixture(blocker);
        if (blocker == "calls") state.Request.MaxModelCalls = 2;
        if (blocker == "repairs") state.Request.MaxReplanAttempts = 1;
        var planner = new HybridWorkflowPlanner();
        state = await planner.AdvanceAsync(state, new(), runtime, PlannerFixture.Ct);
        state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        if (blocker == "historical") state.Request.Options["automatic_correction_progress"]!["version"] = 4;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(2, runtime.Calls.Count); Assert.Equal(1, state.ReplanAttempts);
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
    }

    [Fact]
    public async Task CommittedFollowupKeepsOriginalScopeAndAccountingOnRecovery()
    {
        var (runtime, state) = await Fixture("nullable");
        var completed = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, completed.Status);
        var pending = PlannerFixture.Clone(runtime.Checkpoints.Last(s => s.PendingCall is not null));
        Assert.Equal(3, pending.ModelCalls); Assert.Equal(2, pending.ReplanAttempts);
        var issued = JsonSerializer.Serialize(pending.PendingCall!.Request, PlanningJsonContext.Default.LLMRequest);
        var response = new LLMResponse { Json = TestRuntime.PatchResponse(pending.PendingCall.Request, pending, runtime.Proposal.Plan!) };
        var reads = 0;
        runtime.Respond = (request, _) =>
        { Assert.Equal(0, reads++); Assert.Equal(issued, JsonSerializer.Serialize(request, PlanningJsonContext.Default.LLMRequest)); return response; };
        var recovered = await new HybridWorkflowPlanner().AdvanceAsync(pending, new() { ExpectedRevision = pending.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, recovered.Status); Assert.Equal(completed.Yaml, recovered.Yaml);
        Assert.Equal(3, recovered.ModelCalls); Assert.Equal(2, recovered.ReplanAttempts); Assert.Equal(1, reads);
    }

    private static async Task<(TestRuntime Runtime, PlanningSession State)> Fixture(string defect, string? failure = null)
    {
        var (runtime, state) = await OutputBindingRoutingTests.Fixture();
        var broken = JsonSerializer.SerializeToNode(runtime.Proposal, PlanningJsonContext.Default.PlanningProposal)!.Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
        if (defect == "reference") broken.Plan!.Root.Outputs[0] = new("report", CommitmentCoverageTests.Output("observe", "unknown"));
        if (defect is "coverage" or "structure") broken.Plan!.Root.Outputs[0] = new("report", CommitmentCoverageTests.Text("fabricated"));
        if (defect == "structure")
        { broken.Plan!.Root.Tasks.Clear(); broken.Plan.Root.Outputs[1] = new("finished", new() { Kind = "boolean", Boolean = true }); }
        runtime.Proposal.Plan!.Root.Outputs[0] = new("report", CommitmentCoverageTests.Output("observe", "verified"));
        runtime.Respond = (request, _) =>
        {
            if (runtime.Calls.Count == 3 && failure == "uncertain") throw new InvalidOperationException("Unknown provider completion");
            var proposal = runtime.Calls.Count < 3 || failure == "repeat" ? broken : runtime.Proposal;
            if (runtime.Calls.Count == 3 && failure == "unrelated")
            {
                proposal = JsonSerializer.SerializeToNode(proposal, PlanningJsonContext.Default.PlanningProposal)!.Deserialize(PlanningJsonContext.Default.PlanningProposal)!;
                proposal.Plan!.Root.Outputs[1] = new("finished", new() { Kind = "boolean", Boolean = true });
            }
            var response = TestRuntime.Response(request, proposal);
            if (runtime.Calls.Count == 1 || defect == "unaccepted")
                response.Json!["requirements"]!["outcomes"]![0]!["commitments"]![0]!["result"]!["output"] = "workspace/report.json";
            return response;
        };
        return (runtime, state);
    }
}
