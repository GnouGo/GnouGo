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
    [InlineData("Choose a publishing policy.")]
    [InlineData("Choisir une politique de publication.")]
    [InlineData("Elegir una política de publicación.")]
    public async Task BusinessChoicesUseOwnedEvidenceAndWaitForDiscovery(string prompt)
    {
        var state = Session(); state.Request.Prompt = prompt;
        var runtime = ClarificationRuntime();
        state = await Send(state, runtime);
        Assert.True(state.Intent.Checked); Assert.Null(state.Intent.Question);
        Assert.Equal(0, runtime.PreparationCalls);
        state = await Send(state, runtime);
        Assert.Equal(PlanningStatus.Clarification, state.Status); Assert.Equal(1, runtime.PreparationCalls);
        var outcome = Assert.IsType<PlanningNeedUserClarification>(state.Outcome);
        var obligation = Assert.Single(state.Obligations);
        Assert.Equal(obligation.EvidenceReferences, outcome.Decision.EvidenceReferences);
        Assert.Equal(prompt, PlanningSourceDecisions.Text(state, obligation));
        Assert.Single(state.Intent.Question!.Fields!); Assert.Equal(1, state.Intent.Forms);
        Assert.All(runtime.Requests, request =>
        {
            Assert.DoesNotContain("excerpt", request.StructuredOutputSchema!.ToJsonString());
            Assert.InRange(PlanningJsonTransport.EstimateInputTokens(request.Prompt, request.StructuredOutputSchema.AsObject()), 1, 9600);
        });
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

    [Fact]
    public async Task HostPoliciesCannotSupplyBusinessClarificationEvidence()
    {
        var state = Session(); state.Request.Options["policy"] = new JsonObject { ["instructions"] = "A host policy." };
        var references = PlanningReferences.Register(state, "host", "host_constraint", "A host policy.");
        state.Obligations = [new("business", references.Select(r => r.Id).ToList(), "business_decision", "business_choice", true)];
        var runtime = ClarificationRuntime();
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningClarifications.AskAfterDiscoveryAsync(state, runtime, Ct));
        Assert.Equal("CLARIFICATION_EVIDENCE_UNPROVEN", error.Code); Assert.Empty(runtime.Requests); Assert.Null(state.Outcome);
    }

    [Fact]
    public async Task AnswersContinueTheSameSessionWithValidatedSchemaAndCumulativeAllowances()
    {
        var runtime = ClarificationRuntime(); var state = await Send(Session(), runtime); state = await Send(state, runtime);
        var question = Assert.IsType<PlanningNeedUserClarification>(state.Outcome).Decision;
        var planner = new TypedWorkflowPlanner();
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = new JsonObject { [question.DecisionId] = 123 } }, runtime, Ct));
        var next = await planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = new JsonObject { [question.DecisionId] = "choice_1" } }, runtime, Ct);
        Assert.Equal(state.Request.SessionId, next.Request.SessionId); Assert.Equal(1, next.Intent.Forms); Assert.Null(next.Outcome);
        Assert.Equal("Ask before publishing", Assert.Single(next.Intent.Answers).Answers[question.DecisionId]!.ToString());
        Assert.Equal(state.RequestAccounting.Count, next.RequestAccounting.Count);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(next, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = new JsonObject { [question.DecisionId] = "choice_0" } }, runtime, Ct));
    }

    private static TypedPlannerTests.FakeRuntime ClarificationRuntime(string kind = "business_choice")
    {
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            JsonObject answer = phase == "intent" ? TypedPlannerTests.FakeRuntime.Interpret(request, kind)
                : new(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                    new JsonObject { ["question"] = "Choose the publishing policy", ["first"] = "Publish automatically", ["second"] = "Ask before publishing" })));
            return Task.FromResult(new LLMResponse { Json = answer });
        } };
        runtime.OnPrepareSnapshot = async state =>
        {
            await PlanningClarifications.AskAfterDiscoveryAsync(state, runtime, Ct);
            return TypedPlannerTests.Preparation();
        };
        return runtime;
    }

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
