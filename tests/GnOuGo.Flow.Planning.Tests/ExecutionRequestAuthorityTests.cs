using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Semantic answers are explicitly synthetic. Domain and authority enforcement use production builders.</summary>
public sealed class ExecutionRequestAuthorityTests
{
    private static PlanningSnapshot State(string text)
    {
        var state = OperationAdmissionTests.State(text);
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        foreach (var scope in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request"))
            PlanningFixtures.Runtime(state, scope.Clause, independentBoundary: false);
        return state;
    }

    [Fact]
    public void ThreeClausesHaveOneRequestCohortAndPropertiesCannotCreateRequests()
    {
        var state = State("Transform the supplied value. Select its result according to the rules. This is deterministic processing.");
        var scopes = PlanningOperations.Scopes(state); var description = scopes.Last();
        var cohort = Assert.Single(PlanningOperations.RequestCohorts(state));
        Assert.Equal(3, cohort.Scopes.Length);
        Assert.Single(PlanningOperations.ExecutionRequestDecisions(state));
        Assert.Equal(1, PlanningDecisionPages.PackedPageCount(state, [cohort.Decision]));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ContributionDecisions(state));
        var joint = OperationEffectFixtures.QualificationAnswers(state, s => OperationEffectFixtures.ContributionAnswer(state, s,
            s.Clause.Id == description.Clause.Id ? OperationEffectFixtures.Defer(s) : OperationEffectFixtures.Answer(state, s)));
        var request = OperationEffectFixtures.RequestAnswer(state, cohort, joint);
        Assert.Empty(PlanningContractValidation.ValidateInstance(request, cohort.Decision.Schema));
        OperationEffectFixtures.SeedPages(state, [cohort.Decision], new() { [cohort.Id] = request });
        var proof = Assert.Single(PlanningOperations.ReadExecutionRequests(state));
        Assert.Equal(1, proof.Version);
        Assert.Equal(2, proof.Units.Count(u => u.Role == "requested_execution"));
        Assert.Contains(proof.Units, u => u.ScopeReference == description.Clause.Id && u.Role == "not_requested");
        Assert.All(PlanningOperations.ContributionDecisions(state), d => Assert.DoesNotContain("requested_execution", d.Schema.ToJsonString()));
        var property = OperationEffectFixtures.PropertyAnswer(state, description, joint[PlanningOperations.ContributionDecisionId(description)]!.AsObject());
        var qualification = PlanningOperations.ParseContributions(state, description, property);
        Assert.DoesNotContain(qualification.Contributions, c => c.Role == "supports");
        Assert.Single(qualification.Contributions, c => c.Role == "governing_property");
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, description,
            OperationEffectFixtures.ContributionAnswer(state, description)));
    }

    [Fact]
    public async Task SupportIsProjectedOnceAndRestartRequiresNoDispatchOrWrites()
    {
        var state = State("Transform the value. Select the outcome using the rules. The transformation is deterministic.");
        var description = PlanningOperations.Scopes(state).Last().Evidence!.Id;
        OperationEffectFixtures.Seed(state, s => s.Evidence!.Id == description ? OperationEffectFixtures.Defer(s) : OperationEffectFixtures.Answer(state, s));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("Provider forbidden") };
        await PlanningOperations.ResolveAsync(state, runtime, TestContext.Current.CancellationToken);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        var admission = operation.OperationAdmission!;
        Assert.Equal(17, admission.Version); Assert.Single(admission.ExecutionRequests);
        var supports = admission.ExecutionContributions.SelectMany(p => p.Contributions).Where(c => c.Role == "supports").ToArray();
        Assert.Equal(2, supports.Length);
        Assert.All(supports, c => { Assert.Equal(PlanningContributionOrigin.DeterministicRequestProjection, c.Origin); Assert.Equal(admission.ExecutionRequests.Single().Id, c.ExecutionRequestId); Assert.Equal(operation.Id, c.EffectId); });
        Assert.DoesNotContain(admission.Assignments.Where(a => a.Disposition == "supports"), a => a.RuntimeEvidenceIds.Contains(description));
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var restored = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSnapshot)!;
        runtime.OnCheckpoint = _ => throw new InvalidOperationException("Write forbidden");
        await PlanningOperations.ResolveAsync(restored, runtime, TestContext.Current.CancellationToken);
        Assert.Empty(runtime.Requests); Assert.Equal(saved, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSnapshot));
    }

    [Theory]
    [InlineData("predicate")]
    [InlineData("effect")]
    [InlineData("basis")]
    public void RequestSchemaRejectsForeignOrModelOwnedLockedFacts(string defect)
    {
        var state = State("Transform the value."); var cohort = Assert.Single(PlanningOperations.RequestCohorts(state));
        var answer = OperationEffectFixtures.RequestAnswer(state, cohort,
            OperationEffectFixtures.QualificationAnswers(state, s => OperationEffectFixtures.ContributionAnswer(state, s)));
        var unit = answer[cohort.Scopes.Single().Clause.Id]!["u"]![0]!["q"]!;
        unit[defect == "predicate" ? "p" : defect == "effect" ? "t" : "basis"] = "foreign";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, cohort.Decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseExecutionRequest(state, cohort, answer));
    }

    [Fact]
    public void NestedPropertyDomainIsCoupledToAlreadyFixedRequest()
    {
        var state = State("Create reusable behavior transforming the value."); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var joint = OperationEffectFixtures.ContributionAnswer(state, scope);
        joint["units"]![0]!["request"]!["predicate"] = new JsonObject { ["start"] = "b3", ["end"] = "b6" };
        joint["units"]![0]!["request"]!["evidence"] = new JsonArray((JsonNode)new JsonObject { ["start"] = "b3", ["end"] = "b6" });
        var cohort = Assert.Single(PlanningOperations.RequestCohorts(state));
        var request = OperationEffectFixtures.RequestAnswer(state, cohort, new() { [PlanningOperations.ContributionDecisionId(scope)] = joint });
        OperationEffectFixtures.SeedPages(state, [cohort.Decision], new() { [cohort.Id] = request });
        var parent = Assert.Single(PlanningOperations.ReadExecutionRequests(state).SelectMany(p => p.Units), u => u.Role == "requested_execution");
        var answer = new JsonObject { ["status"] = "qualified", ["units"] = new JsonArray((JsonNode)new JsonObject
            { ["role"] = "governing_property", ["parent"] = parent.Id, ["governingKind"] = "descriptive_property",
                ["evidence"] = new JsonObject { ["start"] = "b0", ["end"] = "b3" } }) };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.ContributionDecision(state, scope).Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
    }

    [Fact]
    public void RequestProofIsOrderIndependentAndChangedEvidenceCannotReuseIt()
    {
        var state = State("Transform the supplied value. Select the result.");
        OperationEffectFixtures.SeedDefaultRequests(state);
        var proof = Assert.Single(PlanningOperations.ReadExecutionRequests(state));
        var saved = PlanningContext.Clone(state); saved.RuntimeEvidence.Reverse(); saved.References.Reverse();
        Assert.Equal(proof.ProofFingerprint, Assert.Single(PlanningOperations.ReadExecutionRequests(saved)).ProofFingerprint);
        Assert.Equal(PlanningOperations.ExecutionRequestDecisions(state).Single().Schema.ToJsonString(), PlanningOperations.ExecutionRequestDecisions(saved).Single().Schema.ToJsonString());
        saved.Request.Prompt += " Additional work.";
        Assert.ThrowsAny<Exception>(() => PlanningOperations.ReadExecutionRequests(saved));
    }

    [Fact]
    public async Task PartialRequestCheckpointReusesCompletedPagesAndPreservesAccounting()
    {
        var state = OperationAdmissionTests.State(string.Join(" ", Enumerable.Range(1, 6)
            .Select(i => $"Independently perform transformation number {i}.")));
        foreach (var scope in PlanningOperations.SourceScopes(state)) PlanningFixtures.Runtime(state, scope.Clause);
        var decisions = PlanningOperations.ExecutionRequestDecisions(state);
        Assert.Equal(6, decisions.Length);
        Assert.True(PlanningDecisionPages.PackedPageCount(state, decisions) > 1);
        PlanningSnapshot? checkpoint = null;
        TypedPlannerTests.FakeRuntime Runtime(PlanningSnapshot current) => new()
        {
            OnCall = (_, request, _) => Task.FromResult(new LLMResponse
            { CompletionStatus = "completed", Json = OperationEffectFixtures.Response(current, request) })
        };
        var first = Runtime(state);
        first.OnCheckpoint = current =>
        {
            if (current.DecisionPages.Any(p => p.Status == "completed"))
            { checkpoint = PlanningContext.Clone(current); throw new OperationCanceledException(); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningDecisionPages.ResolveAsync(
            state, first, "intent_operations", "$plan", decisions, TestContext.Current.CancellationToken));
        Assert.NotNull(checkpoint);
        Assert.Null(checkpoint.OperationAdmissionFingerprint);
        var completed = checkpoint.DecisionPages.Where(p => p.Status == "completed").SelectMany(p => p.Decisions).ToHashSet();
        var accounting = checkpoint.RequestAccounting.Select(a => a.Id).ToArray();
        var resumed = Runtime(checkpoint);
        await PlanningDecisionPages.ResolveAsync(checkpoint, resumed, "intent_operations", "$plan",
            PlanningOperations.ExecutionRequestDecisions(checkpoint), TestContext.Current.CancellationToken);
        Assert.All(resumed.Requests, request => Assert.DoesNotContain(request.StructuredOutputSchema!["properties"]!.AsObject(),
            p => completed.Contains(p.Key)));
        // Packing reserves later pages before the first completion checkpoint.
        // Resume fills those entries; it must not count an existing reservation twice.
        Assert.All(accounting, id => Assert.Single(checkpoint.RequestAccounting, a => a.Id == id));
        Assert.Equal(first.Requests.Count + resumed.Requests.Count, checkpoint.RequestAccounting.Count);
        var proofs = PlanningOperations.ReadExecutionRequests(checkpoint);
        var repacked = PlanningContext.Clone(checkpoint);
        repacked.DecisionPages.Clear(); repacked.References.Reverse(); repacked.RuntimeEvidence.Reverse();
        var originalPages = PlanningDecisionPages.PackedPageCount(checkpoint, decisions);
        for (var limit = 500; limit < 12000; limit += 100)
        {
            repacked.Request.Generation.MaxInputTokensPerRequest = limit;
            try { if (PlanningDecisionPages.PackedPageCount(repacked, decisions) > originalPages) break; }
            catch (WorkflowRuntimeException error) when (error.Code == "DECISION_SIZE_UNSUPPORTED") { }
        }
        Assert.True(PlanningDecisionPages.PackedPageCount(repacked, decisions) > originalPages);
        var joint = OperationEffectFixtures.QualificationAnswers(repacked, s => OperationEffectFixtures.ContributionAnswer(repacked, s));
        var cohorts = PlanningOperations.RequestCohorts(repacked).Reverse().ToArray();
        OperationEffectFixtures.SeedPages(repacked, cohorts.Select(c => c.Decision).ToArray(), new JsonObject(cohorts.Select(c =>
            new KeyValuePair<string, JsonNode?>(c.Id, OperationEffectFixtures.RequestAnswer(repacked, c, joint)))));
        Assert.Equal(proofs.Select(p => p.ProofFingerprint), PlanningOperations.ReadExecutionRequests(repacked).Select(p => p.ProofFingerprint));
        Assert.Empty(checkpoint.RepairAllowances);
    }
}
