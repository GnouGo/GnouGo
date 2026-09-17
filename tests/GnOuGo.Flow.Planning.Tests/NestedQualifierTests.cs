using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

// Explicit synthetic semantic answers. These tests prove composition/authority,
// not that a model will always correctly recognize a request or property.
public sealed class NestedQualifierTests
{
    private static PlanningSnapshot State(string text = "Read the supplied value once.", bool result = false)
    {
        var state = OperationAdmissionTests.State(text);
        if (result)
        {
            state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
            PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        }
        var clause = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request").Clause;
        PlanningFixtures.Runtime(state, clause, result ? "local_processing" : "external_read", independentBoundary: !result);
        OperationEffectFixtures.SeedBoundaries(state);
        return state;
    }
    private static JsonObject Range(int start, int end) => new() { ["start"] = "b" + start, ["end"] = "b" + end };
    private static JsonObject Answer(PlanningSnapshot state, PlanningOperations.Scope scope, params (int Start, int End)[] ranges)
    {
        var answer = OperationEffectFixtures.ContributionAnswer(state, scope);
        answer["units"]![0]!["qualifiers"] = new JsonArray(ranges.Select(r => (JsonNode)new JsonObject { ["evidence"] = Range(r.Start, r.End), ["governingKind"] = "descriptive_property" }).ToArray());
        return answer;
    }
    private static TypedPlannerTests.FakeRuntime NoCalls() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected dispatch") };

