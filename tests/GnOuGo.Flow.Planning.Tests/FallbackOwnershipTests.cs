using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

// All linguistic answers below are explicit synthetic fixtures. Domain and proof
// assertions do not claim that arbitrary model answers are semantically correct.
public sealed class FallbackOwnershipTests
{
    private static PlanningSnapshot State(string text = "Choose the standard result otherwise.")
    {
        var state = OperationAdmissionTests.State(text);
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        return state;
    }
    private static PlanningOperations.Scope Action(PlanningSnapshot state, string end = "b4")
    {
        var clause = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        var reference = clause.Select("b0", end); state.References.Add(reference);
        PlanningFixtures.Runtime(state, reference, independentBoundary: false);
        return Assert.Single(PlanningOperations.Scopes(state));
    }
    private static JsonObject Property(PlanningOperations.Scope scope, string start, string end, string kind = "runtime_fallback") => new()
    {
        ["role"] = "governing_property", ["governingKind"] = kind, ["scope"] = scope.Clause.Id,
        ["evidence"] = new JsonObject { ["start"] = start, ["end"] = end }
    };
    private static JsonObject Answer(PlanningSnapshot state, PlanningOperations.Scope scope)
    {
        var answer = OperationEffectFixtures.ContributionAnswer(state, scope);
        answer["units"]!.AsArray().Add((JsonNode)Property(scope, "b4", "b5")); return answer;
    }
    private static TypedPlannerTests.FakeRuntime Rejecting() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected provider call") };

