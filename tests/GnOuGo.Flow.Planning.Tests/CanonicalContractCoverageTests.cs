using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.DeclarationGroundingTests;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic owned evidence tests; no historical answer is reinterpreted.</summary>
public sealed class CanonicalContractCoverageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime RejectCalls() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected model dispatch") };
    private static PlanningReference Span(PlanningSnapshot state, string text)
    {
        var id = "evidence_" + state.References.Count;
        var obligation = Add(state, text, id, "implementation_policy");
        var reference = state.References.Single(r => r.Id == obligation.EvidenceReferences.Single());
        state.Obligations.Remove(obligation);
        return reference;
    }
    private static PlanningSnapshot Contract(string text, string contract)
    {
        var state = State("Required output result. " + text);
        state.OperationAdmissionFingerprint = null;
        Add(state, "Required output result.", "result"); Add(state, contract, "contract", "declaration_constraint");
        PlanningDeclarations.Commit(state, Canonicalize(state,
            [Distinct(state, "result", "result", direction: "output"), Link("contract", "result", "modifier_of")]), PlanningDeclarations.EvidenceFingerprint(state));
        return state;
    }

    [Theory]
    [InlineData("local_processing")]
    [InlineData("external_read")]
    [InlineData("external_write")]
    [InlineData("external_execute")]
    [InlineData("human_interaction")]
    [InlineData("resource_lifecycle")]
    [InlineData("cleanup")]
    public async Task CanonicalContractCoverageExcludesEveryPreliminaryExecutionKind(string kind)
    {
        const string contract = "Keep the original members unchanged.";
        var state = Contract("Transform the value. " + contract, contract);
        var action = PlanningFixtures.Runtime(state, Span(state, "Transform the value."), independentBoundary: false);
        var covered = PlanningFixtures.Runtime(state, Span(state, contract), kind,
            resourceAction: kind == "resource_lifecycle" ? "create" : kind == "cleanup" ? "delete" : null);
        var retained = JsonSerializer.Serialize(state.RuntimeEvidence, PlanningJsonContext.Default.ListPlanningRuntimeEvidence);
        Assert.Equal(PlanningOperations.ContributionDecisionId(action), Assert.Single(PlanningOperations.ContributionDecisions(state)).Id);
        Assert.Contains(covered.Id, PlanningOperations.DeclarationExclusions(state).Keys);
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.EffectDomain(state, covered));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.OccurrenceDecision(state, covered));
        var forged = PlanningOperations.SourceScopes(state).Single(s => s.Clause.Id == covered.ClauseReference) with { Evidence = covered };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ContributionDecision(state, forged));
        OperationEffectFixtures.Seed(state); await PlanningOperations.ResolveAsync(state, RejectCalls(), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind);
        Assert.DoesNotContain(operation.OperationAdmission!.Assignments, a => a.RuntimeEvidenceId == covered.Id);
        var exclusion = Assert.Single(PlanningOperations.ReadContributions(state), p => p.RuntimeEvidenceId == covered.Id);
        Assert.Null(exclusion.DecisionId); Assert.Equal(3, exclusion.Version);
        Assert.Equal(PlanningContributionOrigin.DeterministicExclusion, Assert.Single(exclusion.Contributions).Origin);
        Assert.Equal(retained, JsonSerializer.Serialize(state.RuntimeEvidence, PlanningJsonContext.Default.ListPlanningRuntimeEvidence));
        var clone = PlanningContext.Clone(state); var fingerprint = JsonSerializer.Serialize(clone, PlanningJsonContext.Default.PlanningSnapshot);
        await PlanningOperations.ResolveAsync(clone, RejectCalls(), Ct);
        Assert.Equal(fingerprint, JsonSerializer.Serialize(clone, PlanningJsonContext.Default.PlanningSnapshot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactOwnedSeparationPreservesIndependentRuntimeAction(bool reverse)
    {
        const string contract = "Keep originals unchanged";
        const string action = "transform another value.";
        var state = Contract(contract + " " + action, contract);
        var parent = PlanningFixtures.Runtime(state, Span(state, contract + " " + action), independentBoundary: false);
        var child = PlanningFixtures.Runtime(state, Span(state, action), independentBoundary: false);
        if (reverse) { state.RuntimeEvidence.Reverse(); state.DeclarationAssignments.Reverse(); }
        var decision = Assert.Single(PlanningOperations.ContributionDecisions(state));
        Assert.Equal(PlanningOperations.ContributionDecisionId(child), decision.Id);
        Assert.DoesNotContain(parent.ActionReference!, decision.Schema.ToJsonString());
        var domains = decision.Schema.ToJsonString();
        var original = PlanningContext.Clone(state); original.RuntimeEvidence.Reverse(); original.DeclarationAssignments.Reverse();
        Assert.Equal(domains, Assert.Single(PlanningOperations.ContributionDecisions(original)).Schema.ToJsonString());
        Assert.Equal(decision.EvidenceFingerprint, Assert.Single(PlanningOperations.ContributionDecisions(original)).EvidenceFingerprint);
        OperationEffectFixtures.Seed(state); OperationEffectFixtures.Seed(original);
        await PlanningOperations.ResolveAsync(state, RejectCalls(), Ct); await PlanningOperations.ResolveAsync(original, RejectCalls(), Ct);
        Assert.Equal(state.OperationAdmissionFingerprint, original.OperationAdmissionFingerprint);
        Assert.Equal("canonical_contract_separation", Assert.Single(PlanningOperations.ReadContributions(state).Single(p => p.RuntimeEvidenceId == parent.Id).Contributions).Basis);
        Assert.Equal(child.Id, Assert.Single(Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).OperationAdmission!.Assignments).RuntimeEvidenceId);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("incomplete")]
    [InlineData("kind")]
    [InlineData("necessity")]
    [InlineData("boundary")]
    public void PartialCoverageRequiresCompleteIndependentOwnedEvidence(string defect)
    {
        var state = Contract("Keep originals unchanged transform another value.", "Keep originals unchanged");
        var parent = PlanningFixtures.Runtime(state, Span(state, "Keep originals unchanged transform another value."), independentBoundary: defect == "boundary");
        if (defect != "missing") PlanningFixtures.Runtime(state, Span(state, defect == "incomplete" ? "another value." : "transform another value."),
            kind: defect == "kind" ? "external_read" : "local_processing", required: defect != "necessity", independentBoundary: false);
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ContributionDecisions(state));
        Assert.Contains(parent.Id, error.Details!["location"]!.ToString());
        Assert.Empty(state.RequestAccounting); Assert.Empty(state.DecisionPages);
    }

    [Theory]
    [InlineData("partial_word")]
    [InlineData("foreign_target")]
    [InlineData("stale_source")]
    [InlineData("stale_declaration")]
    public void CoveredSubspansAndStaleCoverageCannotAcquireSupport(string defect)
    {
        var state = Contract("Keep the payload unchanged. Fetch the source.", "Keep the payload unchanged.");
        var covered = PlanningFixtures.Runtime(state, Span(state, "Keep"), "external_read", independentBoundary: false);
        var action = PlanningFixtures.Runtime(state, Span(state, "Fetch the source."), "external_read");
        if (defect == "foreign_target") state.DeclarationAssignments[0] = state.DeclarationAssignments[0] with { TargetId = "foreign" };
        if (defect == "stale_source") state.Request.Prompt += " Changed contract.";
        if (defect == "stale_declaration") state.Declarations[0].ModifierReferences.Clear();
        if (defect != "partial_word") { Assert.ThrowsAny<Exception>(() => PlanningOperations.DeclarationExclusions(state)); return; }
        Assert.Contains(covered.Id, PlanningOperations.DeclarationExclusions(state).Keys);
        OperationEffectFixtures.SeedBoundaries(state);
        var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = OperationEffectFixtures.ContributionAnswer(state, scope, OperationEffectFixtures.Answer(state, scope));
        answer["contributions"]![0]!["evidence"] = covered.ActionReference;
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.ContributionDecision(state, scope).Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
        Assert.Equal(action.Id, scope.Evidence!.Id);
    }

    [Fact]
    public async Task OmissionDefaultAndAliasAreContractsEvenWithExternalHints()
    {
        var state = State("Optional input limit defaults to 100 when omitted. Use limit as supplied. Fetch the source.");
        state.OperationAdmissionFingerprint = null;
        Add(state, "Optional input limit defaults to 100 when omitted.", "limit");
        Add(state, "defaults to 100 when omitted.", "default", "omission_default");
        Add(state, "Use limit as supplied.", "alias");
        PlanningDeclarations.Commit(state, Canonicalize(state, [Distinct(state, "limit", "limit", "optional"),
            Link("default", "limit", "modifier_of", "optional", Token(state, "default", "100")), Link("alias", "limit")]), PlanningDeclarations.EvidenceFingerprint(state));
        var defaults = PlanningFixtures.Runtime(state, Span(state, "defaults to 100 when omitted."), "external_read");
        var alias = PlanningFixtures.Runtime(state, Span(state, "Use limit as supplied."), "external_read");
        var fetch = PlanningFixtures.Runtime(state, Span(state, "Fetch the source."), "external_read");
        Assert.Equal(PlanningOperations.ContributionDecisionId(fetch), Assert.Single(PlanningOperations.ContributionDecisions(state)).Id);
        Assert.Equal(new[] { defaults.Id, alias.Id }.Order(), PlanningOperations.DeclarationExclusions(state).Keys.Order());
        OperationEffectFixtures.Seed(state); await PlanningOperations.ResolveAsync(state, RejectCalls(), Ct);
        Assert.Equal("external_read", Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).Kind);
        Assert.Equal(100m, PlanningDeclarations.Default(state, Assert.Single(state.Declarations))!.Number);
    }

    [Fact]
    public async Task RestartAfterQualificationPreservesContractExclusionsAndReceiptAccounting()
    {
        var state = Contract("Transform the value. Keep originals unchanged.", "Keep originals unchanged.");
        PlanningFixtures.Runtime(state, Span(state, "Transform the value."), independentBoundary: false);
        var covered = PlanningFixtures.Runtime(state, Span(state, "Keep originals unchanged."), "external_read");
        PlanningSnapshot? checkpoint = null;
        var first = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { Json = OperationEffectFixtures.Response(state, request), CompletionStatus = "completed" }) };
        first.OnCheckpoint = s =>
        {
            if (s.DecisionPages.Any(p => p.Status == "completed")) { checkpoint = PlanningContext.Clone(s); throw new OperationCanceledException(); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, first, Ct));
        Assert.NotNull(checkpoint); Assert.Null(checkpoint.OperationAdmissionFingerprint);
        Assert.DoesNotContain(checkpoint.Obligations, o => o.OperationAdmission is not null);
        var completed = checkpoint.DecisionPages.Where(p => p.Status == "completed").Select(p => p.Id).ToArray();
        Assert.Contains(covered.Id, PlanningOperations.DeclarationExclusions(checkpoint).Keys);
        var second = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { Json = OperationEffectFixtures.Response(checkpoint, request), CompletionStatus = "completed" }) };
        await PlanningOperations.ResolveAsync(checkpoint, second, Ct);
        Assert.All(completed, id => Assert.Single(checkpoint.DecisionPages, p => p.Id == id && p.Status == "completed"));
        Assert.Equal(first.Requests.Count + second.Requests.Count, checkpoint.RequestAccounting.Count);
        var restored = PlanningContext.Clone(checkpoint); var reject = RejectCalls();
        reject.OnCheckpoint = _ => throw new InvalidOperationException("Completed re-entry wrote a checkpoint");
        await PlanningOperations.ResolveAsync(restored, reject, Ct);
        Assert.Equal(checkpoint.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
        Assert.Equal(checkpoint.RequestAccounting.Count, restored.RequestAccounting.Count);
    }

    [Fact]
    public async Task ChangedEligibilityInvalidatesHistoricalProofWithoutChangingIdentityOrAccounting()
    {
        var state = Contract("Transform the value. Keep originals.", "Keep originals.");
        PlanningFixtures.Runtime(state, Span(state, "Transform the value."), independentBoundary: false);
        OperationEffectFixtures.Seed(state); await PlanningOperations.ResolveAsync(state, RejectCalls(), Ct);
        var current = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal(13, current.OperationAdmission!.Version);
        var clone = PlanningContext.Clone(state); var index = clone.Obligations.FindIndex(o => o.Id == current.Id);
        clone.Obligations[index] = clone.Obligations[index] with { OperationAdmission = clone.Obligations[index].OperationAdmission! with { Version = 12 } };
        var accounting = JsonSerializer.Serialize(clone.RequestAccounting);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(clone, RejectCalls(), Ct));
        Assert.Equal("INTENT_OPERATION_PROOF_MISSING", error.Code);
        Assert.Equal(current.Id, clone.Obligations[index].Id); Assert.Equal(accounting, JsonSerializer.Serialize(clone.RequestAccounting));
    }
}
