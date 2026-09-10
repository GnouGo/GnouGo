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

    [Fact]
    public async Task RecordedQuotedMultipleExcerpts_AreRepaired_AndAllThreeQuestionsReachTheForm()
    {
        var repaired = IntentClarificationFixture.Questions();
        repaired["evidence"] = IntentClarificationFixture.Evidence("Review a proposed change.");
        repaired["evidence"]!.AsArray().Add(IntentClarificationFixture.Evidence("Run available checks.")[0]!.DeepClone());
        var runtime = Responses(IntentClarificationFixture.QuotedQuestions(), repaired);
        var result = await Send(Session(), runtime);
        Assert.Equal(PlanningStatus.Clarification, result.Status);
        Assert.Equal(3, result.Intent.Question!.Fields!.Count);
        Assert.Equal(new[] { "choice_0", "choice_1", "choice_2" }, result.Intent.Question.Fields.Select(f => f.Name));
        Assert.All(result.Intent.Question.Fields, field => Assert.Equal(2, field.OptionDefinitions!.Count));
        Assert.Equal(new[] { "intent", "intent_repair" }, runtime.Phases);
        Assert.True(JsonNode.DeepEquals(runtime.Requests[0].StructuredOutputSchema, runtime.Requests[1].StructuredOutputSchema));
        Assert.Contains("INTENT_SCHEMA_INVALID", runtime.Requests[1].Prompt);
        Assert.Equal(1, result.Intent.Forms);
        Assert.Equal(3, result.Intent.Questions);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Events, e => e.Kind == "intent_repair_succeeded");
    }

    [Theory]
    [InlineData("Review a proposed change. Run available checks.")]
    [InlineData("Examiner une modification proposée. Exécuter les contrôles disponibles.")]
    [InlineData("Revisar un cambio propuesto. Ejecutar las comprobaciones disponibles.")]
    public async Task EquivalentLanguages_UseLiteralSourceEvidence(string prompt)
    {
        var state = Session(); state.Request.Prompt = prompt;
        var runtime = Responses(IntentClarificationFixture.Questions(prompt));
        var result = await Send(state, runtime);
        Assert.Equal(PlanningStatus.Clarification, result.Status);
        Assert.Single(runtime.Requests);
    }

    [Theory]
    [InlineData("invented")]
    [InlineData("unknown_source")]
    [InlineData("wrong_source")]
    [InlineData("host_only")]
    [InlineData("model_question")]
    [InlineData("options")]
    [InlineData("id")]
    [InlineData("malformed")]
    public async Task InvalidContracts_ExhaustTwoCalls_AndPauseWithoutLosingTheSession(string defect)
    {
        var state = Session();
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Keep all approvals." };
        state.Intent.Answers.Add(new("Model-written question", new JsonObject { ["prior"] = "yes" })); state.Intent.Forms = 1; state.Intent.Questions = 1;
        var json = IntentClarificationFixture.Questions();
        switch (defect)
        {
            case "invented": json["evidence"] = IntentClarificationFixture.Evidence("Invented behavior"); break;
            case "unknown_source": json["evidence"] = IntentClarificationFixture.Evidence(state.Request.Prompt, "missing"); break;
            case "wrong_source": json["evidence"] = IntentClarificationFixture.Evidence(state.Request.Prompt, "answer_0_0"); break;
            case "host_only": json["evidence"] = IntentClarificationFixture.Evidence("Keep all approvals.", "host"); break;
            case "model_question": json["evidence"] = IntentClarificationFixture.Evidence("Model-written question", "answer_0_0"); break;
            case "options": json["questions"]![0]!["options"]![1]!["value"] = "first"; break;
            case "id": json["questions"]![1]!["id"] = "choice_0"; break;
        }
        var runtime = Responses(defect == "malformed" ? null : json);
        var result = await Send(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, result.Status);
        Assert.True(PlanningStatus.IsWaiting(result.Status));
        Assert.False(PlanningStatus.IsTerminal(result.Status));
        Assert.Null(result.Outcome);
        Assert.NotNull(result.WaitingSinceUtc);
        Assert.Null(result.Intent.Question);
        Assert.Null(result.Graph);
        Assert.False(result.Intent.Checked);
        Assert.Equal(state.Request.SessionId, result.Request.SessionId);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal(1, result.Intent.Forms); // Retained answer, no invalid form counted.
        var paused = await Send(result, runtime);
        Assert.Equal(result.Revision, paused.Revision);
        Assert.Equal(2, runtime.Requests.Count);
    }

    [Fact]
    public async Task SchemaAndSemanticFailures_ShareOneRepairAllowance()
    {
        var badEvidence = IntentClarificationFixture.Questions();
        badEvidence["evidence"] = IntentClarificationFixture.Evidence("Not in the request");
        var runtime = Responses(new JsonObject(), badEvidence, IntentClarificationFixture.Questions());
        var result = await Send(Session(), runtime);
        Assert.Equal(PlanningStatus.Recovery, result.Status);
        Assert.Equal(2, runtime.Requests.Count);
        Assert.Contains(result.Diagnostics, d => d.Location == "/evidence/0");
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("drop")]
    [InlineData("options")]
    [InlineData("prompt")]
    public async Task RepairCannotDiscardOrChangePreviouslyValidQuestions(string regression)
    {
        var original = IntentClarificationFixture.Questions();
        original["evidence"] = IntentClarificationFixture.Evidence("Invented evidence");
        var repaired = IntentClarificationFixture.Questions();
        switch (regression)
        {
            case "ready": repaired = IntentClarificationFixture.Ready(); break;
            case "drop": repaired["questions"]!.AsArray().RemoveAt(2); break;
            case "options": repaired["questions"]![0]!["options"]![0]!["value"] = "changed"; break;
            case "prompt": repaired["questions"]![0]!["prompt"] = "Changed question"; break;
        }
        var result = await Send(Session(), Responses(original, repaired));
        Assert.Equal(PlanningStatus.Recovery, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "INTENT_REPAIR_REGRESSION");
    }

    [Fact]
    public async Task TargetedSemanticRepair_PreservesQuestions_AndProvidesExactDiagnosticPaths()
    {
        var invalid = IntentClarificationFixture.Questions();
        invalid["questions"]![1]!["evidence"] = IntentClarificationFixture.Evidence("« " + IntentClarificationFixture.Prompt + " »");
        var runtime = Responses(invalid, IntentClarificationFixture.Questions());
        var result = await Send(Session(), runtime);
        Assert.Equal(PlanningStatus.Clarification, result.Status);
        Assert.Contains("/questions/1/evidence/0", runtime.Requests[1].Prompt);
    }

    [Fact]
    public async Task EditedRecovery_ArchivesAnswersAndDiagnostics_PreservesBudgets_AndRejectsStaleCommands()
    {
        var state = Session(); state.Status = PlanningStatus.Recovery;
        state.Intent.Answers.Add(new("Prior question", new JsonObject { ["answer"] = "Prior answer" })); state.Intent.Forms = 1; state.Intent.Questions = 1;
        state.Diagnostics.Add(new("INTENT_EVIDENCE_INVALID", "/evidence", "Invalid evidence"));
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Keep approvals" };
        state.Usage = new LLMUsageBudgetScope(new() { MaxCalls = 10 }).Snapshot;
        state.ActiveMilliseconds = 4321;
        state.WaitingSinceUtc = DateTimeOffset.UtcNow.AddHours(-2);
        var originalUsage = JsonSerializer.Serialize(state.Usage, PlanningJsonContext.Default.LLMUsageBudgetSnapshot);
        var runtime = Responses(IntentClarificationFixture.Ready());
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
