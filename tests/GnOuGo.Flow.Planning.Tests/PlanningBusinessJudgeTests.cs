using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningBusinessJudgeTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task OptionalJudgeSharesCallsAndNeverCertifiesBusinessCorrectness(bool enabled, int calls)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Options["business_judge"] = enabled;
        runtime.Respond = (request, purpose) => purpose == "judge" ? new() { Json = Report() } : TestRuntime.Response(request, runtime.Proposal);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(calls, runtime.Calls.Count);
        Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
        if (enabled)
        {
            var review = Assert.Single(state.ValidationResults, r => r.Id == "judge:summary");
            Assert.Equal("unverified", review.Outcome);
            var issued = runtime.Calls[1]; Assert.True(issued.StructuredOutputStrict); Assert.Null(issued.Tools);
            Assert.Equal(runtime.Calls[0].Model, issued.Model);
            Assert.Contains("Return a greeting", issued.Prompt);
            Assert.DoesNotContain("automatic_correction_progress", issued.Prompt);
        }
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(calls, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("/taskPlan/root/tasks/0", "revision_required")]
    [InlineData("/taskPlan/root/tasks/999", "inconclusive")]
    public async Task ObservationsNeverGrantEditAuthority(string location, string outcome)
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.Options["business_judge"] = true; state.Request.Options["automatic_correction"] = true;
        runtime.Respond = (request, purpose) => purpose == "judge" ? new() { Json = Report(location) } : TestRuntime.Response(request, runtime.Proposal);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.ValidationResults, r => r.Id == "judge:summary" && r.Outcome == outcome);
        Assert.Equal(outcome == "revision_required" ? PlanningStatus.Stopped : PlanningStatus.FinalReview, state.Status);
        Assert.Null(state.EditablePaths); Assert.Null(state.ApprovedHash);
        Assert.True(JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(runtime.Proposal.Plan, PlanningJsonContext.Default.TaskPlan),
            System.Text.Json.JsonSerializer.SerializeToNode(state.Plan, PlanningJsonContext.Default.TaskPlan)));
    }

    [Fact]
    public async Task ExhaustedAllowanceRecordsSkippedJudgeWithoutAnotherCall()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.Request.MaxModelCalls = 1; state.Request.Options["business_judge"] = true;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls);
        Assert.Contains(state.ValidationResults, r => r.Id == "judge:summary" && r.Outcome == "not_evaluated");
    }

    [Fact]
    public async Task ExplicitScopeAllowsJudgeGuidedCorrectionAndFullRevalidation()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session();
        state.IntentVersion = 3; state.Request.MaxReplanAttempts = 4; state.Request.MaxModelCalls = 12;
        state.Request.Options["business_judge"] = true; state.Request.Options["automatic_correction"] = true;
        runtime.Proposal.Requirements!.Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }];
        var generations = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == "judge") return new() { Json = Report(generations < 3 ? "/taskPlan/root/tasks/0/outputs/0" : null) };
            generations++;
            var plan = GnOuGo.Planning.Examples.PlanningCorpus.Greeting(generations < 3 ? "Hello" : "Corrected");
            return TestRuntime.Response(request, new() { Requirements = generations == 1 ? runtime.Proposal.Requirements : null, Plan = plan });
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal(0, state.ReplanAttempts);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "revise", ExpectedRevision = state.Revision,
            ArtifactHash = state.ComputeArtifactHash(), Text = "Correct the greeting value only.", PreserveRequirements = true,
            EditablePaths = ["/tasks/greet/outputs/message"] }, runtime, PlannerFixture.Ct);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join(';', state.Diagnostics) + state.Request.Options.ToJsonString());
        Assert.Equal("Corrected", state.Plan!.Root.Tasks[0].Outputs[0].Value.Text);
        Assert.Equal("Return a greeting", state.Plan.Root.Tasks[0].Objective);
        Assert.Equal("8", state.Request.Options["automatic_correction_progress"]!["version"]!.ToString());
        Assert.InRange(state.ReplanAttempts, 1, 4);
        Assert.Equal(generations + state.Request.Options["business_judge_reviews"]!.AsArray().Count, state.ModelCalls);
        Assert.Contains(state.ValidationResults, r => r.Id == "static" && r.Outcome == "passed");
    }

    [Fact]
    public async Task JudgeReceiptRecoveryKeepsTheIssuedIdentityAndDoesNotChargeAgain()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["business_judge"] = true;
        string? requestId = null; var physical = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose != "judge") { physical++; return TestRuntime.Response(request, runtime.Proposal); }
            if (requestId is null) { requestId = request.ClientRequestId; physical++; throw new IOException("committed receipt, interrupted checkpoint"); }
            Assert.Equal(requestId, request.ClientRequestId); return new() { Json = Report() };
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Equal("judge", state.PendingCall!.Purpose);
        state = PlannerFixture.Clone(state); state.Status = PlanningStatus.Generating; state.Diagnostics.Clear();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, physical);
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.PendingCall);
    }

    [Fact]
    public async Task InvalidJudgeResponseDoesNotBecomeAPlanRepair()
    {
        var runtime = new TestRuntime(); var state = PlannerFixture.Session(); state.Request.Options["business_judge"] = true;
        runtime.Respond = (request, purpose) => purpose == "judge" ? new() { Json = new JsonObject { ["plan"] = "invented" } } : TestRuntime.Response(request, runtime.Proposal);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Contains(state.ValidationResults, r => r.Id == "judge:summary" && r.Outcome == "inconclusive");
    }

    internal static JsonObject Report(string? location = null) => new()
    {
        ["uncertainty"] = "Execution has not been observed.",
        ["findings"] = location is null ? new JsonArray() : new JsonArray(new JsonObject
        {
            ["kind"] = "inconsistency", ["location"] = location,
            ["evidence"] = new JsonArray("/request"), ["explanation"] = "The intended business content may differ.",
            ["uncertainty"] = "Human intent needs confirmation."
        })
    };
}