    [Fact]
    public async Task ExactSourceFallbackComposesWithoutWideningSupportOrBorrowingRuntimeAuthority()
    {
        var state = State(); var scope = Action(state); var original = scope.Evidence!;
        var decision = Assert.Single(OperationEffectFixtures.PropertyDecisions(state));
        var answer = Answer(state, scope);
        Assert.NotNull(OperationEffectFixtures.ParseQualification(state, scope, answer));
        var forged = answer.DeepClone(); forged["units"]![0]!["request"]!["evidence"] = new JsonArray(scope.Clause.Id);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(forged, decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => OperationEffectFixtures.ParseQualification(state, scope, forged.AsObject()));
        var proof = OperationEffectFixtures.ParseQualification(state, scope, answer);
        var support = Assert.Single(proof.Contributions, c => c.Role == "supports");
        var fallback = Assert.Single(proof.Contributions, c => c.Role == "governing_property");
        Assert.Equal(original.ActionReference, support.EvidenceReference);
        Assert.Equal("otherwise.", PlanningChoiceEvidence.Text(state, fallback.EvidenceReference));
        Assert.Empty(fallback.RuntimeEvidenceIds); Assert.Null(fallback.EffectId);
        Assert.Equal("runtime_fallback", fallback.GoverningKind);
        Assert.Null(Assert.Single(fallback.SourceBindings).SemanticObligationId);
        OperationEffectFixtures.Seed(state, qualification: _ => answer);
        Assert.Single(PlanningOperations.ApplicabilityDecisions(state)); // Singleton is still semantic.
        await PlanningOperations.ResolveAsync(state, Rejecting(), TestContext.Current.CancellationToken);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind); Assert.True(operation.Required);
        Assert.Equal(18, operation.OperationAdmission!.Version);
        var attached = Assert.Single(operation.OperationAdmission.Assignments, a => a.Disposition == "attach");
        Assert.Null(attached.RuntimeEvidenceId); Assert.Empty(attached.RuntimeEvidenceIds);
        Assert.Equal(PlanningOperationNecessity.Unspecified, attached.Necessity);
        var applicability = Assert.Single(operation.OperationAdmission.GoverningApplicability);
        Assert.Equal(2, applicability.Version); Assert.Equal(PlanningApplicabilityOrigin.ModelApplicability, applicability.Origin);
        Assert.Equal(operation.Id, Assert.Single(applicability.Targets));
        Assert.Empty(operation.OperationAdmission.Dependencies!.Assignments);
        Assert.Equal(original, state.RuntimeEvidence.Single(e => e.Id == original.Id));
        var json = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var restored = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanningSnapshot)!;
        var runtime = Rejecting(); runtime.OnCheckpoint = _ => throw new InvalidOperationException("Unexpected checkpoint write");
        await PlanningOperations.ResolveAsync(restored, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(json, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSnapshot));
    }

    [Fact]
    public void UnaccountedFallbackFailsBeforeCoverageAndCannotBecomeSupport()
    {
        var state = State(); var scope = Action(state);
        var answer = OperationEffectFixtures.ContributionAnswer(state, scope);
        var error = Assert.Throws<WorkflowRuntimeException>(() => OperationEffectFixtures.ParseQualification(state, scope, answer));
        Assert.Contains("issued governing domain", error.Message);
        answer["units"]![0]!["request"]!["evidence"] = new JsonArray((JsonNode)new JsonObject { ["start"] = "b4", ["end"] = "b5" });
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, OperationEffectFixtures.PropertyDecision(state, scope).Schema));
    }

    [Fact]
    public void FallbackWithoutRuntimeActionHasOnlyGoverningAuthorityAndNoRealization()
    {
        var state = State("Use the lower result on remaining cases.");
        PolicyGroundingTests.Add(state, "request", state.Request.Prompt, "fallback", "runtime_fallback");
        PlanningFixtures.EmptyRuntime(state); PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var scope = Assert.Single(PlanningOperations.ContributionScopes(state));
        Assert.Null(scope.Evidence);
        var decision = Assert.Single(OperationEffectFixtures.PropertyDecisions(state));
        Assert.DoesNotContain("requested_execution", decision.Schema.ToJsonString());
        OperationEffectFixtures.SeedContributions(state);
        var property = Assert.Single(PlanningOperations.ReadContributions(state).SelectMany(p => p.Contributions), c => c.Role == "governing_property");
        Assert.Equal("fallback", Assert.Single(property.SourceBindings).SemanticObligationId);
        Assert.Empty(PlanningOperations.CoverageGroups(state));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ApplicabilityDecisions(state));
    }

    [Fact]
    public void ExistingSemanticKindAndGroundingCannotBeDiscardedOrChanged()
    {
        var state = State(); var scope = Action(state);
        var owned = PolicyGroundingTests.Add(state, "request", "otherwise.", "fallback", "runtime_fallback");
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var answer = Answer(state, scope);
        var proof = OperationEffectFixtures.ParseQualification(state, scope, answer);
        var binding = Assert.Single(Assert.Single(proof.Contributions, c => c.GoverningKind == "runtime_fallback").SourceBindings);
        Assert.Equal(owned.Grounding!.Fingerprint, binding.GroundingFingerprint);
        var broadProperty = new JsonObject { ["status"] = "qualified", ["units"] = new JsonArray((JsonNode)
            Property(scope, "b0", "b5", "descriptive_property")) };
        Assert.Throws<WorkflowRuntimeException>(() => OperationEffectFixtures.ParseQualification(state, scope, broadProperty));
        answer["units"]![1]!["governingKind"] = "descriptive_property";
        Assert.Throws<WorkflowRuntimeException>(() => OperationEffectFixtures.ParseQualification(state, scope, answer));
        answer["units"]![1] = new JsonObject { ["role"] = "excluded", ["basis"] = "no_operation_relevance", ["scope"] = scope.Clause.Id,
            ["evidence"] = owned.EvidenceReferences[0] };
        Assert.Throws<WorkflowRuntimeException>(() => OperationEffectFixtures.ParseQualification(state, scope, answer));
        state.Obligations[state.Obligations.IndexOf(owned)] = owned with { Required = !owned.Required };
        Assert.Throws<WorkflowRuntimeException>(() => OperationEffectFixtures.PropertyDecision(state, scope));
    }

    [Fact]
    public void SourceAndEvidenceEnumerationPreserveAggregateProof()
    {
        var state = State(); var scope = Action(state); var answer = Answer(state, scope);
        var first = OperationEffectFixtures.ParseQualification(state, scope, answer);
        var restored = PlanningContext.Clone(state); restored.RuntimeEvidence.Reverse(); restored.References.Reverse(); restored.Obligations.Reverse();
        answer["units"] = new JsonArray(answer["units"]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray());
        var second = OperationEffectFixtures.ParseQualification(restored, Assert.Single(PlanningOperations.Scopes(restored)), answer);
        Assert.Equal(first.ProofFingerprint, second.ProofFingerprint);
        Assert.Equal(first.DomainFingerprint, second.DomainFingerprint);
    }

    [Fact]
    public async Task IndependentlyOwnedFallbackClauseJoinsAnEstablishedEffectWithoutCreatingAnother()
    {
        var state = State("Transform the value. Use a lower result for remaining cases.");
        var clauses = PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").OrderBy(s => s.Clause.Start).ToArray();
        PlanningFixtures.Runtime(state, clauses[0].Clause, independentBoundary: false);
        PolicyGroundingTests.Add(state, "request", "Use a lower result for remaining cases.", "fallback", "runtime_fallback");
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        Assert.Equal(2, OperationEffectFixtures.PropertyDecisions(state).Length);
        OperationEffectFixtures.Seed(state);
        await PlanningOperations.ResolveAsync(state, Rejecting(), TestContext.Current.CancellationToken);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Single(operation.OperationAdmission!.Assignments, a => a.Disposition == "supports");
        Assert.Single(operation.OperationAdmission.Assignments, a => a.Disposition == "attach");
    }

    [Fact]
    public async Task PartialQualificationRestartPreservesSourceOnlyApplicabilityAndProofs()
    {
        var state = State(); var scope = Action(state); var answer = Answer(state, scope);
        OperationEffectFixtures.SeedContributions(state, qualification: _ => answer);
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var restored = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSnapshot)!;
        var qualificationPages = restored.DecisionPages.Select(p => p.Id).ToArray();
        foreach (var snapshot in new[] { state, restored })
        {
            OperationEffectFixtures.Seed(snapshot, qualification: _ => answer.DeepClone().AsObject());
            await PlanningOperations.ResolveAsync(snapshot, Rejecting(), TestContext.Current.CancellationToken);
        }
        Assert.All(qualificationPages, id => Assert.Single(restored.DecisionPages, p => p.Id == id));
        Assert.Equal(state.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
        Assert.Equal(PlanningOperations.ReadContributions(state).Select(p => p.ProofFingerprint), PlanningOperations.ReadContributions(restored).Select(p => p.ProofFingerprint));
        Assert.Equal(PlanningOperations.ReadApplicability(state).Select(p => p.ProofFingerprint), PlanningOperations.ReadApplicability(restored).Select(p => p.ProofFingerprint));
        var operation = Assert.Single(restored.Obligations, PlanningSourceDecisions.IsOperation);
        var proof = operation.OperationAdmission!;
        var property = proof.ExecutionContributions.SelectMany(p => p.Contributions).Single(c => c.GoverningKind == "runtime_fallback");
        property.SourceBindings[0] = property.SourceBindings[0] with { ScopeReference = "foreign" };
        restored.Obligations[restored.Obligations.IndexOf(operation)] = PlanningOperations.Prove(restored, operation, proof);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(restored));
    }

    [Fact]
    public async Task RequiredSemanticObligationDoesNotOverrideOptionalExecutionNecessity()
    {
        var state = State(); var scope = Action(state); var evidence = scope.Evidence!;
        state.RuntimeEvidence.Remove(evidence);
        state.RuntimeEvidence.Add(PlanningOperations.SealRuntime(state, evidence with
            { Necessity = PlanningOperationNecessity.Optional, NecessityReference = evidence.ActionReference }));
        PlanningFixtures.EmptyRuntime(state);
        PolicyGroundingTests.Add(state, "request", "otherwise.", "fallback", "runtime_fallback");
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        scope = Assert.Single(PlanningOperations.Scopes(state));
        OperationEffectFixtures.Seed(state, qualification: _ => Answer(state, scope));
        await PlanningOperations.ResolveAsync(state, Rejecting(), TestContext.Current.CancellationToken);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.False(operation.Required);
        Assert.Equal(PlanningOperationNecessity.Unspecified, Assert.Single(operation.OperationAdmission!.Assignments, a => a.Disposition == "attach").Necessity);
    }
}
