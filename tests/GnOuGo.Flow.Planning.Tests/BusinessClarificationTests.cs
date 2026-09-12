using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class BusinessClarificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningSnapshot Session(string prompt = "Publish the result or retain the draft.") => new()
    {
        Request = new() { TenantId = "tenant", Prompt = prompt }, Intent = new() { Checked = true },
        Preparation = TypedPlannerTests.Preparation()
    };

    private static string Evidence(PlanningSnapshot state, string fragment, string kind, string id, bool required = false)
    {
        var parent = PlanningChoiceEvidence.Clauses(state).Single(r => PlanningChoiceEvidence.Text(state, r.Id).Contains(fragment, StringComparison.Ordinal));
        var text = PlanningSourceDecisions.Sources(state)[parent.SourceId];
        var offset = text.IndexOf(fragment, parent.Start, StringComparison.Ordinal);
        var reference = parent with { Id = "ref_" + id, Kind = parent.Kind + ":selection", Start = offset, Length = fragment.Length };
        state.References.Add(reference); state.Obligations.Add(new(id, [reference.Id], "business_decision", kind, required));
        return reference.Id;
    }

    private static PlanningBusinessDecision Domain(PlanningSnapshot state, bool complete = true)
    {
        var subject = Evidence(state, state.Request.Prompt, "business_choice", "choice");
        var first = Evidence(state, "Publish the result", "external_write", "publish");
        var second = Evidence(state, "retain the draft", "local_processing", "retain");
        state.Preparation!.Capabilities = [new() { Id = "implementation", StepType = "mcp.call", Resolution = "mcp", EffectKind = "write", OperationIds = ["publish"], Required = false },
            new() { Id = "local", StepType = "set", Resolution = "local", EffectKind = "none", OperationIds = ["retain"], Required = false }];
        var decision = new PlanningBusinessDecision { Id = "question_choice", ObligationId = "choice", SubjectReference = subject,
            EvidenceReferences = [subject], Status = "analyzed", CompleteDomain = complete, AffectedObligations = ["publish"],
            Alternatives = [new() { Id = "choice_publish", EvidenceReference = first, Label = "Publish", OperationIds = ["publish"] },
                new() { Id = "choice_draft", EvidenceReference = second, Label = "Keep the draft", OperationIds = [] }] };
        state.BusinessDecisions.Add(decision); return decision;
    }

    private static TypedPlannerTests.FakeRuntime ResolveWith(string kind, string reference) => new()
    {
        OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject()
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["kind"] = kind, ["reference"] = reference }))) })
    };
    private static Task Resolve(PlanningSnapshot state, IPlanningRuntime? runtime = null, bool ask = true)
        => PlanningClarifications.ResolveAsync(state, runtime ?? new TypedPlannerTests.FakeRuntime(), ask, Ct);
    private static async Task<PlanningSnapshot> Ask(PlanningSnapshot state, IPlanningRuntime? runtime = null)
    {
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Resolve(state, runtime));
        Assert.Equal("PLANNING_CLARIFICATION_REQUIRED", error.Code);
        state.Intent.Question = System.Text.Json.JsonSerializer.Deserialize(error.Details!["question"], PlanningJsonContext.Default.HumanInputRequest);
        state.Status = PlanningStatus.Clarification; state.Intent.Forms = 1; state.Intent.Questions = 1;
        return state;
    }

    [Fact]
    public async Task CapturedOmissionFragmentUsesContainingDefaultClauseWithoutClarification()
    {
        // Sanitized interpretation from retained run f29bac6f; responses below are synthetic, not live receipts.
        const string clause = "Optional input threshold is a non-nullable number defaulting to 100 when omitted.";
        var state = Session(clause);
        Evidence(state, "when omitted.", "business_choice", "omission");
        var input = Evidence(state, "Optional input threshold is a non-nullable number defaulting to 100", "business_input", "input");
        var runtime = ResolveWith("runtime", input);
        await Resolve(state, runtime);
        Assert.Null(state.Intent.Question); Assert.Null(state.Outcome); Assert.Equal(0, state.Intent.Questions);
        Assert.Equal("runtime", Assert.Single(state.BusinessDecisions).Status);
        Assert.Contains("defaulting to 100", Assert.Single(runtime.Requests).Prompt);
        var plan = PlanningBehaviorDecisions.Assemble(state, new());
        Assert.Contains("defaulting to 100", Assert.Single(plan.Workflows[0].Inputs).Description);
        Assert.False(plan.Workflows[0].Inputs[0].Required);
        Assert.DoesNotContain(runtime.Phases, p => p == "clarification");
        var restarted = PlanningContext.Clone(state); var replay = new TypedPlannerTests.FakeRuntime();
        await Resolve(restarted, replay); Assert.Empty(replay.Requests);
        Assert.Equal(state.Events.Count, restarted.Events.Count);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("answer")]
    [InlineData("policy")]
    [InlineData("baseline")]
    public async Task GoverningReferencesAreScopedToTheirCurrentSource(string origin)
    {
        var state = Session("Choose the result behavior.");
        Evidence(state, state.Request.Prompt, "business_choice", "choice");
        string reference;
        if (origin == "answer") state.Intent.Answers.Add(new("Prior behavior", new() { ["value"] = "Retain the draft." }));
        if (origin == "policy") state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Retain the draft." };
        if (origin == "baseline") state.Request.Baseline = TypedPlannerTests.Graph();
        if (origin == "intent")
            reference = Evidence(state, state.Request.Prompt, "explicit_value", "value");
        else reference = PlanningChoiceEvidence.Clauses(state).First(r => PlanningChoiceEvidence.Origin(state, r.Id) == origin).Id;
        await Resolve(state, ResolveWith("settled", reference));
        Assert.Equal(origin, Assert.Single(state.BusinessDecisions).ResolutionOrigin);
        Assert.Null(state.Outcome);
    }

    [Fact]
    public async Task DiscoveryAloneNeverGrantsPermissionToQuestion()
    {
        var state = Session(); Evidence(state, state.Request.Prompt, "business_choice", "choice"); state.Preparation = null;
        var runtime = new TypedPlannerTests.FakeRuntime(); await Resolve(state, runtime);
        Assert.Empty(runtime.Requests); Assert.Empty(state.BusinessDecisions); Assert.Null(state.Outcome);
    }

    [Fact]
    public async Task OneAdmissibleChoiceAndIdenticalBehaviorResolveWithoutCalls()
    {
        var state = Session(); var decision = Domain(state);
        decision.Constraints.Add(new("require", "choice_draft", decision.SubjectReference, "intent"));
        var runtime = new TypedPlannerTests.FakeRuntime(); await Resolve(state, runtime);
        Assert.Equal("choice_draft", decision.SelectedChoiceId); Assert.Empty(runtime.Requests); Assert.Null(state.Outcome);
        Assert.False(PlanningBusinessAnswers.Included(state, "publish"));
        Assert.DoesNotContain(PlanningBehaviorPlans.Enumerate(PlanningBehaviorDecisions.Assemble(state, new()).Workflows[0].Steps), n => n.OperationIds.Contains("publish"));
        state = Session(); decision = Domain(state);
        decision.Alternatives[1].OperationIds = ["publish"]; decision.Alternatives[1].EvidenceReference = decision.Alternatives[0].EvidenceReference;
        await Resolve(state, runtime); Assert.Equal("equivalence", decision.ResolutionOrigin);
    }

    [Theory]
    [InlineData(true, "BUSINESS_CHOICE_UNSUPPORTED")]
    [InlineData(false, "BUSINESS_CHOICE_PROOF_UNAVAILABLE")]
    public async Task EmptyDomainNeedsCompleteAssessmentAndExclusionEvidence(bool complete, string code)
    {
        var state = Session(); var decision = Domain(state, complete);
        foreach (var choice in decision.Alternatives) decision.Constraints.Add(new("deny", choice.Id, decision.SubjectReference, "intent"));
        if (complete)
        { await Resolve(state); Assert.Equal(code, Assert.Single(Assert.IsType<PlanningUnsupported>(state.Outcome).Obligations).Code); }
        else
        { Assert.Equal(code, (await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Resolve(state))).Code); Assert.Null(state.Outcome); }
    }

    [Theory]
    [InlineData("omitted", "omitted", true)]
    [InlineData("explicit_null", "omitted", false)]
    [InlineData("explicit_null", "explicit_null", true)]
    public async Task DefaultsRequireDeclaredApplicability(string presence, string condition, bool resolved)
    {
        var state = Session(); var decision = Domain(state); decision.ValuePresence = presence;
        decision.Constraints.Add(new("require", "choice_draft", decision.SubjectReference, "intent", condition));
        await Resolve(state, ask: false);
        Assert.Equal(resolved ? "resolved" : "eligible", decision.Status);
        Assert.Equal(resolved ? "choice_draft" : null, decision.SelectedChoiceId);
    }

    [Fact]
    public async Task MissingApplicabilityProofIsTechnicalRatherThanAUserQuestion()
    {
        var state = Session(); var decision = Domain(state);
        decision.Constraints.Add(new("require", "choice_draft", decision.SubjectReference, "intent", "omitted"));
        Assert.Equal("BUSINESS_CHOICE_APPLICABILITY_UNPROVEN", (await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Resolve(state))).Code);
        Assert.Null(state.Outcome);
    }

    [Fact]
    public void ContainingClauseCrossesInterpretationPagesWithoutDroppingDeclaredValues()
    {
        var text = "An optional business input with " + string.Join(' ', Enumerable.Repeat("declared", 40)) + " default 100.5 when omitted.";
        var state = Session(text);
        var last = PlanningChoiceEvidence.Clauses(state).Last();
        var clause = PlanningChoiceEvidence.Parent(state, last.Id);
        Assert.Equal(text, PlanningChoiceEvidence.Text(state, clause.Id));
        var boundaries = PlanningReferences.Boundaries(clause, text);
        Assert.Contains("100.5", boundaries.Context.ToJsonString());
    }

    [Fact]
    public async Task QuestionsShowLabelsAndJustifiedPreferenceButNeverSelectIt()
    {
        var state = Session(); var decision = Domain(state);
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Prefer retaining the draft." };
        var policy = PlanningChoiceEvidence.Clauses(state).Single(r => r.SourceId == "host").Id;
        state.Obligations.Add(new("preference", [policy], "business_decision", "business_preference", false));
        decision.EvidenceReferences.Add(policy); decision.Constraints.Add(new("prefer", "choice_draft", policy, "policy"));
        await Ask(state);
        var question = Assert.IsType<PlanningNeedUserClarification>(state.Outcome).Decision;
        Assert.Equal(2, question.Choices.Count); var preferred = Assert.Single(question.Choices, c => c.Preferred);
        Assert.Equal("Keep the draft", preferred.Label); Assert.Contains("policy", preferred.PreferredReason);
        Assert.Null(decision.SelectedChoiceId); Assert.True(state.Intent.Question!.Fields![0].AllowCustomAnswer);
        Assert.Equal(question.Choices.Select(c => c.Id), state.Intent.Question.Fields[0].Options);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SuggestedChoicesAreBoundedWithoutDiscardingTheLargerDomain(int count)
    {
        var state = Session("Publish the result or retain the draft or send a notice or store a copy or share a summary.");
        var decision = Domain(state);
        var fragments = new[] { "send a notice", "store a copy", "share a summary" };
        for (var index = 2; index < count; index++)
        {
            var operation = "operation_" + index;
            var reference = Evidence(state, fragments[index - 2], "external_write", operation);
            decision.Alternatives.Add(new() { Id = "choice_" + index, Label = fragments[index - 2], EvidenceReference = reference, OperationIds = [operation] });
            decision.AffectedObligations.Add(operation);
            state.Preparation!.Capabilities.Add(new() { Id = "cap_" + index, StepType = "mcp.call", Resolution = "mcp", EffectKind = "write", OperationIds = [operation] });
        }
        await Ask(state);
        var choices = ((PlanningNeedUserClarification)state.Outcome!).Decision.Choices;
        Assert.Equal(Math.Min(4, count), choices.Count); Assert.Equal(count, decision.Alternatives.Count);
        Assert.Equal(choices.Count, choices.Select(c => c.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.All(choices, c => { Assert.False(c.Preferred); Assert.Null(c.PreferredReason); });
    }

    [Fact]
    public async Task RealSessionConstructsIndependentBehaviorBeforeQuestionAndRetainsItAsUnapproved()
    {
        var state = Session(); Domain(state);
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "advance", ExpectedRevision = state.Revision }, new TypedPlannerTests.FakeRuntime(), Ct);
        Assert.Equal(PlanningStatus.Clarification, result.Status);
        Assert.NotNull(result.BehaviorAssessment.Candidate); Assert.Null(result.ApprovedBehaviorHash); Assert.Null(result.Graph);
        Assert.Single(result.Intent.Question!.Fields!);
    }

    [Fact]
    public async Task ConflictingPreferencesNeverCreateAnArbitraryFavorite()
    {
        var state = Session(); var decision = Domain(state);
        state.Obligations.Add(new("preference", [decision.SubjectReference], "business_decision", "business_preference", false));
        foreach (var alternative in decision.Alternatives) decision.Constraints.Add(new("prefer", alternative.Id, decision.SubjectReference, "intent"));
        await Ask(state); Assert.All(((PlanningNeedUserClarification)state.Outcome!).Decision.Choices, c => Assert.False(c.Preferred));
    }

    [Fact]
    public async Task TypedAnswersRetainIdentitySessionBudgetsAndRejectStaleScopes()
    {
        var state = Session(); Domain(state); await Ask(state);
        var runtime = new TypedPlannerTests.FakeRuntime(); var planner = new TypedWorkflowPlanner();
        var next = await planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = new() { ["question_choice"] = "choice_draft" } }, runtime, Ct);
        Assert.Equal(state.Request.SessionId, next.Request.SessionId); Assert.Empty(runtime.Requests);
        Assert.Equal("choice_draft", Assert.Single(next.Intent.Answers).Answers["question_choice"]!.ToString());
        Assert.True(next.Intent.Checked); Assert.NotNull(next.Preparation); Assert.Null(next.BehaviorPlan); Assert.Null(next.Outcome);
        Assert.Equal(state.Intent.Forms, next.Intent.Forms); Assert.Equal(state.DecisionCorrections, next.DecisionCorrections);
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(next, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = new() { ["question_choice"] = "choice_publish" } }, runtime, Ct));
        state.Preparation!.Fingerprint = "changed";
        await Assert.ThrowsAsync<PlanningConflictException>(() => planner.AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = new() { ["question_choice"] = "choice_draft" } }, runtime, Ct));
    }

    [Fact]
    public async Task TransportSettingsDoNotStaleBusinessEvidenceAndRepairsCannotUndoTheAnswer()
    {
        var state = Session(); Domain(state); await Ask(state);
        state.Request.Generation.MaxInputTokensPerRequest = 10_000;
        var next = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "answer", ExpectedRevision = state.Revision,
            Answers = new() { ["question_choice"] = "choice_draft" } }, new TypedPlannerTests.FakeRuntime(), Ct);
        var plan = PlanningBehaviorDecisions.Assemble(next, new());
        Assert.Empty(PlanningBusinessAnswers.ValidateBehavior(next, plan));
        plan.Workflows[0].Steps.Add(new() { Key = "reintroduced", Kind = "operation", CapabilityId = "implementation", OperationIds = ["publish"] });
        Assert.Equal("BUSINESS_CHOICE_BEHAVIOR_CONFLICT", Assert.Single(PlanningBusinessAnswers.ValidateBehavior(next, plan)).Code);
    }

    [Fact]
    public async Task ResolutionReachesClosureBeforeAskingAboutAnEarlierCoupledChoice()
    {
        var state = Session(); var first = Domain(state);
        var second = new PlanningBusinessDecision { Id = "question_z", ObligationId = "z", SubjectReference = first.SubjectReference,
            EvidenceReferences = first.EvidenceReferences.ToList(), Status = "analyzed", CompleteDomain = true, AffectedObligations = ["publish"],
            Alternatives = [new() { Id = "keep", Label = "Keep the draft", EvidenceReference = first.Alternatives[1].EvidenceReference }] };
        state.BusinessDecisions.Add(second); state.Obligations.Add(new("z", [first.SubjectReference], "business_decision", "business_choice", true));
        var runtime = new TypedPlannerTests.FakeRuntime(); await Resolve(state, runtime);
        Assert.Equal("choice_draft", first.SelectedChoiceId); Assert.Equal("keep", second.SelectedChoiceId);
        Assert.Null(state.Outcome); Assert.Empty(runtime.Requests);
    }

    [Theory]
    [InlineData("choice_draft", true)]
    [InlineData("unresolved", false)]
    public async Task CustomAnswersUseBoundedChoiceReferencesAndRemainStagedWhenUnresolved(string response, bool accepted)
    {
        var state = Session(); Domain(state); await Ask(state);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(response)))) }) };
        var command = new PlanningCommand { Kind = "answer", ExpectedRevision = state.Revision, Answers = new() { ["question_choice"] = "Keep it private. Do not publish." } };
        var next = await new TypedWorkflowPlanner().AdvanceAsync(state, command, runtime, Ct);
        Assert.Equal(accepted ? PlanningStatus.Created : PlanningStatus.Clarification, next.Status);
        Assert.Equal(accepted ? 1 : 0, next.Intent.Answers.Count); Assert.Single(runtime.Requests);
        if (!accepted)
        {
            command.ExpectedRevision = next.Revision; var replay = new TypedPlannerTests.FakeRuntime();
            next = await new TypedWorkflowPlanner().AdvanceAsync(next, command, replay, Ct);
            Assert.Empty(replay.Requests); Assert.Equal(1, next.Intent.Forms);
        }
    }

    [Fact]
    public async Task UnknownContractsAndTechnicalImplementationDifferencesNeverBecomeQuestions()
    {
        var state = Session(); var decision = Domain(state); state.Preparation!.Capabilities[0].EffectKind = "unknown";
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Resolve(state));
        Assert.Equal("BUSINESS_CHOICE_PROOF_UNAVAILABLE", error.Code); Assert.Null(state.Outcome);
        Assert.Equal("/obligations/@choice", state.Diagnostics[0].Location);
        Assert.Null(decision.SelectedChoiceId);
    }

    [Fact]
    public async Task UnknownSemanticProofStopsAtItsCanonicalDecisionLocation()
    {
        var state = Session(); Evidence(state, state.Request.Prompt, "business_choice", "choice");
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
            new JsonObject { ["kind"] = "technical" }))) }) };
        var result = await new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = "advance", ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, result.Status); Assert.Null(result.Outcome);
        Assert.Equal("/obligations/@choice", result.TechnicalStop!.Location); Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task CurrentAnswersSupersedeEarlierChoicesButNeverMandatoryPolicy()
    {
        var state = Session(); var decision = Domain(state);
        state.Intent.Answers.Add(new("Earlier", new() { ["business"] = "Publish." }));
        state.Intent.Answers.Add(new("Current", new() { ["business"] = "Keep the draft." }));
        var answers = PlanningChoiceEvidence.Clauses(state).Where(r => PlanningChoiceEvidence.Origin(state, r.Id) == "answer").ToArray();
        decision.EvidenceReferences.AddRange(answers.Select(r => r.Id));
        decision.Constraints.Add(new("require", "choice_publish", answers[0].Id, "answer"));
        decision.Constraints.Add(new("require", "choice_draft", answers[1].Id, "answer"));
        await Resolve(state); Assert.Equal("choice_draft", decision.SelectedChoiceId);

        state = Session(); decision = Domain(state);
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Publication is forbidden." };
        var policy = PlanningChoiceEvidence.Clauses(state).Single(r => r.SourceId == "host").Id;
        decision.EvidenceReferences.Add(policy);
        decision.Constraints.Add(new("require", "choice_publish", decision.SubjectReference, "intent"));
        decision.Constraints.Add(new("deny", "choice_publish", policy, "policy"));
        await Resolve(state); Assert.IsType<PlanningUnsupported>(state.Outcome); Assert.Null(decision.SelectedChoiceId);
    }

    [Fact]
    public async Task SourceInterpretationCannotInventPreferenceAuthorityOrCompleteDomains()
    {
        var state = Session(); Evidence(state, state.Request.Prompt, "business_choice", "choice");
        Evidence(state, "Publish the result", "external_write", "publish");
        state.Preparation!.Capabilities = [new() { Id = "cap", StepType = "mcp.call", Resolution = "mcp", EffectKind = "write", OperationIds = ["publish"] }];
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            var answer = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                new JsonObject { ["kind"] = "planning", ["alternatives"] = new JsonArray(
                    new JsonObject { ["start"] = "b0", ["end"] = "b3", ["operations"] = new JsonArray("publish") },
                    new JsonObject { ["start"] = "b4", ["end"] = "b7", ["operations"] = new JsonArray() }) })));
            Assert.Empty(PlanningContractValidation.ValidateInstance(answer, request.StructuredOutputSchema.AsObject()));
            var invented = answer.DeepClone(); invented.AsObject().First().Value!["preferred"] = true;
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(invented, request.StructuredOutputSchema.AsObject()));
            return Task.FromResult(new LLMResponse { Json = answer });
        } };
        await Ask(state, runtime);
        Assert.False(Assert.Single(state.BusinessDecisions).CompleteDomain);
        Assert.All(((PlanningNeedUserClarification)state.Outcome!).Decision.Choices, c => Assert.False(c.Preferred));
    }

    [Fact]
    public async Task OrdinaryInputEvidenceDoesNotExposePreferenceAssignmentsToTheModel()
    {
        var state = Session(); var decision = Domain(state);
        Evidence(state, "retain the draft", "business_input", "input");
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            Assert.DoesNotContain("\"prefer\"", request.StructuredOutputSchema!.ToJsonString());
            return Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema["properties"]!.AsObject()
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, new JsonObject { ["choice"] = "unrelated", ["rule"] = "require", ["applicability"] = "always" }))) });
        } };
        await PlanningBusinessGovernance.AssessAsync(state, runtime, decision, Ct);
        Assert.Single(runtime.Requests); Assert.Empty(decision.Constraints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenCodeReviewRuntimeRulesDoNotAskUnlessBusinessOutcomesRemain(bool renamed)
    {
        // Frozen catalog/policy are evidence inputs; the interpretation and locked
        // contracts below are explicitly synthetic deterministic regression fixtures.
        var catalog = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodeReviewMatchingCatalog.json"), Ct);
        var policy = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CodeReviewPolicy.txt"), Ct);
        Assert.NotEmpty(JsonNode.Parse(catalog)!.AsArray());
        if (renamed) policy = policy.Replace("git_compare_refs", "compare_owned", StringComparison.Ordinal).Replace("copilot_review", "analyze_owned", StringComparison.Ordinal);
        var state = Session("Publish only after permission. Rejected permission performs no publication. Discover available checks at execution. Always clean the owned resource.");
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = policy };
        var publication = Evidence(state, "Publish only after permission.", "external_write", "publish", true);
        Evidence(state, "only after permission.", "business_choice", "permission");
        Evidence(state, "Rejected permission performs no publication.", "runtime_condition", "denied", true);
        Evidence(state, "Discover available checks at execution.", "external_read", "discover", true);
        Evidence(state, "Always clean the owned resource.", "cleanup", "cleanup", true);
        state.Preparation!.Capabilities = [new() { Id = renamed ? "impl_renamed" : "impl", Server = renamed ? "Other" : "Git",
            StepType = "mcp.call", Resolution = "mcp", EffectKind = "write", Required = true, OperationIds = ["publish"] }];
        state.Preparation.Decisions = [new() { Group = "permission", SourceOperationId = "permission_response", EffectOperationIds = ["publish"],
            AllowedValues = ["accepted", "rejected"], NoEffectValues = ["rejected"], ContractSource = PlanningDecisionContract.HumanConfirmation }];
        var before = state.Preparation.Decisions[0]; var runtime = new TypedPlannerTests.FakeRuntime();
        await Resolve(state, runtime);
        Assert.Empty(runtime.Requests); Assert.Null(state.Outcome); Assert.Equal("runtime", Assert.Single(state.BusinessDecisions).Status);
        Assert.Same(before, state.Preparation.Decisions[0]); Assert.Contains(publication, state.BusinessDecisions[0].EvidenceReferences);
        var eventCount = state.Events.Count;
        await Resolve(state, runtime); Assert.Equal(eventCount, state.Events.Count); Assert.Empty(runtime.Requests);
        state.Preparation.Decisions.Clear();
        Assert.Equal("BUSINESS_CHOICE_PROOF_STALE", (await Assert.ThrowsAsync<WorkflowRuntimeException>(() => Resolve(state, runtime))).Code);

        var unresolved = Session(); Domain(unresolved, complete: false);
        unresolved.Request.Options["policy"] = new JsonObject { ["instructions"] = policy };
        await Ask(unresolved); Assert.Single(unresolved.Intent.Question!.Fields!);
        Assert.Equal(1, unresolved.Intent.Questions);
    }
}
