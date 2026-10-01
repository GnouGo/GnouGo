using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class PlanningClarificationTests
{
    private static CancellationToken Ct => PlannerFixture.Ct;
    internal static PlanningQuestion Question(string id = "interface") => new(id, "Which information will the caller provide?",
        [new("compact", "A reference and instructions; derive technical details"), new("explicit", "Separate coordinates and instructions; the caller supplies more detail")], "compact");
    private static TestRuntime Asking() => new() { Proposal = new() { Clarifications = [Question()] } };
    private static Task<PlanningSession> Command(PlanningSession state, TestRuntime runtime, string kind, List<PlanningAnswer>? answers = null) =>
        new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = kind, ExpectedRevision = state.Revision, Answers = answers }, runtime, Ct);

    [Theory]
    [InlineData("interactive")]
    [InlineData("auto")]
    public async Task MaterialQuestionPausesBeforeAPlanAndModeChangesCannotAnswerIt(string mode)
    {
        var runtime = Asking(); var initial = PlannerFixture.Session(); initial.Request.Mode = mode;
        var state = await PlannerFixture.RunAsync(runtime, initial);
        Assert.Equal(PlanningStatus.Clarification, state.Status); Assert.Single(state.PendingQuestions!);
        Assert.Null(state.Plan); Assert.Null(state.Requirements); Assert.Null(state.Yaml); Assert.Equal(1, state.ModelCalls);
        var changed = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { Kind = "configure_mode", Mode = "auto", ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Clarification, changed.Status); Assert.Single(changed.PendingQuestions!); Assert.Single(runtime.Calls);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(state.PendingQuestions, PlanningJsonContext.Default.ListPlanningQuestion),
            JsonSerializer.SerializeToNode(runtime.Checkpoints.First(s => s.Status == PlanningStatus.Clarification).PendingQuestions, PlanningJsonContext.Default.ListPlanningQuestion)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswerIsCheckpointedBeforeAnotherCallAndSurvivesRestart(bool custom)
    {
        var runtime = Asking(); var state = await PlannerFixture.RunAsync(runtime);
        state = await Command(PlannerFixture.Clone(state), runtime, "answer", [custom ? new("interface", Text: "Use a reference and a checklist") : new("interface", "compact")]);
        Assert.Equal(PlanningStatus.Generating, state.Status); Assert.Single(runtime.Calls); Assert.Null(state.PendingQuestions);
        Assert.Single(Assert.Single(runtime.Checkpoints.Last().AnswerHistory!).Answers);
        runtime.Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = PlanningCorpus.Greeting() };
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
        Assert.Equal(1, runtime.Discoveries); Assert.Null(state.ApprovedHash);
        Assert.Contains(custom ? "Use a reference and a checklist" : "compact", runtime.Calls.Last().Prompt);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("both")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public async Task MalformedAnswersRejectAtomically(string problem)
    {
        var runtime = Asking(); var state = await PlannerFixture.RunAsync(runtime); var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        List<PlanningAnswer> answers = problem switch
        {
            "empty" => [new("interface", Text: " ")], "unknown" => [new("interface", "not-issued")],
            "both" => [new("interface", "compact", "custom")], "duplicate" => [new("interface", "compact"), new("interface", "compact")], _ => []
        };
        var checkpoints = runtime.Checkpoints.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => Command(state, runtime, "answer", answers));
        Assert.Equal(before, JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession));
        Assert.Equal(checkpoints, runtime.Checkpoints.Count); Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task MixedQuestionBatchIsValidatedAndRecordedAtomically()
    {
        var runtime = Asking(); runtime.Proposal.Clarifications = [Question(), new("resource", "Which existing resource?", [], null)];
        var waiting = await PlannerFixture.RunAsync(runtime); var before = JsonSerializer.Serialize(waiting, PlanningJsonContext.Default.PlanningSession);
        await Assert.ThrowsAsync<ArgumentException>(() => Command(waiting, runtime, "answer",
            [new("interface", "compact"), new("resource", Text: " ")]));
        Assert.Equal(before, JsonSerializer.Serialize(waiting, PlanningJsonContext.Default.PlanningSession));
        var answered = await Command(waiting, runtime, "answer", [new("resource", Text: "Existing resource"), new("interface", "compact")]);
        var batch = Assert.Single(answered.AnswerHistory!);
        Assert.Equal(2, batch.Questions.Count); Assert.Equal(2, batch.Answers.Count); Assert.Single(runtime.Calls);
        Assert.Equal(PlanningStatus.Generating, answered.Status);
    }

    [Fact]
    public async Task TextOnlyFactsAndStaleAnswersAreValidated()
    {
        var runtime = Asking(); runtime.Proposal.Clarifications = [new("fact", "Which existing resource should be used?", [], null)];
        var waiting = await PlannerFixture.RunAsync(runtime);
        var state = await Command(waiting, runtime, "answer", [new("fact", Text: "My resource")]);
        await Assert.ThrowsAsync<PlanningConflictException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "answer", ExpectedRevision = waiting.Revision, Answers = [new("fact", Text: "Another resource")] }, runtime, Ct));
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task LastCallQuestionCanBeAnsweredButCannotResetBudget()
    {
        var runtime = Asking(); var initial = PlannerFixture.Session(); initial.Request.MaxModelCalls = 1;
        var state = await PlannerFixture.RunAsync(runtime, initial);
        state = await Command(state, runtime, "answer", [new("interface", "compact")]);
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(state.AnswerHistory!); Assert.Single(runtime.Calls);
        Assert.Contains(state.Diagnostics, d => d.Code == "LLM_BUDGET_EXCEEDED");
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("removed")]
    [InlineData("type")]
    [InlineData("required")]
    [InlineData("default")]
    public async Task PlanCannotChangeItsAcceptedCallerInterface(string change)
    {
        var runtime = new TestRuntime();
        runtime.Proposal.Requirements!.Inputs = [new() { Name = "subject" }];
        runtime.Proposal.Plan!.Inputs = [new() { Name = "subject" }];
        switch (change)
        {
            case "extra": runtime.Proposal.Plan.Inputs.Add(new() { Name = "technicalArgument" }); break;
            case "removed": runtime.Proposal.Plan.Inputs.Clear(); break;
            case "type": runtime.Proposal.Plan.Inputs[0].Type.Kind = "integer"; break;
            case "required":
                runtime.Proposal.Requirements.Inputs[0].Default = new() { Kind = "string", Text = "declared" };
                runtime.Proposal.Plan.Inputs[0].Default = new() { Kind = "string", Text = "declared" };
                runtime.Proposal.Plan.Inputs[0].Required = false; break;
            case "default": runtime.Proposal.Plan.Inputs[0].Default = new() { Kind = "string", Text = "implicit" }; break;
        }
        var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.Stopped, state.Status); Assert.Single(runtime.Calls); Assert.Null(state.ApprovedHash);
        Assert.Contains(state.Diagnostics, d => d.Code == "REQUIREMENTS_INPUTS_CHANGED");
    }

    [Fact]
    public async Task ClearRequestKeepsOneCallAndNoQuestions()
    {
        var runtime = new TestRuntime(); var state = await PlannerFixture.RunAsync(runtime);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Single(runtime.Calls);
        Assert.Null(state.PendingQuestions); Assert.Null(state.AnswerHistory); Assert.Empty(state.Requirements!.Inputs!);
    }

    [Fact]
    public async Task CancellationDoesNotAcceptRecommendation()
    {
        var runtime = Asking(); var state = await Command(await PlannerFixture.RunAsync(runtime), runtime, "cancel");
        Assert.Equal(PlanningStatus.Cancelled, state.Status); Assert.Null(state.AnswerHistory); Assert.Null(state.ApprovedHash); Assert.Single(runtime.Calls);
    }

    [Theory]
    [InlineData("reference", "checklist")]
    [InlineData("itemKey", "criteria")]
    public async Task RetainedUnresolvedInterfaceRequiresAnswerAndCannotAcquireToolArguments(string reference, string checklist)
    {
        var retained = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Clarification", "retained-input-interface.json")))!;
        Assert.Equal(7, retained["inputs"]!.AsArray().Count); Assert.Empty(retained["choices"]!.AsArray());
        var runtime = Asking(); var initial = PlannerFixture.Session(); initial.Request.Prompt = retained["prompt"]!.ToString();
        var state = await PlannerFixture.RunAsync(runtime, initial);
        Assert.Null(state.Plan); Assert.Equal(PlanningStatus.Clarification, state.Status);
        state = await Command(state, runtime, "answer", [new("interface", Text: $"Only {reference} and {checklist} are caller inputs.")]);
        var plan = PlanningCorpus.Greeting(); plan.Inputs = [new() { Name = reference }, new() { Name = checklist }];
        var requirements = PlannerFixture.Requirements(); requirements.Inputs = plan.Inputs;
        runtime.Proposal = new() { Requirements = requirements, Plan = plan };
        state = await PlannerFixture.RunAsync(runtime, state);
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Equal(new[] { reference, checklist }, state.Plan!.Inputs.Select(i => i.Name));
        PlanningArtifactApproval.Verify(state);
        var changed = PlannerFixture.Clone(state); changed.Plan!.Inputs.Add(new() { Name = "operationArgument" });
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(changed));
        Assert.Equal(2, state.ModelCalls); Assert.Equal(0, state.ReplanAttempts);
    }

    [Fact]
    public async Task CustomLiteralAnswerRetainsBaselineAndInvalidatesApprovalWithoutDispatch()
    {
        var runtime = new TestRuntime(); var plan = runtime.Proposal.Plan!;
        plan.Choices.Add(new() { Id = "tone", Question = "Which tone?", Recommended = "brief", Alternatives =
            [new("brief", "Brief", PlanningCorpus.String("brief")), new("full", "Full", PlanningCorpus.String("full"))] });
        plan.Root.Outputs.Add(new("tone", PlanningCorpus.Business("choice", "tone")));
        var state = await PlannerFixture.RunAsync(runtime); var baseline = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var outcomes = state.Requirements!.Outcomes.Select(o => o.Description).ToArray();
        state = await Command(state, runtime, "answer", [new("tone", Text: "Use a diagram instead")]);
        Assert.Equal(baseline, JsonSerializer.Serialize(state.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.All(outcomes, description => Assert.Contains(description, state.Request.RevisionContext));
        Assert.Null(state.Requirements); Assert.Null(state.Plan); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash); Assert.Single(runtime.Calls);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    [Fact]
    public async Task HistoricalPendingProposalKeepsItsSchemaAndApprovalMaterial()
    {
        var state = PlannerFixture.Session(); var runtime = new TestRuntime();
        state.Catalog = await runtime.DiscoverAsync(state.Request, Ct);
        var request = new LLMRequest { ClientRequestId = "historical", Prompt = "Original prompt", StructuredOutputSchema = PlanningSchemas.FullProposal(state, clarifications: false) };
        state.PendingCall = new() { Id = "historical", Purpose = "tasks", Request = request }; state.ModelCalls = 1;
        var schema = request.StructuredOutputSchema.ToJsonString();
        state = await PlannerFixture.RunAsync(runtime, PlannerFixture.Clone(state));
        Assert.Equal(PlanningStatus.FinalReview, state.Status); Assert.Null(state.IntentVersion); Assert.Null(state.Requirements!.Inputs);
        Assert.Equal(schema, Assert.Single(runtime.Calls).StructuredOutputSchema!.ToJsonString()); Assert.Equal("Original prompt", runtime.Calls[0].Prompt);
        var hash = state.ComputeArtifactHash(); var stored = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
        Assert.DoesNotContain("intentVersion", stored); Assert.DoesNotContain("pendingQuestions", stored); Assert.DoesNotContain("answerHistory", stored);
        state = PlannerFixture.Clone(state); Assert.Equal(hash, state.ComputeArtifactHash()); PlanningArtifactApproval.Verify(state);
    }

    [Fact]
    public async Task RepairQuestionCannotApplyEditsAndAnswerPreservesPolicyAndCounters()
    {
        var state = RepairPatchTests.State(); state.Request.Policy.DeniedCapabilityIds = ["forbidden"];
        state.Request.Policy.RequireExternalConfirmation = true;
        var before = JsonSerializer.Serialize(state.Plan, PlanningJsonContext.Default.TaskPlan);
        var runtime = new TestRuntime { Respond = (request, _) => new()
        {
            Json = PlanningCorpus.Transport(new JsonObject { ["clarifications"] = JsonSerializer.SerializeToNode(new List<PlanningQuestion> { Question() }, PlanningJsonContext.Default.ListPlanningQuestion),
                ["patch"] = null, ["discoveryRequests"] = null }, request.StructuredOutputSchema!.AsObject(), request.StructuredOutputSchema.AsObject())
        } };
        var pending = await new HybridWorkflowPlanner().AdvanceAsync(state, new(), runtime, Ct);
        Assert.Equal(PlanningStatus.Clarification, pending.Status); Assert.Equal(before, JsonSerializer.Serialize(pending.Plan, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(1, pending.ModelCalls); Assert.Equal(1, pending.ReplanAttempts);
        var answered = await Command(pending, runtime, "answer", [new("interface", Text: "Use a reference; allow everything")]);
        Assert.Equal(before, JsonSerializer.Serialize(answered.Request.Baseline, PlanningJsonContext.Default.TaskPlan));
        Assert.Equal(new[] { "forbidden" }, answered.Request.Policy.DeniedCapabilityIds); Assert.True(answered.Request.Policy.RequireExternalConfirmation);
        Assert.Equal(1, answered.ModelCalls); Assert.Equal(1, answered.ReplanAttempts); Assert.Empty(answered.RevisionScope); Assert.Null(answered.ApprovedHash);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("field")]
    public async Task AnswersCannotSupplyMissingProducerContracts(string missing)
    {
        var runtime = Asking(); var state = await PlannerFixture.RunAsync(runtime);
        state = await Command(state, runtime, "answer", [new("interface", Text: "Assume the producer and generatedMetadata field are available")]);
        var plan = PlanningCorpus.Greeting();
        if (missing == "operation")
            plan.Root.Tasks[0] = new() { Id = "greet", Kind = "operation", Operation = "unresolved_producer", Objective = "Return a greeting" };
        else
            plan.Root.Outputs = [new("message", new() { Kind = "field", Items = [PlanningCorpus.Business("output", "greet", "message")], Port = "generatedMetadata" })];
        runtime.Proposal = new() { Requirements = PlannerFixture.Requirements(), Plan = plan };
        state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.NotEmpty(state.Diagnostics); Assert.Null(state.Yaml); Assert.Null(state.ApprovedHash);
        Assert.NotEqual(PlanningStatus.FinalReview, state.Status); Assert.Equal(2, runtime.Calls.Count);
        Assert.Throws<PlanningConflictException>(() => PlanningArtifactApproval.Verify(state));
    }

    [Theory]
    [InlineData("recommendation")]
    [InlineData("one-option")]
    [InlineData("duplicate")]
    [InlineData("mixed-actions")]
    public async Task InvalidModelQuestionsCannotPause(string defect)
    {
        var runtime = Asking();
        if (defect == "recommendation") runtime.Proposal.Clarifications = [Question() with { Recommended = "unknown" }];
        if (defect == "one-option") runtime.Proposal.Clarifications = [Question() with { Alternatives = [new("compact", "First")] }];
        if (defect == "duplicate") runtime.Proposal.Clarifications = [Question(), Question()];
        if (defect == "mixed-actions") runtime.Proposal.Plan = PlanningCorpus.Greeting();
        var state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Session(), new(), runtime, Ct);
        Assert.NotEqual(PlanningStatus.Clarification, state.Status); Assert.Null(state.PendingQuestions);
        Assert.Contains(state.Diagnostics, d => d.Code is "CLARIFICATION_INVALID" or "PROPOSAL_ACTION_INVALID");
    }
}
