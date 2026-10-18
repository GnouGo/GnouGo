using System.Text.Json.Nodes;
using System.Text.Json;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningBusinessJudgeAuditTests
{
    [Fact]
    public async Task IdenticalImmutableContextReusesItsReviewAfterCheckpoint()
    {
        var (state, runtime) = await ReviewedAsync();
        state = PlannerFixture.Clone(state);
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(2, state.ModelCalls);
        Assert.Single(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray());
        Assert.Single(state.ValidationResults, result => result.Id == "judge:summary");
    }

    [Theory]
    [InlineData("request")]
    [InlineData("requirements")]
    [InlineData("contract")]
    [InlineData("plan")]
    public async Task ChangedReviewEvidenceRequiresItsOwnIndependentReview(string changed)
    {
        var (state, runtime) = await ReviewedAsync();
        switch (changed)
        {
            case "request": state.Request.Prompt += " Keep the agreed presentation."; break;
            case "requirements": state.Requirements!.Summary += " Retain the agreed presentation."; break;
            case "contract": state.Discovery.Resolved.Add(new() { Id = "inspected-operation", Version = "2",
                InputSchema = new JsonObject { ["type"] = "object" } }); break;
            case "plan": state.Plan!.Root.Tasks[0].Outputs[0].Value.Text = "A different greeting"; break;
        }
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(3, runtime.Calls.Count);
        Assert.Equal(3, state.ModelCalls);
        Assert.Equal(2, state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray().Count);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task InvalidJsonPointerEscapeCannotGroundAnAdvisoryFinding()
    {
        var (state, runtime) = await ReviewedAsync();
        state.Discovery.Resolved.Add(new() { Id = "inspected-operation", Version = "1",
            InputSchema = new JsonObject { ["literal~2key"] = "Declared evidence" } });
        runtime.Respond = (_, _) =>
        {
            var report = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0");
            report["findings"]![0]!["evidence"] = new JsonArray("/contracts/0/inputSchema/literal~2key");
            return new() { Json = report };
        };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Contains(state.ValidationResults, result => result.Id == "judge:summary" && result.Outcome == "inconclusive");
        Assert.Null(state.EditablePaths);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task MixedFindingsCannotExtendAnExplicitValueSlotToOtherWork()
    {
        var (state, runtime) = await ReviewedAsync();
        state.Request.Prompt += " Review the retained value.";
        state.Request.Options["automatic_correction"] = true;
        state.Request.Options[PlanningBusinessJudge.AuthorityKey] = new JsonArray("/tasks/greet/outputs/message");
        runtime.Respond = (_, _) =>
        {
            var report = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0/outputs/0");
            report["findings"]!.AsArray().Add(PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0")["findings"]![0]!.DeepClone());
            return new() { Json = report };
        };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "JUDGE_SCOPED_REVIEW");
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "BUSINESS_REVIEW_REQUIRED");
        Assert.Null(state.EditablePaths);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal("Return a greeting", state.Plan!.Root.Tasks[0].Objective);
    }

    [Theory]
    [InlineData("/taskPlan/root/tasks/0/outputs/0/value/text", true)]
    [InlineData("/taskPlan/root/tasks/0/outputs/0/value", true)]
    [InlineData("/taskPlan/root/tasks/0/objective", false)]
    [InlineData("/taskPlan/root/tasks/0/condition", false)]
    public async Task GroundedDescendantUsesOnlyItsExistingAuthorizedValueSlot(string location, bool authorized)
    {
        var (state, runtime) = await ReviewedAsync();
        state.Request.Prompt += " Review the retained greeting value.";
        state.Request.Options["automatic_correction"] = true;
        state.Request.Options[PlanningBusinessJudge.AuthorityKey] = new JsonArray("/tasks/greet/outputs/message");
        runtime.Respond = (_, _) => new() { Json = PlanningBusinessJudgeTests.Report(location) };
        await PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct);
        var diagnostic = Assert.Single(state.Diagnostics);
        Assert.Equal(authorized ? "JUDGE_SCOPED_REVIEW" : "BUSINESS_REVIEW_REQUIRED", diagnostic.Code);
        if (authorized)
        {
            Assert.Equal("/tasks/greet/outputs/message", diagnostic.Location);
            Assert.Equal(["/tasks/greet/outputs/message"], state.EditablePaths);
        }
        else Assert.Null(state.EditablePaths);
        Assert.Equal(0, state.ReplanAttempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RetainedJudgeReceiptUsesItsIssuedPromptAfterAnInstructionVersionChange(int version)
    {
        var state = PlannerFixture.Session();
        // A persisted session predating fresh version capture must keep its v1
        // context and issued request; a new session captures the current v2.
        if (version == 1) state.Revision = 1;
        state.Request.Options["business_judge"] = true;
        var runtime = new TestRuntime();
        runtime.Respond = (request, purpose) => purpose == "judge"
            ? throw new IOException("Interrupted after the receipt was retained.")
            : TestRuntime.Response(request, runtime.Proposal);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Equal("judge", state.PendingCall!.Purpose);
        Assert.Equal(version.ToString(System.Globalization.CultureInfo.InvariantCulture), state.Request.Options["business_judge_pending_context"]!["version"]!.ToString());
        // Model a receipt issued by an earlier private instruction version while
        // retaining the captured immutable context, output schema and accounting.
        state = PlannerFixture.Clone(state);
        state.PendingCall!.Request.Prompt = "Retained review instructions from the issued request.\n" + state.PendingCall.Request.Prompt;
        state.PendingCall.Request.ClientRequestId = null;
        var issuedId = state.Request.SessionId + ":2:" + PlanningGraphCompiler.Fingerprint(
            JsonSerializer.Serialize(state.PendingCall.Request, PlanningJsonContext.Default.LLMRequest));
        state.PendingCall.Request.ClientRequestId = issuedId;
        state.PendingCall.Id = issuedId;
        var issuedPrompt = state.PendingCall.Request.Prompt;
        runtime.Respond = (request, purpose) =>
        {
            Assert.Equal("judge", purpose);
            Assert.Equal(issuedId, request.ClientRequestId);
            Assert.Equal(issuedPrompt, request.Prompt);
            return new() { Json = PlanningBusinessJudgeTests.Report() };
        };
        state.Status = PlanningStatus.Generating;
        state.Diagnostics.Clear();
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(0, state.ReplanAttempts);
        Assert.Null(state.PendingCall);
        Assert.Null(state.Request.Options["business_judge_pending_context"]);
    }

    [Fact]
    public async Task ChangedImmutableContextCannotConsumeAPendingJudgeResponse()
    {
        var state = PlannerFixture.Session();
        state.Request.Options["business_judge"] = true;
        var runtime = new TestRuntime();
        runtime.Respond = (request, purpose) => purpose == "judge"
            ? throw new IOException("Uncertain completion.")
            : TestRuntime.Response(request, runtime.Proposal);
        state = await PlannerFixture.RunAsync(runtime, state);
        var pendingId = state.PendingCall!.Id;
        state.Request.Prompt += " A changed business requirement.";
        await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningBusinessJudge.ReviewAsync(state, runtime, PlannerFixture.Ct));
        Assert.Equal(2, runtime.Calls.Count);
        Assert.Equal(2, state.ModelCalls);
        Assert.Equal(pendingId, state.PendingCall.Id);
        Assert.Empty(state.Request.Options[PlanningBusinessJudge.HistoryKey]!.AsArray());
    }

    [Fact]
    public async Task RepeatedBusinessIncorrectCandidatesStopBeforeExhaustingCorrections()
    {
        var state = PlannerFixture.Session();
        state.IntentVersion = 3;
        state.Request.MaxModelCalls = 30;
        state.Request.MaxReplanAttempts = 4;
        state.Request.Options["business_judge"] = true;
        state.Request.Options["automatic_correction"] = true;
        var runtime = new TestRuntime();
        runtime.Proposal.Requirements!.Outputs = [new() { Name = "message", Type = new() { Kind = "string" } }];
        var proposals = 0;
        runtime.Respond = (request, purpose) =>
        {
            if (purpose == "judge") return new() { Json = PlanningBusinessJudgeTests.Report("/taskPlan/root/tasks/0/outputs/0") };
            proposals++;
            return TestRuntime.Response(request, new()
            {
                Requirements = proposals == 1 ? runtime.Proposal.Requirements : null,
                Plan = GnOuGo.Planning.Examples.PlanningCorpus.Greeting(proposals % 2 == 0 ? "Candidate B" : "Candidate A")
            });
        };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new()
        {
            Kind = "revise", ExpectedRevision = state.Revision, ArtifactHash = state.ComputeArtifactHash(),
            Text = "Correct only the retained greeting value.", PreserveRequirements = true,
            EditablePaths = ["/tasks/greet/outputs/message"]
        }, runtime, PlannerFixture.Ct);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, diagnostic => diagnostic.Code == "REPLAN_NO_PROGRESS");
        Assert.Equal("repeated_candidate", state.Request.Options["automatic_correction_progress"]!["blocked"]!.ToString());
        Assert.InRange(state.ReplanAttempts, 1, 3);
        Assert.Null(state.ApprovedHash);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> ReviewedAsync()
    {
        var state = PlannerFixture.Session();
        state.Request.Options["business_judge"] = true;
        var runtime = new TestRuntime();
        runtime.Respond = (request, purpose) => purpose == "judge"
            ? new() { Json = PlanningBusinessJudgeTests.Report() }
            : TestRuntime.Response(request, runtime.Proposal);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status);
        return (state, runtime);
    }
}
