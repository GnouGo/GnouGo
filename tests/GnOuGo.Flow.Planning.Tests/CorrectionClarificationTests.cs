using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CorrectionClarificationTests
{
    private static readonly PlanningQuestion Question = new("subject", "Which existing subject should the requested output describe?",
        [new("first", "The first supplied subject"), new("second", "The second supplied subject")], "first");
    private static Task<PlanningSession> Advance(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
    private static Task<PlanningSession> Answer(PlanningSession state, TestRuntime runtime) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = [new(Question.Id, "first")] }, runtime, PlannerFixture.Ct);

    [Theory]
    [InlineData(false, 2, "final_review", 3)]
    [InlineData(true, 3, "final_review", 4)]
    [InlineData(true, 2, "stopped", 3)]
    public async Task CorrectionQuestionsPauseTheSameAuthorityWithoutResettingAttempts(bool failedPatch, int allowance, string expected, int calls)
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("read", "consume", "ready", "ready},{");
        state.Request.Options["automatic_correction"] = true; state.Request.Options["clarification_policy"] = 1;
        state.Request.MaxReplanAttempts = allowance;
        state.Request.Prompt += " The subject to describe is undecided; ask which supplied subject to use rather than inventing that business preference.";
        state = await Advance(state, runtime);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        if (failedPatch) state = await Advance(state, runtime);
        runtime.Respond = (request, _) => TestRuntime.Response(request, new() { Clarifications = [Question] });
        state = await Advance(state, runtime);
        if (expected == PlanningStatus.Stopped)
        {
            Assert.Equal(expected, state.Status); Assert.Null(state.PendingQuestions);
            Assert.Contains(state.Diagnostics, d => d.Code == "CLARIFICATION_CONTINUATION_UNAVAILABLE");
        }
        else
        {
            Assert.Equal(PlanningStatus.Clarification, state.Status);
            var before = PlannerFixture.Clone(state);
            state = await Answer(PlannerFixture.Clone(state), runtime);
            Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
            Assert.Equal(before.Diagnostics, state.Diagnostics); Assert.Equal(before.EditablePaths, state.EditablePaths);
            Assert.Equal(before.ModelCalls, state.ModelCalls); Assert.Equal(before.ReplanAttempts, state.ReplanAttempts);
            Assert.True(JsonNode.DeepEquals(before.Request.Options["automatic_correction_progress"], state.Request.Options["automatic_correction_progress"]));
            Assert.Single(runtime.Checkpoints.Last().AnswerHistory!);
            runtime.Respond = null;
            runtime.Proposal.Plan!.Root.Tasks[1].Requires!.Items[1].Port = "ready";
            state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
            Assert.True(state.Status == expected, string.Join(';', state.Diagnostics));
            Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
            Assert.Contains("userAnswers", runtime.Calls.Last().Prompt);
            Assert.Equal("validated", state.Request.Options["automatic_correction_progress"]!["attempts"]!.AsArray()[^1]!["outcome"]?.ToString());
            Assert.Null(state.ApprovedHash);
        }
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(calls - 1, state.ReplanAttempts);
    }

    [Theory]
    [InlineData("interactive")]
    [InlineData("auto")]
    public async Task InitialAnswerResumesOnceAndRepeatedQuestionStops(string mode)
    {
        var runtime = new TestRuntime { Proposal = new() { Clarifications = [Question] } };
        var state = PlannerFixture.Session(); state.Request.Options["clarification_policy"] = 1; state.Request.Mode = mode;
        state = await Advance(state, runtime);
        state = await Answer(state, runtime);
        Assert.Null(state.Requirements); Assert.Single(runtime.Calls);
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Null(state.PendingQuestions);
        Assert.Contains(state.Diagnostics, d => d.Code == "CLARIFICATION_NO_PROGRESS");
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task AutoDoesNotAuthorizeRecommendationsButReusesExplicitSelection()
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Plan!.Choices = [new() { Id = "tone", Question = "Which tone?", Recommended = "brief",
            Alternatives = [new("brief", "Brief", PlanningCorpus.String("brief")), new("full", "Full", PlanningCorpus.String("full"))] }];
        runtime.Proposal.Plan.Root.Outputs.Add(new("tone", PlanningCorpus.Business("choice", "tone")));
        var state = PlannerFixture.Session(); state.Request.Mode = "auto"; state.Request.Options["clarification_policy"] = 1;
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.True(state.Status == PlanningStatus.Clarification, string.Join(';', state.Diagnostics)); Assert.Null(state.Plan!.Choices[0].Selected);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "choose", ExpectedRevision = state.Revision,
            Selections = new() { ["tone"] = "full" } }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal("full", state.Plan!.Choices[0].Selected);
        Assert.Single(runtime.Calls); Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedClarificationBaselineRejectsAnswerAtomically(bool unknownVersion)
    {
        var runtime = new TestRuntime { Proposal = new() { Clarifications = [Question] } };
        var state = PlannerFixture.Session(); state.Request.Options["clarification_policy"] = 1;
        state = await Advance(state, runtime);
        if (unknownVersion) state.Request.Options["clarification_context"]!["version"] = 2;
        else state.Requirements = PlannerFixture.Requirements();
        await Assert.ThrowsAsync<PlanningConflictException>(() => Answer(state, runtime));
        Assert.Null(state.AnswerHistory); Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData("requirements")]
    [InlineData("guard")]
    [InlineData("objective")]
    public async Task AnswerDoesNotAuthorizeUnrelatedChangesDuringRegeneration(string change)
    {
        var (runtime, state) = await MalformedGuardRevisionTests.Fixture("read", "consume", "ready", "ready},{");
        state.Request.Options["automatic_correction"] = true; state.Request.Options["clarification_policy"] = 1;
        state.Request.MaxReplanAttempts = 3;
        state = await Advance(state, runtime); state = await Advance(state, runtime);
        runtime.Respond = (request, _) => TestRuntime.Response(request, new() { Clarifications = [Question] });
        state = await Advance(state, runtime); Assert.Equal(PlanningStatus.Clarification, state.Status);
        var accepted = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        state = await Answer(state, runtime);
        runtime.Respond = null;
        var task = runtime.Proposal.Plan!.Root.Tasks[1]; task.Requires!.Items[1].Port = "ready";
        if (change == "requirements")
        {
            runtime.Proposal.Requirements!.Summary += " changed";
            runtime.Respond = (request, _) =>
            {
                var response = TestRuntime.Response(request, runtime.Proposal);
                response.Json!["requirements"] = JsonSerializer.SerializeToNode(runtime.Proposal.Requirements, PlanningJsonContext.Default.PlanningRequirements);
                return response;
            };
        }
        if (change == "guard") task.Requires = new() { Kind = "boolean", Boolean = true };
        if (change == "objective") runtime.Proposal.Plan.Root.Tasks[0].Objective += " different work";
        state = await Advance(state, runtime);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code is "REQUIREMENTS_CHANGED" or "REVISION_SCOPE_CHANGED" or "PLANNING_RESPONSE_INVALID");
        Assert.Equal(accepted, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash); Assert.Equal(4, state.ModelCalls); Assert.Equal(3, state.ReplanAttempts);
    }

    [Fact]
    public async Task QuestionAndAnswerEventsCarryIdentifiersWithoutAnswerContents()
    {
        using var activity = new System.Diagnostics.Activity("fixture").Start();
        var runtime = new TestRuntime { Proposal = new() { Clarifications = [Question] } };
        var state = PlannerFixture.Session(); state.Request.Options["clarification_policy"] = 1;
        state = await Advance(state, runtime);
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = [new(Question.Id, Text: "private business answer")] }, runtime, PlannerFixture.Ct);
        Assert.Equal(new[] { "planning.clarification.requested", "planning.clarification.answered" }, activity.Events.Select(e => e.Name));
        foreach (var item in activity.Events)
        {
            Assert.Equal("subject", item.Tags.Single(t => t.Key == "question.ids").Value);
            Assert.DoesNotContain(item.Tags, t => t.Value?.ToString()?.Contains("private business answer", StringComparison.Ordinal) == true);
        }
        Assert.Equal("", activity.Events.First().Tags.Single(t => t.Key == "diagnostic.locations").Value);
        Assert.Equal("private business answer", Assert.Single(Assert.Single(state.AnswerHistory!).Answers).Text);
    }

    [Fact]
    public void DiagnosticLocationTelemetryIsBoundedAndExcludesDiagnosticText()
    {
        using var activity = new System.Diagnostics.Activity("fixture").Start();
        var state = PlannerFixture.Session(); state.Request.Options["clarification_policy"] = 1;
        state.Diagnostics = Enumerable.Range(0, 40).Select(i => new PlanningDiagnostic("TASK_FIELD_UNKNOWN",
            "/tasks/" + new string('x', 300) + i, "private diagnostic context")).ToList();
        PlanningClarifications.Suspend(state, [Question]);
        var item = Assert.Single(activity.Events);
        Assert.InRange(item.Tags.Single(t => t.Key == "diagnostic.locations").Value!.ToString()!.Length, 1, 1543);
        Assert.DoesNotContain(item.Tags, t => t.Value?.ToString()?.Contains("private diagnostic context", StringComparison.Ordinal) == true);
    }
}
