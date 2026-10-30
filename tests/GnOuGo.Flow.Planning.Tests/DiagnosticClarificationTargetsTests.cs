using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Planning.Examples;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DiagnosticClarificationTargetsTests
{
    [Theory]
    [InlineData(PlanningCompactConstruction.IntentPurpose)]
    [InlineData(PlanningCompactConstruction.SelectPurpose)]
    [InlineData(PlanningCompactConstruction.ImplementationPurpose)]
    [InlineData(PlanningCompactConstruction.DiscoveryPurpose)]
    [InlineData(PlanningConstruction.DraftPurpose)]
    public async Task DiagnosticQuestionsAndCompleteLongAnswerSurviveRestartWithoutChangingAuthority(string phase)
    {
        var (state, runtime) = await Asking(phase);
        Assert.True(state.Status == PlanningStatus.Clarification, string.Join("; ", state.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Equal(4, state.PendingQuestions!.Count);
        Assert.All(state.PendingQuestions, question => Assert.Equal(4, question.Alternatives.Count));
        var warnings = state.ThresholdWarnings!;
        Assert.Contains(warnings, warning => warning.Kind == "clarification_questions" && warning.Current == 4 && warning.Threshold == 3);
        Assert.Equal(4, warnings.Count(warning => warning.Kind == "clarification_alternatives" && warning.Current == 4 && warning.Threshold == 3));
        var plan = PlanningConstruction.Hash(state.Plan);
        var requirements = JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements);
        var calls = state.ModelCalls; var repairs = state.ReplanAttempts; var corrections = state.Construction!.DraftCorrections;
        var warningSnapshot = JsonSerializer.Serialize(warnings, PlanningJsonContext.Default.ListPlanningThresholdWarning);
        state = PlannerFixture.Clone(state);
        Assert.Equal(warningSnapshot, JsonSerializer.Serialize(state.ThresholdWarnings!, PlanningJsonContext.Default.ListPlanningThresholdWarning));
        var completeAnswer = new string('x', 9000) + " END_OF_EXACT_ANSWER";
        var answers = state.PendingQuestions!.Select((question, index) => index == 0
            ? new PlanningAnswer(question.Id, Text: completeAnswer) : new(question.Id, "option_3")).ToList();
        state = await new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = answers }, runtime, PlannerFixture.Ct);
        Assert.Equal(PlanningStatus.Generating, state.Status);
        Assert.Equal(calls, state.ModelCalls); Assert.Equal(calls, runtime.Calls.Count);
        Assert.Equal(repairs, state.ReplanAttempts); Assert.Equal(corrections, state.Construction!.DraftCorrections);
        Assert.Equal(plan, PlanningConstruction.Hash(state.Plan));
        Assert.Equal(requirements, JsonSerializer.Serialize(state.Requirements, PlanningJsonContext.Default.PlanningRequirements));
        Assert.Null(state.PendingQuestions); Assert.Null(state.PendingCall); Assert.Null(state.ApprovedHash);
        Assert.Equal(completeAnswer, Assert.Single(state.AnswerHistory!).Answers[0].Text);
        Assert.Contains(state.ThresholdWarnings!, warning => warning.Kind == "clarification_answer_characters" && warning.Current == completeAnswer.Length && warning.Threshold == 8192);
        state = PlannerFixture.Clone(state);
        PlanningCompactConstruction.InitializeSources(state);
        Assert.Contains(state.Construction!.Compact!.Sources, source => source.Text.Contains(completeAnswer, StringComparison.Ordinal));
        Assert.Empty(state.Diagnostics);
    }

    [Fact]
    public async Task DiagnosticLongAnswerIsAdvisoryEvenForAnOrdinarySizedQuestionBatch()
    {
        var state = State("diagnostic"); var questions = Questions(1, 0);
        PlanningClarifications.Suspend(state, questions);
        state.PendingQuestions = questions; state.Status = PlanningStatus.Clarification;
        Assert.Null(state.ThresholdWarnings);
        var runtime = new TestRuntime(); var answer = new string('x', 8193);
        state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state),
            new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = [new("question_0", Text: answer)] }, runtime, PlannerFixture.Ct);
        Assert.Equal(answer, Assert.Single(Assert.Single(state.AnswerHistory!).Answers).Text);
        Assert.Equal("clarification_answer_characters", Assert.Single(state.ThresholdWarnings!).Kind);
        Assert.Empty(runtime.Calls); Assert.Equal(0, state.ReplanAttempts); Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("ordinary", "questions")]
    [InlineData("ordinary", "alternatives")]
    [InlineData("token", "questions")]
    [InlineData("token", "alternatives")]
    public void HistoricalPoliciesKeepClarificationCountCaps(string policy, string target)
    {
        var state = State(policy);
        var questions = Questions(target == "questions" ? 4 : 1, target == "alternatives" ? 4 : 2);
        var before = Snapshot(state);
        Assert.Throws<PlanningResponseException>(() => PlanningClarifications.Suspend(state, questions));
        Assert.Equal(before, Snapshot(state));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("token")]
    public async Task HistoricalPoliciesRejectOversizedAnswersAtomically(string policy)
    {
        var state = State(policy); var runtime = new TestRuntime();
        state.PendingQuestions = Questions(1, 2);
        PlanningClarifications.Suspend(state, state.PendingQuestions);
        state.Status = PlanningStatus.Clarification;
        var before = Snapshot(state);
        await Assert.ThrowsAsync<ArgumentException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = [new("question_0", Text: new string('x', 8193))] }, runtime, PlannerFixture.Ct));
        Assert.Equal(before, Snapshot(state)); Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate_question")]
    [InlineData("blank_question")]
    [InlineData("blank_id")]
    [InlineData("one_alternative")]
    [InlineData("duplicate_alternative")]
    [InlineData("blank_alternative")]
    [InlineData("missing_recommendation")]
    [InlineData("unknown_recommendation")]
    [InlineData("recommendation_without_alternatives")]
    public void DiagnosticPolicyPreservesQuestionValidityWithoutPartialWarnings(string defect)
    {
        var state = State("diagnostic"); var questions = Questions(4, 4);
        questions[0] = defect switch
        {
            "blank_question" => questions[0] with { Question = " " },
            "blank_id" => questions[0] with { Id = " " },
            "one_alternative" => questions[0] with { Alternatives = [new("only", "Only result")] },
            "duplicate_alternative" => questions[0] with { Alternatives = [new("same", "First"), new("same", "Second")] },
            "blank_alternative" => questions[0] with { Alternatives = [new("one", " "), new("two", "Second")] },
            "missing_recommendation" => questions[0] with { Recommended = null },
            "unknown_recommendation" => questions[0] with { Recommended = "absent" },
            "recommendation_without_alternatives" => questions[0] with { Alternatives = [] },
            _ => questions[0]
        };
        if (defect == "empty") questions.Clear();
        if (defect == "duplicate_question") questions[1] = questions[0];
        var before = Snapshot(state);
        Assert.Throws<PlanningResponseException>(() => PlanningClarifications.Suspend(state, questions));
        Assert.Equal(before, Snapshot(state));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown_question")]
    [InlineData("unknown_alternative")]
    [InlineData("blank")]
    [InlineData("both")]
    public async Task DiagnosticPolicyKeepsExactAnswerValidation(string defect)
    {
        var (state, runtime) = await Asking(PlanningCompactConstruction.IntentPurpose);
        Assert.True(state.Status == PlanningStatus.Clarification, string.Join("; ", state.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        var answers = state.PendingQuestions!.Select(question => new PlanningAnswer(question.Id, "option_0")).ToList();
        switch (defect)
        {
            case "missing": answers.RemoveAt(0); break;
            case "duplicate": answers[1] = answers[0]; break;
            case "unknown_question": answers[0] = new("unknown", "option_0"); break;
            case "unknown_alternative": answers[0] = new("question_0", "absent"); break;
            case "blank": answers[0] = new("question_0", Text: " "); break;
            case "both": answers[0] = new("question_0", "option_0", new string('x', 9000)); break;
        }
        var before = Snapshot(state); var calls = runtime.Calls.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => new HybridWorkflowPlanner().AdvanceAsync(state,
            new() { Kind = "answer", ExpectedRevision = state.Revision, Answers = answers }, runtime, PlannerFixture.Ct));
        Assert.Equal(before, Snapshot(state)); Assert.Equal(calls, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("token")]
    [InlineData("diagnostic")]
    public async Task TypedRepairClarificationsUseCapturedTargetsWithoutChangingEditAuthority(string policy)
    {
        var state = State(policy);
        state.Catalog = await new TestRuntime().DiscoverAsync(state.Request, PlannerFixture.Ct);
        state.Plan = new() { Root = new() { Tasks = [new() { Id = "number", Kind = "value", Outputs = [new("value", new() { Kind = "number", Number = 42 })] }] } };
        state.Requirements = new() { Inputs = [], Outputs = [], Outcomes = [new("req_0", state.Request.Prompt)] };
        state.EditablePaths = ["/tasks/number/outputs/value"];
        state.RevisionScope = state.EditablePaths.ToList();
        var authority = PlanningRepairPatch.Authority(state);
        var schema = PlanningSchemas.RepairSchema(state);
        var response = new JsonObject { ["clarifications"] = JsonSerializer.SerializeToNode(Questions(4, 4), PlanningJsonContext.Default.ListPlanningQuestion) };
        var wire = PlanningCorpus.Transport(response, schema, schema)!;
        var findings = PlanningModelCalls.ValidateResponseFindings(wire, schema);
        if (policy == "diagnostic") Assert.Empty(findings);
        else Assert.Contains(findings, finding => finding.Rule == "maxItems");
        Assert.Equal(authority, PlanningRepairPatch.Authority(state));
        if (policy == "diagnostic")
        {
            state.ReplanAttempts = 2; state.Construction!.DraftCorrections = 1;
            var questions = Questions(4, 4);
            PlanningClarifications.Suspend(state, questions);
            state.PendingQuestions = questions; state.Status = PlanningStatus.Clarification;
            var runtime = new TestRuntime();
            state = await new HybridWorkflowPlanner().AdvanceAsync(PlannerFixture.Clone(state),
                new() { Kind = "answer", ExpectedRevision = state.Revision,
                    Answers = questions.Select(question => new PlanningAnswer(question.Id, Text: new string('x', 9000))).ToList() }, runtime, PlannerFixture.Ct);
            Assert.Empty(runtime.Calls); Assert.Equal(2, state.ReplanAttempts); Assert.Equal(1, state.Construction!.DraftCorrections);
            Assert.Equal(authority, PlanningRepairPatch.Authority(state));
            Assert.Equal(4, Assert.Single(state.AnswerHistory!).Answers.Count);
        }
        Assert.Equal(new[] { "/tasks/number/outputs/value" }, state.EditablePaths);
        Assert.Null(state.ApprovedHash);
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("token")]
    [InlineData("diagnostic")]
    public void MissingFactsStillPermitNoAlternativesAndNoRecommendation(string policy)
    {
        var state = State(policy);
        PlanningClarifications.Suspend(state, Questions(1, 0));
        Assert.NotNull(state.Request.Options["clarification_context"]);
        Assert.Null(state.ThresholdWarnings);
    }

    private static async Task<(PlanningSession State, TestRuntime Runtime)> Asking(string phase)
    {
        var model = new CurrentPlanningFixture.LiteralModel();
        var questions = Questions(4, 4);
        var runtime = new TestRuntime { Respond = (request, purpose) =>
        {
            var schema = request.StructuredOutputSchema!.AsObject();
            if (purpose == phase)
            {
                var response = new JsonObject { ["clarifications"] = JsonSerializer.SerializeToNode(questions, PlanningJsonContext.Default.ListPlanningQuestion) };
                if (schema["properties"]?["baseHash"]?["enum"]?[0] is { } baseline) response["baseHash"] = baseline.DeepClone();
                var wire = PlanningCorpus.Transport(response, schema, schema)!;
                Assert.Empty(PlanningModelCalls.ValidateResponseFindings(wire, schema));
                return new() { Json = wire };
            }
            if (phase == PlanningCompactConstruction.DiscoveryPurpose && purpose == PlanningCompactConstruction.ImplementationPurpose)
            {
                var selection = new JsonObject { ["step"] = new JsonObject
                {
                    ["scope"] = "/root", ["kind"] = null, ["operationIds"] = new JsonArray(), ["inputs"] = new JsonArray(), ["dependencyTaskIds"] = new JsonArray(),
                    ["closeRequirementIds"] = new JsonArray("req_0"), ["outputNames"] = new JsonArray("result")
                } };
                selection["baseHash"] = schema["properties"]!["baseHash"]!["enum"]![0]!.DeepClone();
                var wire = PlanningCorpus.Transport(selection, schema, schema)!;
                Assert.Empty(PlanningModelCalls.ValidateResponseFindings(wire, schema));
                return new() { Json = wire };
            }
            return model.CallAsync(request, PlannerFixture.Ct).GetAwaiter().GetResult();
        } };
        var state = State("diagnostic");
        for (var tick = 0; tick < 16 && !PlanningStatus.IsWaiting(state.Status) && !PlanningStatus.IsTerminal(state.Status); tick++)
            state = await new HybridWorkflowPlanner().AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, PlannerFixture.Ct);
        return (state, runtime);
    }

    private static List<PlanningQuestion> Questions(int count, int alternatives) => Enumerable.Range(0, count).Select(index =>
        new PlanningQuestion("question_" + index, "Which presentation is required for item " + index + "?",
            Enumerable.Range(0, alternatives).Select(option => new PlanningQuestionAlternative("option_" + option, "Presentation " + option)).ToList(),
            alternatives == 0 ? null : "option_0")).ToList();

    private static PlanningSession State(string policy)
    {
        var options = new JsonObject { ["automatic_correction"] = true };
        if (policy == "diagnostic") options[PlanningTokenThresholds.OptimizationOptionName] = "diagnostic-v1";
        if (policy == "token") options[PlanningTokenThresholds.OptionName] = "advisory";
        return PlannerFixture.Capture(new() { Request = new()
        {
            TenantId = "clarification-target", Prompt = "Return the required public numeric result named result with value 42.",
            MaxModelCalls = 64, MaxReplanAttempts = 4, Options = options, Generation = new("medium", 32000, 8192)
        } });
    }

    private static string Snapshot(PlanningSession state) => JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSession);
}
