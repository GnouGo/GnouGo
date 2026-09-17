using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Semantic answers are explicitly synthetic. These tests prove linkage, domains and durability, not linguistic infallibility.</summary>
public sealed class JointClauseQualificationTests
{
    private static PlanningSnapshot Result(string text)
    {
        var state = OperationAdmissionTests.State(text);
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        return state;
    }
    private static PlanningRuntimeEvidence Add(PlanningSnapshot state, PlanningOperations.Scope scope, string start, string end,
        PlanningOperationNecessity necessity = PlanningOperationNecessity.Unspecified)
    {
        var reference = scope.Select(start, end); state.References.Add(reference);
        var evidence = PlanningFixtures.Runtime(state, reference, independentBoundary: false);
        state.RuntimeEvidence.Remove(evidence);
        evidence = PlanningOperations.SealRuntime(state, evidence with { Necessity = necessity,
            NecessityReference = necessity == PlanningOperationNecessity.Unspecified ? null : reference.Id });
        state.RuntimeEvidence.Add(evidence); PlanningFixtures.EmptyRuntime(state); return evidence;
    }
    private static JsonObject Range(string start, string end) => new() { ["start"] = start, ["end"] = end };

    [Fact]
    public void OverlappingRuntimeEvidenceHasOneIndivisibleClauseDecisionAndCompleteProvenance()
    {
        var state = Result("Classify as low when absent and high otherwise.");
        var clause = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        Add(state, clause, "b0", "b8"); Add(state, clause, "b2", "b5"); Add(state, clause, "b6", "b8", PlanningOperationNecessity.Required);
        var scope = PlanningOperations.Scopes(state).First(s => s.Evidence!.ActionReference == state.References.Single(r => r.Start == 0 && r.Length == state.Request.Prompt.Length && r.Kind.EndsWith(":selection", StringComparison.Ordinal)).Id);
        var decision = Assert.Single(PlanningOperations.ContributionDecisions(state));
        Assert.DoesNotContain("\"supports\"", decision.Schema.ToJsonString());
        Assert.Equal(3, decision.Context["provenance"]!.AsObject().Count);
        Assert.Equal(4, decision.SourceDecisionIds!.Count);
        var answer = OperationEffectFixtures.ContributionAnswer(state, scope);
        answer["units"]![0]!["request"]!["predicate"] = Range("b0", "b2");
        answer["units"]![0]!["request"]!["evidence"] = new JsonArray((JsonNode)Range("b0", "b2"), Range("b2", "b5"), Range("b5", "b8"));
        var proof = PlanningOperations.ParseContributions(state, scope, answer);
        var unit = Assert.Single(proof.Units);
        Assert.Equal("requested_execution", unit.Role); Assert.Equal(3, proof.Contributions.Count);
        Assert.All(proof.Contributions, support => { Assert.Equal(unit.Id, support.UnitId); Assert.Equal(3, support.RuntimeEvidenceIds.Count); });
        Assert.Equal(3, proof.RuntimeEvidenceIds.Count);
        Assert.NotNull(unit.PredicateReference); Assert.Equal(clause.Clause.Id, unit.ScopeReference);
        var reversed = PlanningContext.Clone(state); reversed.RuntimeEvidence.Reverse(); reversed.References.Reverse();
        answer["units"]![0]!["request"]!["evidence"] = new JsonArray(answer["units"]![0]!["request"]!["evidence"]!.AsArray().Reverse().Select(v => v!.DeepClone()).ToArray());
        Assert.Equal(proof.ProofFingerprint, PlanningOperations.ParseContributions(reversed, PlanningOperations.Scopes(reversed).First(s => s.Evidence!.Id == scope.Evidence!.Id), answer).ProofFingerprint);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("contradictory")]
    public void SupportWithoutAnAccountedOwnedRequestIsRejected(string failure)
    {
        var state = Result("Transform the value using deterministic processing.");
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request").Clause, independentBoundary: false);
        var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = OperationEffectFixtures.ContributionAnswer(state, scope);
        if (failure == "missing") answer["units"]![0]!["request"]!.AsObject().Remove("predicate");
        if (failure == "foreign") answer["units"]![0]!["request"]!["predicate"] = "foreign";
        if (failure == "contradictory")
        {
            answer["units"]![0]!["request"]!["predicate"] = Range("b4", "b6");
            answer["units"]![0]!["request"]!["evidence"] = new JsonArray((JsonNode)Range("b0", "b4"));
            answer["units"]!.AsArray().Add((JsonNode)new JsonObject { ["role"] = "governing_property", ["governingKind"] = "descriptive_property", ["scope"] = scope.Clause.Id, ["evidence"] = Range("b4", "b6") });
        }
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
    }

    [Fact]
    public void PropertiesHaveNoSupportProjectionAndHistoricalFragmentShapeIsRejected()
    {
        var state = Result("This execution is deterministic.");
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request").Clause, "external_execute", independentBoundary: false);
        var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = new JsonObject { ["status"] = "qualified", ["units"] = new JsonArray((JsonNode)new JsonObject
            { ["role"] = "governing_property", ["governingKind"] = "descriptive_property", ["scope"] = scope.Clause.Id, ["evidence"] = scope.Evidence!.ActionReference }) };
        var proof = PlanningOperations.ParseContributions(state, scope, answer);
        Assert.All(proof.Units, u => Assert.Null(u.PredicateReference));
        Assert.DoesNotContain(proof.Contributions, c => c.Role == "supports");
        var old = new JsonObject { ["status"] = "qualified", ["contributions"] = new JsonArray((JsonNode)new JsonObject
            { ["role"] = "supports", ["evidence"] = scope.Evidence.ActionReference }) };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(old, PlanningOperations.ContributionDecision(state, scope).Schema));
    }

    [Fact]
    public void ConflictingNecessityFromAnOverlappingParentCannotDisappear()
    {
        var state = Result("Transform the supplied value."); var scope = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        Add(state, scope, "b0", "b4", PlanningOperationNecessity.Required);
        Add(state, scope, "b2", "b4", PlanningOperationNecessity.Optional);
        var parent = PlanningOperations.Scopes(state).First(s => s.Evidence!.Necessity == PlanningOperationNecessity.Required);
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, parent, OperationEffectFixtures.ContributionAnswer(state, parent)));
        Assert.Contains("conflicting explicit", error.Message);
    }

    [Fact]
    public async Task TwoActionsInOneClauseKeepIndependentOwnedOccurrences()
    {
        var state = OperationAdmissionTests.State("Read the first value then read the second value.");
        var clause = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        foreach (var (start, end) in new[] { ("b0", "b4"), ("b4", "b9") })
        {
            var reference = clause.Select(start, end); state.References.Add(reference);
            PlanningFixtures.Runtime(state, reference, "external_read");
        }
        OperationEffectFixtures.Seed(state);
        Assert.Single(PlanningOperations.ContributionDecisions(state));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected call") };
        await PlanningOperations.ResolveAsync(state, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.All(state.Obligations, o => Assert.DoesNotContain(o.OperationAdmission!.Dependencies!.Assignments, a => a.Disposition == "data"));
        var saved = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var reload = JsonSerializer.Deserialize(saved, PlanningJsonContext.Default.PlanningSnapshot)!;
        runtime.OnCheckpoint = _ => throw new InvalidOperationException("Unexpected write");
        await PlanningOperations.ResolveAsync(reload, runtime, TestContext.Current.CancellationToken);
        Assert.Equal(saved, JsonSerializer.Serialize(reload, PlanningJsonContext.Default.PlanningSnapshot));
    }
}
