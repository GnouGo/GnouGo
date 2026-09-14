using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using GnOuGo.Flow.Planning.Capabilities;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic semantic answers. They never replace retained model receipts.</summary>
public sealed class OperationAdmissionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningSnapshot State(string prompt) => new() { Request = new() { TenantId = "tenant", SessionId = "operation-fixture", Prompt = prompt } };
    internal static JsonObject None() => new() { ["status"] = "not_an_operation" };
    internal static JsonObject Actions(params JsonObject[] actions) => new() { ["status"] = "operations", ["actions"] = new JsonArray(actions.Select(a => a.DeepClone()).ToArray()) };
    internal static JsonObject Action(PlanningOperations.Scope scope, string kind = "local_processing", string? target = null, string? baseline = null, string? fragment = null)
    {
        var words = scope.Words.ToArray(); var from = 0; var count = words.Length;
        if (fragment is not null)
        {
            var selected = fragment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            from = Enumerable.Range(0, words.Length).First(i => words.Skip(i).Take(selected.Length).Select(p => p.Value!.ToString()).SequenceEqual(selected)); count = selected.Length;
        }
        return new() { ["kind"] = kind, ["required"] = true, ["target"] = target, ["baseline"] = baseline, ["start"] = "b" + from, ["end"] = "b" + (from + count) };
    }
    internal static TypedPlannerTests.FakeRuntime Runtime(PlanningSnapshot state, Func<PlanningOperations.Scope, LLMRequest, JsonObject> answer) => new()
    {
        OnCall = (phase, request, _) =>
        {
            Assert.Equal("intent_operations", phase);
            var response = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, answer(PlanningOperations.Scopes(state).Single(s => PlanningOperations.DecisionId(s) == p.Key), request))));
            return Task.FromResult(new LLMResponse { Json = response, CompletionStatus = "completed" });
        }
    };
    internal static string ReuseTarget(LLMRequest request) => request.StructuredOutputSchema!["properties"]!.AsObject().Single().Value!["anyOf"]!.AsArray()
        .Single(v => v!["properties"]?["actions"] is not null)!["properties"]!["actions"]!["items"]!["anyOf"]!.AsArray()
        .First(v => v!["properties"]!["target"]!["enum"] is not null)!["properties"]!["target"]!["enum"]![0]!.ToString();

    [Fact]
    public async Task CapturedStageOneKeepsProvenDeclarationsAndGainsOneClassificationAction()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "operation-admission-stage1.json"), Ct))!;
        var state = JsonSerializer.Deserialize(fixture.ToJsonString(), PlanningJsonContext.Default.PlanningSnapshot)!;
        var original = fixture.ToJsonString();
        var assignments = state.DeclarationAssignments.ToList();
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation);
        var rule = state.Obligations.Single(o => o.Kind == "workflow_policy" && PlanningSourceDecisions.Text(state, o).StartsWith("Classify as", StringComparison.Ordinal));
        var runtime = Runtime(state, (scope, request) => scope.Clause.Start == 0 ? Actions(Action(scope)) : scope.Clause.Id == rule.Grounding!.ClauseReference
            ? Actions(Action(scope, target: ReuseTarget(request))) : None());
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind); Assert.Equal(2, operation.OperationAdmission!.Assignments.Count);
        Assert.Contains("amount>=threshold", PlanningSourceDecisions.Text(state, operation));
        Assert.Contains(rule, state.Obligations);
        // Re-assess retained declaration answers under current operation evidence;
        // only the admission replies above are new synthetic semantic answers.
        PlanningDeclarations.Commit(state, assignments, PlanningDeclarations.EvidenceFingerprint(state));
        state.Preparation = TypedPlannerTests.Preparation();
        state.Preparation.Capabilities.Add(new() { Id = "local", StepType = "set", Resolution = "local", Required = true, OperationIds = [operation.Id] });
        var plan = PlanningBehaviorDecisions.Assemble(state, new());
        var workflow = Assert.Single(plan.Workflows);
        Assert.Equal(["record", "threshold"], workflow.Inputs.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("classifiedResult", Assert.Single(workflow.Outputs).Name);
        Assert.Contains("category has exactly", workflow.Outputs[0].Description);
        Assert.Contains("Preserve the original", workflow.Outputs[0].Description);
        var threshold = state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold");
        Assert.False(threshold.Required); Assert.Equal(100m, PlanningDeclarations.Default(state, threshold)!.Number);
        Assert.Equal([operation.Id], Assert.Single(workflow.Steps).OperationIds);
        Assert.Empty(PlanningDeclarations.ValidateBehavior(state, plan)); Assert.Equal(original, fixture.ToJsonString());
    }

    [Theory]
    [InlineData("Classify a record according to rules.")]
    [InlineData("Transform an entry according to instructions.")]
    [InlineData("Aggregate the supplied values.")]
    public async Task RequestedProcessingIsAdmittedDespitePolicyOrAbsentHints(string text)
    {
        var state = State(text);
        var hint = PolicyGroundingTests.Add(state, "request", text, "preliminary-policy", "workflow_policy");
        var runtime = Runtime(state, (scope, _) => Actions(Action(scope)));
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind); Assert.NotEqual(hint.Id, operation.Id);
        Assert.Contains(hint, state.Obligations); Assert.Equal("workflow_policy", hint.Kind);
        Assert.Equal(text, PlanningSourceDecisions.Text(state, operation));
        PlanningOperations.RequireCurrent(state);
        var noHints = State(text); await PlanningOperations.ResolveAsync(noHints, Runtime(noHints, (scope, _) => Actions(Action(scope))), Ct);
        Assert.Equal(operation.Id, Assert.Single(noHints.Obligations).Id);
    }

    [Fact]
    public async Task ConditionsReuseOneCanonicalActionWithoutLosingTheirOtherSemantics()
    {
        var state = State("Classify a record. Reject when approval is false; accept otherwise.");
        var condition = PolicyGroundingTests.Add(state, "request", "Reject when approval is false;", "condition", "runtime_condition");
        var fallback = PolicyGroundingTests.Add(state, "request", "accept otherwise.", "fallback", "runtime_fallback");
        var runtime = Runtime(state, (scope, request) => Actions(Action(scope, target: scope.Clause.Start == 0 ? null : ReuseTarget(request))));
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal(3, operation.OperationAdmission!.Assignments.Count); Assert.Contains(condition, state.Obligations); Assert.Contains(fallback, state.Obligations);
        Assert.Contains("approval is false", PlanningSourceDecisions.Text(state, operation)); Assert.Contains("accept otherwise", PlanningSourceDecisions.Text(state, operation));
        Assert.Equal(2, state.Events.Single(e => e.Kind == "operation_evidence_reused").Count);
    }

    [Fact]
    public async Task MultipleRequestedActionsWithinAndAcrossClausesRemainDistinct()
    {
        var state = State("Read X and classify it. Send the result.");
        var runtime = Runtime(state, (scope, _) => scope.Clause.Start == 0
            ? Actions(Action(scope, "external_read", fragment: "Read X"), Action(scope, fragment: "classify it."))
            : Actions(Action(scope, "external_write")));
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.Equal(3, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.Equal(["external_read", "external_write", "local_processing"], state.Obligations.Select(o => o.Kind).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task PolicyMentionWithoutRequestedWriteDoesNotCreateAnyOperation()
    {
        var state = State("Confirm before an external write.");
        PolicyGroundingTests.Add(state, "request", state.Request.Prompt, "policy", "workflow_policy");
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "An external write requires permission." };
        PolicyGroundingTests.Add(state, "host", "An external write requires permission.", "host-policy", "workflow_policy");
        var runtime = Runtime(state, (_, _) => None());
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation); Assert.Single(runtime.Requests);
        Assert.All(PlanningOperations.Scopes(state), s => Assert.Equal(PlanningSourceAuthority.RequestedBehavior, s.Source.Authority));
        var inventoryRuntime = new TypedPlannerTests.FakeRuntime { OnCall = (_, r, _) => Task.FromResult(new LLMResponse
            { Json = PolicyGroundingTests.Reply(r, new() { ["policy"] = new() { ["rule"] = "not_confirmation_policy", ["reason"] = "other_workflow_constraint" },
                ["host-policy"] = new() { ["rule"] = "not_confirmation_policy", ["reason"] = "other_workflow_constraint" } }) }) };
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => CapabilityInventoryDecisions.BuildAsync(state, inventoryRuntime, Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code); Assert.Null(state.Outcome);
    }

    [Fact]
    public async Task WrongPreliminaryOperationKindHasNoAdmissionAuthority()
    {
        var state = State("Classify the supplied record.");
        var hint = PolicyGroundingTests.Add(state, "request", state.Request.Prompt, "wrong", "external_write"); hint.Disposition = "preliminary";
        Assert.False(PlanningSourceDecisions.IsOperation(hint));
        await PlanningOperations.ResolveAsync(state, Runtime(state, (scope, _) => Actions(Action(scope))), Ct);
        Assert.Equal("local_processing", Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).Kind);
        Assert.Equal("external_write", state.Obligations.Single(o => o.Id == "wrong").Kind);
    }

    [Fact]
    public async Task SourceInterpretationNeverAdmitsItsOwnOperationHints()
    {
        var state = State("Transform the input.");
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, r, _) => Task.FromResult(new LLMResponse { Json = TypedPlannerTests.FakeRuntime.Interpret(r) }) };
        await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct);
        Assert.All(state.Obligations, o => Assert.Equal("preliminary", o.Disposition));
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation); Assert.Null(state.OperationAdmissionFingerprint);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("kind")]
    [InlineData("required")]
    [InlineData("baseline")]
    [InlineData("description")]
    public void ReuseSchemaRejectsForeignOrContradictoryFields(string field)
    {
        var state = State("Transform the input. Follow its transformation rules.");
        var scopes = PlanningOperations.Scopes(state); var roots = new List<PlanningObligation>();
        var first = PlanningOperations.Decision(state, scopes[0], []); PlanningOperations.Apply(state, scopes[0], first, Actions(Action(scopes[0])), roots);
        var decision = PlanningOperations.Decision(state, scopes[1], roots); var action = Action(scopes[1], target: roots[0].Id);
        action[field] = field == "required" ? JsonValue.Create(false) : JsonValue.Create(field == "kind" ? "external_write" : "foreign");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Actions(action), decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.Apply(state, scopes[1], decision, Actions(action), roots));
        Assert.Single(roots);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("invalid_span")]
    [InlineData("duplicate")]
    public async Task IncompleteOrAmbiguousAdmissionCommitsNoPartialAuthority(string failure)
    {
        var state = State("Read X. Classify it.");
        var runtime = Runtime(state, (scope, _) =>
        {
            if (scope.Clause.Start == 0) return Actions(Action(scope, "external_read"));
            if (failure == "unresolved") return new() { ["status"] = "unresolved" };
            var action = Action(scope); if (failure == "invalid_span") { action["start"] = "b1"; action["end"] = "b1"; return Actions(action); }
            var other = action.DeepClone().AsObject(); other["kind"] = "external_write"; return Actions(action, other);
        });
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation); Assert.Null(state.OperationAdmissionFingerprint);
    }

    [Fact]
    public async Task ExactBaselineAuthorityAndDeclaredExecutorSemanticsAreRequired()
    {
        var state = State("Keep the existing workflow."); state.Request.Baseline = TypedPlannerTests.Graph();
        var baseline = PlanningSourceGroundingRules.BaselineNodes(state).Single().Key;
        var runtime = Runtime(state, (scope, _) => scope.Source.Authority == PlanningSourceAuthority.ExistingBehavior
            ? Actions(Action(scope, baseline: baseline)) : None());
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation); Assert.Equal(baseline, operation.OperationAdmission!.BaselineReference);
        var scope = PlanningOperations.Scopes(state).First(); var decision = PlanningOperations.Decision(state, scope, []);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Actions(Action(scope, baseline: "foreign")), decision.Schema));
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Actions(Action(scope, "external_write", baseline: baseline)), decision.Schema));
        state.Request.Baseline.Workflows[0].Steps.Clear();
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(state));
    }

    [Fact]
    public async Task ExactBaselineProofSurvivesReviewOfTheNewBehavior()
    {
        var state = State("Keep the existing workflow."); state.Request.Baseline = TypedPlannerTests.Graph();
        var node = PlanningSourceGroundingRules.BaselineNodes(state).Single().Key;
        await PlanningOperations.ResolveAsync(state, Runtime(state, (scope, _) => scope.Source.Authority == PlanningSourceAuthority.ExistingBehavior
            ? Actions(Action(scope, baseline: node)) : None()), Ct);
        var proof = state.OperationAdmissionFingerprint;
        state.BehaviorPlan = TypedPlannerTests.BehaviorPlan(); state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        PlanningOperations.RequireCurrent(state); Assert.Equal(proof, state.OperationAdmissionFingerprint);
    }

    [Fact]
    public void PolicyAuthorityHasNoActionAlternativeEvenWithBaselineAndStagedTargets()
    {
        var state = State("Transform a record."); state.Request.Baseline = TypedPlannerTests.Graph();
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = "Confirm before an external write." };
        var source = PlanningIntentAssessment.IntentSources(state).Single(s => s.Authority == PlanningSourceAuthority.ConstraintsOnly);
        var clause = PlanningReferences.ContainingClause(state, PlanningReferences.Register(state, source.Id, source.Kind, source.Text)[0], source.Text);
        var words = PlanningReferences.Boundaries(clause, source.Text);
        var scope = new PlanningOperations.Scope(source, clause, words.Context, words.Schema, words.Select);
        var decision = PlanningOperations.Decision(state, scope, []);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(Actions(Action(scope, "external_write")), decision.Schema));
        Assert.Empty(PlanningContractValidation.ValidateInstance(None(), decision.Schema));
    }

    [Fact]
    public async Task MalformedAnswerGetsOnlyTheExistingSingleCorrectionAndCharge()
    {
        var state = State("Transform a record."); state.Request.MaxRepairsPerWorkflowGate = 1;
        var count = 0;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            count++;
            var key = request.StructuredOutputSchema!["properties"]!.AsObject().Single().Key;
            Assert.Equal(count == 1 ? "intent_operations" : "intent_operations_repair", phase);
            var action = Action(PlanningOperations.Scopes(state).Single());
            var correctEnd = action["end"]!.DeepClone(); action["end"] = "foreign";
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [key] = count == 1 ? Actions(action)
                : new JsonObject(request.StructuredOutputSchema["properties"]![key]!["properties"]!.AsObject().Select(p =>
                    new KeyValuePair<string, JsonNode?>(p.Key, correctEnd.DeepClone()))) } });
        } };
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.Equal(2, count); Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Single(state.DecisionCorrections); Assert.Equal(1, Assert.Single(state.RepairAllowances).Attempts);
        var restored = PlanningContext.Clone(state);
        await PlanningOperations.ResolveAsync(restored, new TypedPlannerTests.FakeRuntime(), Ct);
        Assert.Single(restored.DecisionCorrections); Assert.Equal(1, Assert.Single(restored.RepairAllowances).Attempts);
    }

    [Fact]
    public async Task UnverifiableAdmissionStopsWithoutRedispatchOrPartialOperations()
    {
        var state = State("Read X. Transform it."); state.Intent.Checked = true;
        var runtime = new TypedPlannerTests.FakeRuntime();
        runtime.OnPrepareSnapshot = async snapshot => { await PlanningOperations.ResolveAsync(snapshot, runtime, Ct); return TypedPlannerTests.Preparation(); };
        runtime.OnCall = (_, _, _) => throw new LLMClientException(LLMClientFailureKind.Transport, "Synthetic unverifiable dispatch.", true);
        var planner = new TypedWorkflowPlanner();
        var stopped = await planner.AdvanceAsync(state, new() { ExpectedRevision = state.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, stopped.Status); Assert.True(stopped.TechnicalStop!.Unverifiable);
        Assert.DoesNotContain(stopped.Obligations, PlanningSourceDecisions.IsOperation); Assert.Single(runtime.Requests);
        var reopened = await planner.AdvanceAsync(PlanningContext.Clone(stopped), new() { ExpectedRevision = stopped.Revision }, runtime, Ct);
        Assert.Equal(PlanningStatus.Stopped, reopened.Status); Assert.Single(runtime.Requests); Assert.Empty(reopened.RepairAllowances);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("tenant")]
    [InlineData("source")]
    [InlineData("foreign_clause")]
    public async Task CurrentAdmissionCannotBeForgedOrTransferred(string defect)
    {
        var state = State("Transform a record.");
        await PlanningOperations.ResolveAsync(state, Runtime(state, (scope, _) => Actions(Action(scope))), Ct);
        if (defect == "fingerprint") state.OperationAdmissionFingerprint = null;
        if (defect == "tenant") state.Request.TenantId = "other";
        if (defect == "source") state.Request.Prompt += " Revised.";
        if (defect == "foreign_clause")
        {
            var operation = state.Obligations[0];
            state.Obligations[0] = operation with { OperationAdmission = operation.OperationAdmission! with
                { Assignments = [operation.OperationAdmission.Assignments[0] with { ClauseReference = "foreign" }] } };
        }
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(state));
    }

    [Theory]
    [InlineData("staged")]
    [InlineData("completed")]
    public async Task RestartReusesExactPagesAndDoesNotRepeatAccounting(string checkpoint)
    {
        var state = State("Read X. Classify it."); PlanningSnapshot? captured = null;
        var runtime = Runtime(state, (scope, _) => Actions(Action(scope, scope.Clause.Start == 0 ? "external_read" : "local_processing")));
        runtime.OnCheckpoint = s => { if (s.DecisionPages.Any(p => p.Status == checkpoint) && captured is null) captured = PlanningContext.Clone(s); return Task.CompletedTask; };
        await PlanningOperations.ResolveAsync(state, runtime, Ct); Assert.NotNull(captured);
        var requests = runtime.Requests.Select(r => r.ClientRequestId).ToArray();
        var restarted = Runtime(captured!, (scope, _) => Actions(Action(scope, scope.Clause.Start == 0 ? "external_read" : "local_processing")));
        await PlanningOperations.ResolveAsync(captured!, restarted, Ct);
        Assert.Equal(state.OperationAdmissionFingerprint, captured!.OperationAdmissionFingerprint);
        Assert.All(restarted.Requests, r => Assert.Contains(r.ClientRequestId, requests));
        Assert.DoesNotContain(restarted.Requests, r => r.ClientRequestId == requests[0]);
        var calls = restarted.Requests.Count; var events = captured.Events.Count;
        await PlanningOperations.ResolveAsync(captured, restarted, Ct); Assert.Equal(calls, restarted.Requests.Count); Assert.Equal(events, captured.Events.Count);
        Assert.Empty(captured.RepairAllowances);
    }

    [Fact]
    public async Task GoverningRevisionCanRetireARecognizedActionWithoutResurrectingIt()
    {
        var state = State("Read X."); state.BehaviorRevision = new() { Text = "Remove that read operation." };
        var runtime = Runtime(state, (scope, _) => Actions(Action(scope, "external_read"))); var respond = runtime.OnCall!;
        runtime.OnCall = (phase, request, ct) => phase == "behavior_revision_obligations"
            ? Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["enum"]![1]!.DeepClone()))) }) : respond(phase, request, ct);
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation); PlanningOperations.RequireCurrent(state);
        var calls = runtime.Requests.Count; await PlanningOperations.ResolveAsync(state, runtime, Ct); Assert.Equal(calls, runtime.Requests.Count);
    }
}
