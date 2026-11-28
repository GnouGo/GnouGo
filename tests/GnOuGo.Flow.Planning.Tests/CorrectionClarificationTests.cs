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
    [InlineData("auto")]
    [InlineData("interactive")]
    public async Task QuestionAndAnswerEventsPreserveIntentWithoutLeakingContents(string mode)
    {
        using var activity = new System.Diagnostics.Activity("fixture").Start();
        var model = new CurrentPlanningFixture.LiteralModel(questions: [Question]);
        var runtime = new WorkflowPlanningRuntime(new() { LLMClient = model }, (_, _) => Task.CompletedTask);
        var state = new PlanningSession { Request = new() { TenantId = "clarification-events", Mode = mode,
            Prompt = "Return the required public numeric result named result with value 42." } };
        var planner = new HybridWorkflowPlanner();
        for (var count = 0; count < 4 && state.Status != PlanningStatus.Clarification; count++)
            state = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Clarification, state.Status);
        var originals = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var calls = state.ModelCalls;
        state = await planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = [new(Question.Id, Text: "private business answer")] }, runtime, PlannerFixture.Ct);
        Assert.Equal(originals, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Equal(calls, state.ModelCalls);
        var events = activity.Events.Where(item => item.Name.StartsWith("planning.clarification.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "planning.clarification.requested", "planning.clarification.answered" }, events.Select(item => item.Name));
        foreach (var item in events)
        {
            Assert.Equal("subject", item.Tags.Single(tag => tag.Key == "question.ids").Value);
            Assert.DoesNotContain(item.Tags, tag => tag.Value?.ToString()?.Contains("private business answer", StringComparison.Ordinal) == true);
        }
        Assert.Equal("private business answer", Assert.Single(Assert.Single(state.AnswerHistory!).Answers).Text);
    }

    [Fact]
    public void DiagnosticLocationTelemetryIsBoundedAndExcludesDiagnosticText()
    {
        using var activity = new System.Diagnostics.Activity("fixture").Start();
        var state = PlannerFixture.Session();
        state.Diagnostics = Enumerable.Range(0, 40).Select(i => new PlanningDiagnostic("TASK_FIELD_UNKNOWN",
            "/tasks/" + new string('x', 300) + i, "private diagnostic context")).ToList();
        PlanningClarifications.Suspend(state, [Question]);
        var item = Assert.Single(activity.Events);
        Assert.InRange(item.Tags.Single(t => t.Key == "diagnostic.locations").Value!.ToString()!.Length, 1, 1543);
        Assert.DoesNotContain(item.Tags, t => t.Value?.ToString()?.Contains("private diagnostic context", StringComparison.Ordinal) == true);
    }
}