    [Fact]
    public async Task NestedQualifierHasNoSupportAndPreservesOwnedReadAcrossRestart()
    {
        var state = State(); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope, (4, 5));
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.ContributionDecision(state, scope).Schema));
        var parsed = PlanningOperations.ParseContributions(state, scope, answer);
        var parent = Assert.Single(parsed.Units, u => u.Role == "requested_execution");
        var child = Assert.Single(parsed.Units, u => u.Role == "governing_property");
        Assert.Equal(parent.Id, child.ParentRequestUnitId); Assert.Null(parent.ParentRequestUnitId);
        Assert.Null(child.EffectId); Assert.Null(child.PredicateReference); Assert.Null(child.OwnerReference);
        Assert.Single(parsed.Contributions, c => c.Role == "supports");
        Assert.Equal("once.", PlanningChoiceEvidence.Text(state, child.EvidenceReferences.Single()));
        Assert.Equal(state.Request.Prompt, PlanningChoiceEvidence.Text(state, parent.EvidenceReferences.Single()));
        OperationEffectFixtures.Seed(state, qualification: _ => answer);
        Assert.Empty(PlanningOperations.ApplicabilityDecisions(state)); // Exact external occurrence owner, not nesting.
        await PlanningOperations.ResolveAsync(state, NoCalls(), TestContext.Current.CancellationToken);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.True(operation.Required); Assert.Equal(16, operation.OperationAdmission!.Version);
        Assert.Single(operation.OperationAdmission.Assignments, a => a.Disposition == "supports");
        Assert.Single(operation.OperationAdmission.Assignments, a => a.Disposition == "attach");
        Assert.Equal(PlanningApplicabilityOrigin.DeterministicOwner, Assert.Single(operation.OperationAdmission.GoverningApplicability).Origin);
        Assert.Empty(operation.OperationAdmission.Dependencies!.Assignments);
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var restored = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSnapshot)!;
        var runtime = NoCalls(); runtime.OnCheckpoint = _ => throw new InvalidOperationException("Unexpected write");
        await PlanningOperations.ResolveAsync(restored, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(saved, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSnapshot));
    }

    [Fact]
    public void NestedCompositionAndSingletonCardinalityDoNotProveLocalApplicability()
    {
        var state = State("Transform the supplied value deterministically.", result: true);
        OperationEffectFixtures.Seed(state, qualification: members => Answer(state, members.Single(), (4, 5)));
        var decision = Assert.Single(PlanningOperations.ApplicabilityDecisions(state));
        Assert.Single(decision.Context["realized"]!.AsObject());
        Assert.Equal(PlanningApplicabilityOrigin.ModelApplicability, Assert.Single(PlanningOperations.ReadApplicability(state)).Origin);
        var support = Assert.Single(PlanningOperations.CoverageGroups(state)).Scopes;
        Assert.Single(support); Assert.All(support, s => Assert.Equal("supports", s.Contribution!.Role));
    }

    [Fact]
    public async Task WrongKindPropertyRecordRetainsProvenanceWithoutGrantingExecution()
    {
        var state = State("Transform the supplied value deterministically.", result: true);
        var parent = Assert.Single(PlanningOperations.Scopes(state));
        var property = parent.Select("b4", "b5"); state.References.Add(property);
        var hint = PlanningFixtures.Runtime(state, property, "external_execute", independentBoundary: false);
        var answer = Answer(state, parent, (4, 5));
        OperationEffectFixtures.Seed(state, s => s.Evidence!.Id == hint.Id ? OperationEffectFixtures.Defer(s) : OperationEffectFixtures.Answer(state, s), qualification: _ => answer);
        await PlanningOperations.ResolveAsync(state, NoCalls(), TestContext.Current.CancellationToken);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind);
        var proof = PlanningOperations.ReadContributions(state).Single(p => p.DecisionId is not null);
        Assert.DoesNotContain(hint.Id, Assert.Single(proof.Contributions, c => c.Role == "supports").RuntimeEvidenceIds);
        Assert.Contains(hint.Id, Assert.Single(proof.Contributions, c => c.Role == "governing_property").RuntimeEvidenceIds);
        Assert.Single(PlanningOperations.ApplicabilityDecisions(state));
    }

    [Theory]
    [InlineData("effect")]
    [InlineData("basis")]
    [InlineData("kind")]
    [InlineData("necessity")]
    [InlineData("parentRequestUnitId")]
    [InlineData("qualifiers")]
    public void QualifierSchemaOffersNoExecutionAuthorityOrModelOwnedParent(string field)
    {
        var state = State(); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope, (4, 5));
        answer["units"]![0]!["qualifiers"]![0]![field] = "foreign";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.ContributionDecision(state, scope).Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("outside")]
    [InlineData("crossing")]
    [InlineData("predicate_exhausted")]
    [InlineData("execution_exhausted")]
    [InlineData("crossing_siblings")]
    [InlineData("foreign")]
    [InlineData("peer")]
    [InlineData("excluded")]
    [InlineData("missing_predicate")]
    [InlineData("capacity")]
    public void MissingOrContradictoryCompositionFailsClosed(string defect)
    {
        var state = State(); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope, (4, 5)); var parent = answer["units"]![0]!;
        if (defect == "equal") parent["qualifiers"]![0]!["evidence"] = scope.Evidence!.ActionReference;
        if (defect is "outside" or "crossing") parent["request"]!["evidence"] = new JsonArray((JsonNode)Range(0, defect == "outside" ? 3 : 4));
        if (defect is "outside" or "crossing") parent["request"]!["predicate"] = Range(0, 1);
        if (defect == "crossing") parent["qualifiers"]![0]!["evidence"] = Range(3, 5);
        if (defect == "predicate_exhausted") parent["request"]!["predicate"] = Range(4, 5);
        if (defect == "execution_exhausted") parent["qualifiers"] = new JsonArray((JsonNode)new JsonObject { ["evidence"] = Range(0, 3), ["governingKind"] = "descriptive_property" }, new JsonObject { ["evidence"] = Range(3, 5), ["governingKind"] = "descriptive_property" });
        if (defect == "crossing_siblings") parent["qualifiers"] = new JsonArray((JsonNode)new JsonObject { ["evidence"] = Range(1, 4), ["governingKind"] = "descriptive_property" }, new JsonObject { ["evidence"] = Range(3, 5), ["governingKind"] = "descriptive_property" });
        if (defect == "foreign") parent["qualifiers"]![0]!["evidence"] = "foreign";
        if (defect is "peer" or "excluded")
        {
            parent["qualifiers"] = new JsonArray();
            var peer = new JsonObject { ["role"] = defect == "peer" ? "governing_property" : "excluded", ["scope"] = scope.Clause.Id, ["evidence"] = Range(4, 5) };
            if (defect == "peer") peer["governingKind"] = "descriptive_property";
            if (defect == "excluded") peer["basis"] = "no_operation_relevance";
            answer["units"]!.AsArray().Add(peer);
        }
        if (defect == "missing_predicate") parent["request"]!.AsObject().Remove("predicate");
        if (defect == "capacity")
        {
            parent["qualifiers"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode)new JsonObject { ["evidence"] = Range(4, 5), ["governingKind"] = "descriptive_property" }).ToArray());
            answer["units"]!.AsArray().Add(new JsonObject { ["role"] = "governing_property", ["governingKind"] = "descriptive_property", ["scope"] = scope.Clause.Id, ["evidence"] = Range(4, 5) });
        }
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
    }

    [Fact]
    public void ChildOrderAndEquivalentNotationPreserveParentAndProofIdentity()
    {
        var state = State("Read the supplied value once sequentially."); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var plain = PlanningOperations.ParseContributions(state, scope, Answer(state, scope));
        var answer = Answer(state, scope, (4, 5), (5, 6));
        var proof = PlanningOperations.ParseContributions(state, scope, answer);
        Assert.Equal(Assert.Single(plain.Units).Id, Assert.Single(proof.Units, u => u.Role == "requested_execution").Id);
        answer["units"]![0]!["qualifiers"] = new JsonArray(answer["units"]![0]!["qualifiers"]!.AsArray().Reverse().Select(n => n!.DeepClone()).ToArray());
        answer["units"]![0]!["request"]!["evidence"]!.AsArray().Add(Range(0, 6)); // Exact duplicate notation is idempotent.
        state.RuntimeEvidence.Reverse(); state.References.Reverse();
        Assert.Equal(proof.ProofFingerprint, PlanningOperations.ParseContributions(state, scope, answer).ProofFingerprint);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("cycle")]
    [InlineData("promoted")]
    [InlineData("historical")]
    public async Task RestoredCompositionCannotBeForgedOrPromoted(string defect)
    {
        var state = State(); OperationEffectFixtures.Seed(state, qualification: members => Answer(state, members.Single(), (4, 5)));
        await PlanningOperations.ResolveAsync(state, NoCalls(), TestContext.Current.CancellationToken);
        var admission = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation).OperationAdmission!;
        var proof = admission.ExecutionContributions.Single(p => p.DecisionId is not null);
        var child = proof.Units.Single(u => u.ParentRequestUnitId is not null);
        proof.Units[proof.Units.IndexOf(child)] = child with
        { ParentRequestUnitId = defect == "missing" ? null : defect == "cycle" ? child.Id : "foreign", Role = defect == "promoted" ? "requested_execution" : child.Role };
        var changed = defect == "historical" ? proof with { Version = 4 } : proof;
        admission.ExecutionContributions[admission.ExecutionContributions.IndexOf(proof)] = changed with
        { ProofFingerprint = PlanningGraphCompiler.Fingerprint(JsonSerializer.Serialize(changed with { ProofFingerprint = "" }, PlanningJsonContext.Default.PlanningExecutionContributionProof)) };
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        var resigned = PlanningOperations.Prove(state, operation, admission);
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.Validate(state, resigned));
        Assert.Equal("Canonical contribution qualification is stale or incomplete.", error.Message);
    }

    [Fact]
    public async Task IndependentRequestsKeepSeparateOccurrencesAndExactQualifierProvenance()
    {
        var state = OperationAdmissionTests.State("Read the first value once then read the second value once.");
        var clause = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        foreach (var (start, end) in new[] { (0, 5), (5, 11) })
        { var reference = clause.Select("b" + start, "b" + end); state.References.Add(reference); PlanningFixtures.Runtime(state, reference, "external_read"); }
        OperationEffectFixtures.SeedBoundaries(state);
        var scopes = PlanningOperations.Scopes(state).OrderBy(s => state.References.Single(r => r.Id == s.Evidence!.ActionReference).Start).ToArray();
        var answer = OperationEffectFixtures.CombineQualifications([Answer(state, scopes[0], (4, 5)), Answer(state, scopes[1], (10, 11))]);
        OperationEffectFixtures.Seed(state, qualification: _ => answer);
        // Restart after qualification/coverage pages, before admission.
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var restored = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSnapshot)!;
        await PlanningOperations.ResolveAsync(state, NoCalls(), TestContext.Current.CancellationToken);
        await PlanningOperations.ResolveAsync(restored, NoCalls(), TestContext.Current.CancellationToken);
        Assert.Equal(state.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
        var operations = state.Obligations.Where(PlanningSourceDecisions.IsOperation).ToArray(); Assert.Equal(2, operations.Length);
        Assert.All(operations, o => { Assert.Single(o.OperationAdmission!.Assignments, a => a.Disposition == "attach"); Assert.DoesNotContain(o.OperationAdmission.Dependencies!.Assignments, a => a.Disposition == "data"); });
        var proof = PlanningOperations.ReadContributions(state).Single(p => p.DecisionId is not null);
        foreach (var child in proof.Units.Where(u => u.ParentRequestUnitId is not null))
            Assert.Equal(proof.Units.Single(u => u.Id == child.ParentRequestUnitId).RuntimeEvidenceIds, child.RuntimeEvidenceIds);
    }
    [Fact]
    public async Task SharedPropertyRequiresApplicabilityAndDoesNotMergeIndependentClauseActions()
    {
        var state = OperationAdmissionTests.State("Read the first value then read the second value with both following the common policy.");
        var clause = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        foreach (var (start, end) in new[] { (0, 4), (4, 9) })
        { var reference = clause.Select("b" + start, "b" + end); state.References.Add(reference); PlanningFixtures.Runtime(state, reference, "external_read"); }
        var propertyRef = clause.Select("b9", "b15"); state.References.Add(propertyRef);
        var property = PlanningFixtures.Runtime(state, propertyRef, "human_interaction", independentBoundary: false);
        JsonObject Mapping(PlanningOperations.Scope s) => s.Evidence!.Id == property.Id ? OperationEffectFixtures.Defer(s) : OperationEffectFixtures.Answer(state, s);
        OperationEffectFixtures.Seed(state, Mapping, rootsOnly: true);
        var decision = Assert.Single(PlanningOperations.ApplicabilityDecisions(state));
        var targets = decision.Context["realized"]!.AsObject().Select(p => p.Key).ToArray(); Assert.Equal(2, targets.Length);
        OperationEffectFixtures.SeedPages(state, [decision], new() { [decision.Id] = OperationEffectFixtures.ApplicabilityAnswer(decision, targets) });
        OperationEffectFixtures.SeedDependencies(state, OperationEffectFixtures.Staged(state));
        await PlanningOperations.ResolveAsync(state, NoCalls(), TestContext.Current.CancellationToken);
        Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.All(state.Obligations, o =>
        {
            Assert.Equal("external_read", o.Kind);
            Assert.Equal(propertyRef.Id, Assert.Single(o.OperationAdmission!.Assignments, a => a.Disposition == "attach").ActionReference);
            Assert.Equal(PlanningApplicabilityOrigin.ModelApplicability, Assert.Single(o.OperationAdmission.GoverningApplicability).Origin);
        });
    }

}
