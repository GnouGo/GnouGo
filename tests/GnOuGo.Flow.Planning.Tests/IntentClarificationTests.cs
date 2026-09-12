using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Testing;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class IntentClarificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningSnapshot Session() => new() { Request = new() { TenantId = "tenant", Prompt = IntentClarificationFixture.Prompt } };
    private static Task<PlanningSnapshot> Send(PlanningSnapshot state, IPlanningRuntime runtime, string command = "advance", string? text = null)
        => new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = command, ExpectedRevision = state.Revision, Text = text }, runtime, Ct);
    private static TypedPlannerTests.FakeRuntime Responses(params JsonNode?[] responses)
    {
        var index = 0;
        return new() { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = responses[Math.Min(index++, responses.Length - 1)]?.DeepClone(), Text = "{malformed" }) };
    }

    [Theory]
    [InlineData("external_read")]
    [InlineData("external_execute")]
    [InlineData("resource_lifecycle")]
    [InlineData("local_processing")]
    public async Task RuntimeFactsAndImplementationWorkCannotBecomeBusinessQuestions(string kind)
    {
        var state = Session();
        var runtime = ClarificationRuntime(kind);
        state = await Send(state, runtime); state = await Send(state, runtime);
        Assert.Null(state.Outcome); Assert.Null(state.Intent.Question);
        Assert.DoesNotContain("clarification", runtime.Phases); Assert.Equal(0, state.Intent.Questions);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("quoted")]
    [InlineData("offset")]
    [InlineData("unknown_field")]
    public async Task EvidenceTranscriptionAndForeignSelectionsAreRejectedBeforeStaging(string defect)
    {
        var state = Session(); var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            if (phase.EndsWith("_repair", StringComparison.Ordinal)) return Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                new JsonObject(p.Value!["properties"]!.AsObject().Select(f => new KeyValuePair<string, JsonNode?>(f.Key, JsonValue.Create("invalid"))))))) });
            var response = TypedPlannerTests.FakeRuntime.Interpret(request);
            var item = response.First().Value![0]!.AsObject();
            if (defect == "foreign") item["start"] = "foreign_reference";
            if (defect == "quoted") item["excerpt"] = state.Request.Prompt + "s";
            if (defect == "offset") item["start"] = 0;
            if (defect == "unknown_field") item["operationId"] = "invented";
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(response, request.StructuredOutputSchema!.AsObject()));
            return Task.FromResult(new LLMResponse { Json = response });
        } };
        state = await Send(state, runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.True(PlanningStatus.IsTerminal(state.Status));
        Assert.False(PlanningStatus.IsWaiting(state.Status)); Assert.Null(state.WaitingSinceUtc);
        Assert.Null(state.Outcome); Assert.NotNull(state.TechnicalStop); Assert.Null(state.Graph); Assert.Empty(state.Obligations);
        Assert.Equal(defect is "quoted" or "unknown_field" ? 1 : 2, runtime.Requests.Count);
        Assert.Equal(defect is "quoted" or "unknown_field" ? 0 : 1, state.DecisionCorrections.Count);
        var stopped = await Send(PlanningContext.Clone(state), runtime);
        Assert.Equal(state.Revision, stopped.Revision); Assert.Equal(defect is "quoted" or "unknown_field" ? 1 : 2, runtime.Requests.Count);
    }

    private static TypedPlannerTests.FakeRuntime ClarificationRuntime(string kind = "local_processing")
        => new() { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = TypedPlannerTests.FakeRuntime.Interpret(request, kind) }) };

    [Fact]
    public async Task EditedRecovery_ArchivesAnswersAndDiagnostics_PreservesBudgets_AndRejectsStaleCommands()
    {
        var state = Session(); state.Status = PlanningStatus.Stopped;
        state.Intent.Answers.Add(new("Prior question", new JsonObject { ["answer"] = "Prior answer" })); state.Intent.Forms = 1; state.Intent.Questions = 1;
        state.Diagnostics.Add(new("INTENT_EVIDENCE_INVALID", "/evidence", "Invalid evidence"));
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Keep approvals" };
        state.Usage = new LLMUsageBudgetScope(new() { MaxCalls = 10 }).Snapshot;
        state.ActiveMilliseconds = 4321;
        state.WaitingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2);
        var originalUsage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var runtime = new TypedPlannerTests.FakeRuntime();
        var edited = await Send(state, runtime, "edit_intent", "Return a greeting");
        Assert.Equal(state.Request.SessionId, edited.Request.SessionId);
        Assert.Equal("Keep approvals", edited.Request.Options["policy"]!["instructions"]!.GetValue<string>());
        Assert.Equal(originalUsage, JsonSerializer.Serialize(edited.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot));
        Assert.Equal(1, edited.Intent.Forms);
        Assert.Equal(1, edited.Intent.Questions);
        Assert.Single(Assert.Single(edited.Intent.History).Answers);
        Assert.Single(edited.Intent.History[0].Diagnostics);
        Assert.Empty(edited.Intent.Answers);
        Assert.Empty(edited.Diagnostics);
        Assert.Null(edited.Preparation);
        Assert.InRange(edited.ActiveMilliseconds, 4321, 14321);
        Assert.True(edited.HumanWaitMilliseconds >= 7_200_000);
        Assert.Empty(runtime.Requests);
        await Assert.ThrowsAsync<PlanningConflictException>(() => new TypedWorkflowPlanner().AdvanceAsync(edited,
            new() { Kind = "edit_intent", ExpectedRevision = state.Revision, Text = "stale" }, runtime, Ct));
        var reassessed = await Send(edited, runtime);
        Assert.True(reassessed.Intent.Checked);
        Assert.Equal(1, reassessed.Intent.Forms);
        Assert.DoesNotContain("Prior answer", runtime.Requests[0].Prompt);
    }
}
