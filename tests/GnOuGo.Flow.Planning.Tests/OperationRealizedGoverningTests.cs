using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic complete coverage; original independent-answer receipts remain historical only.</summary>
public sealed class OperationRealizedGoverningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("No model decision expected.") };
    private static PlanningRuntimeEvidence Add(PlanningSnapshot state, int clause, string role = "action", bool independent = true, bool required = true) =>
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state)[clause].Clause, evidenceRole: role, independentBoundary: independent, required: required);
    private static string Own(PlanningSnapshot state, PlanningRuntimeEvidence evidence) => PlanningOperations.EffectDomain(state, evidence)
        .Single(p => p.Value.BoundaryReference == evidence.ActionReference).Key;

    [Fact]
    public async Task JointDescriptionCannotSelectUnrealizedInvocationOrCreateRoot()
    {
        var state = OperationAdmissionTests.State("Transform the value. The transformation is deterministic.");
        var action = Add(state, 0); var description = Add(state, 1, independent: false);
        var selected = Own(state, action);
        var group = Assert.Single(OperationEffectFixtures.Groups(state, s => OperationEffectFixtures.Answer(state, s, [selected],
            s.Evidence!.Id == description.Id ? "governs" : "realizes")));
        Assert.Single(group.Effects);
        Assert.All(group.Plans.Values, mappings => Assert.Contains(mappings, m => m.Disposition == "supports"));
        var answer = OperationEffectFixtures.CoverageAnswer(state, group, s => OperationEffectFixtures.Answer(state, s, [selected],
            s.Evidence!.Id == description.Id ? "governs" : "realizes"));
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, group.Decision.Schema));
        var historical = new JsonObject { ["status"] = "governing", ["evidence"] = OperationEffectFixtures.Strings([action.ClauseReference]) };
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(historical, group.Decision.Schema));
        OperationEffectFixtures.SeedPages(state, [group.Decision], new() { [group.Id] = answer });
        OperationEffectFixtures.SeedApplicability(state);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var operation = Assert.Single(state.Obligations);
        Assert.Equal(selected, operation.Id);
        Assert.Contains(operation.OperationAdmission!.Assignments, a => a.RuntimeEvidenceId == description.Id && a.Disposition == "attach");
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), id => id.StartsWith("operation_", StringComparison.Ordinal) || id.StartsWith("effect_governing_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExactOwnedOccurrenceApplicabilityIsDeterministic()
    {
        var state = OperationAdmissionTests.State("Transform the value according to its rules.");
        Add(state, 0);
        OperationEffectFixtures.SeedBoundaries(state);
        var scope = Assert.Single(PlanningOperations.Scopes(state));
        var qualification = OperationEffectFixtures.ContributionAnswer(state, scope);
        qualification["contributions"]!.AsArray().Add(OperationEffectFixtures.ContributionAnswer(state, scope,
            OperationEffectFixtures.Defer(scope))["contributions"]![0]!.DeepClone());
        OperationEffectFixtures.SeedPages(state, PlanningOperations.ContributionDecisions(state), new() { [PlanningOperations.ContributionDecisionId(scope.Evidence!)] = qualification });
        var group = Assert.Single(PlanningOperations.CoverageGroups(state));
        var mapping = Assert.Single(group.Plans);
        var effect = Assert.Single(group.Effects).Key;
        OperationEffectFixtures.SeedPages(state, [group.Decision], new() { [group.Id] = new JsonObject { ["status"] = "complete", ["mapping"] = mapping.Key,
            ["effects"] = new JsonObject { [effect] = new JsonObject { ["inputs"] = new JsonArray(), ["outputs"] = new JsonArray() } } } });
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var proof = Assert.Single(state.Obligations).OperationAdmission!;
        Assert.Single(proof.Assignments, a => a.Disposition == "attach");
        Assert.Equal(3, proof.RealizationCoverage!.Version);
        Assert.Single(state.DecisionPages, p => p.Decisions.Any(id => id.StartsWith("coverage_", StringComparison.Ordinal))); Assert.Empty(state.RequestAccounting);
    }

    [Theory]
    [InlineData("governs")]
    [InlineData("shared_rule")]
    public async Task SharedAndSingleGoverningMappingsContainOnlyRealizedTargets(string contribution)
    {
        var state = OperationAdmissionTests.State("Transform the first value. Independently transform the second. These rules govern transformations.");
        var first = Add(state, 0); var second = Add(state, 1); var description = Add(state, 2, "governing");
        var targets = new[] { Own(state, first), Own(state, second) };
        var groups = OperationEffectFixtures.Groups(state, s => s.Evidence!.Id == description.Id
            ? OperationEffectFixtures.Answer(state, s, contribution == "shared_rule" ? targets : [targets[0]], contribution) : OperationEffectFixtures.Answer(state, s));
        Assert.All(groups.SelectMany(g => g.Plans.Values), mappings =>
        {
            var realized = mappings.Where(m => m.Disposition == "supports").SelectMany(m => m.Effects).ToHashSet();
            Assert.All(mappings.Where(m => m.Disposition == "governs").SelectMany(m => m.Effects), t => Assert.Contains(t, realized));
        });
        OperationEffectFixtures.Seed(state, s => s.Evidence!.Id == description.Id
            ? OperationEffectFixtures.Answer(state, s, contribution == "shared_rule" ? targets : [targets[0]], contribution)
            : OperationEffectFixtures.Answer(state, s));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal(2, state.Obligations.Count);
        Assert.Equal(contribution == "shared_rule" ? 2 : 1, state.Obligations.Count(o => o.OperationAdmission!.Assignments.Any(a => a.Disposition == "attach")));
    }

    [Fact]
    public async Task NoExecutableAuthorityCannotBePromotedFromDescription()
    {
        var state = OperationAdmissionTests.State("This processing is deterministic."); Add(state, 0, "governing");
        OperationEffectFixtures.SeedContributions(state);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code); Assert.Empty(state.RequestAccounting); Assert.Empty(state.Obligations);
    }

    [Fact]
    public async Task GoverningMayPrecedeOutputlessRealizationAndEnumerationDoesNotChangeProof()
    {
        var state = OperationAdmissionTests.State("The processing is deterministic. Perform the transformation.");
        Add(state, 0, "governing"); var action = Add(state, 1); OperationEffectFixtures.Seed(state);
        var reverse = PlanningContext.Clone(state); reverse.RuntimeEvidence.Reverse(); reverse.References.Reverse();
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct); await PlanningOperations.ResolveAsync(reverse, NoModel(), Ct);
        Assert.Equal(Own(state, action), Assert.Single(state.Obligations).Id);
        Assert.Equal(state.OperationAdmissionFingerprint, reverse.OperationAdmissionFingerprint);
        Assert.Equal(2, state.Obligations[0].OperationAdmission!.Assignments.Count);
        Assert.Empty(state.Obligations[0].OperationAdmission!.RealizationCoverage!.Effects.Single().Outputs);
    }

    [Fact]
    public async Task OptionalOmissionIsPersistedAndCannotHideRequiredContribution()
    {
        var state = OperationAdmissionTests.State("Optionally transform the value."); Add(state, 0, required: false);
        var group = Assert.Single(OperationEffectFixtures.Groups(state));
        var answer = OperationEffectFixtures.CoverageAnswer(state, group, _ => new() { ["status"] = "omitted" });
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, group.Decision.Schema));
        OperationEffectFixtures.Seed(state, _ => new() { ["status"] = "omitted" });
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Empty(state.Obligations); Assert.NotNull(state.OperationAdmissionFingerprint);
        Assert.Equal("omitted", Assert.Single(Assert.Single(PlanningOperations.ReadCoverage(state)).Contributions).Disposition);
        var restored = PlanningContext.Clone(state); await PlanningOperations.ResolveAsync(restored, NoModel(), Ct);
        Assert.Equal(state.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
        restored.DecisionPages.Clear(); Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(restored));
        var required = OperationAdmissionTests.State("Transform the value when enabled."); Add(required, 0);
        Assert.DoesNotContain(Assert.Single(OperationEffectFixtures.Groups(required)).Plans.Values.SelectMany(p => p), c => c.Disposition == "omitted");
    }

    [Fact]
    public async Task RestartReusesCompleteCoverageAndAssignmentOrderHasNoAuthority()
    {
        var state = OperationAdmissionTests.State("Transform the value. Apply these detailed rules. Describe this transformation.");
        var root = Add(state, 0); Add(state, 1, independent: false); Add(state, 2, "governing");
        PlanningSnapshot? saved = null;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
            { Json = OperationEffectFixtures.Response(state, request), CompletionStatus = "completed" }) };
        runtime.OnCheckpoint = s =>
        {
            if (saved is null && s.DecisionPages.Any(p => p.Status == "completed" && p.Decisions.Any(id => id.StartsWith("applicability_", StringComparison.Ordinal))))
            { saved = PlanningContext.Clone(s); throw new OperationCanceledException(); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(saved); Assert.Empty(saved.Obligations);
        var requests = saved.RequestAccounting.Count;
        await PlanningOperations.ResolveAsync(saved, NoModel(), Ct);
        var original = Assert.Single(saved.Obligations); var originalFingerprint = saved.OperationAdmissionFingerprint;
        original.OperationAdmission!.Assignments.Reverse();
        // Canonical proof hashes normalize evidence ordering instead of privileging index zero.
        saved.Obligations[0] = PlanningOperations.Prove(saved, original, original.OperationAdmission);
        await PlanningOperations.ResolveAsync(saved, NoModel(), Ct);
        Assert.Equal(Own(saved, root), saved.Obligations[0].Id);
        Assert.Equal(originalFingerprint, saved.OperationAdmissionFingerprint);
        Assert.Equal(requests, saved.RequestAccounting.Count); Assert.Empty(saved.RepairAllowances);
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("missing_effect")]
    [InlineData("foreign_effect")]
    [InlineData("foreign_input")]
    public void CoverageSchemaRejectsIncompleteAndForeignAssignments(string defect)
    {
        var state = OperationAdmissionTests.State("Perform a transformation."); Add(state, 0);
        var group = Assert.Single(OperationEffectFixtures.Groups(state));
        var answer = OperationEffectFixtures.CoverageAnswer(state, group);
        if (defect == "mapping") answer["mapping"] = "foreign";
        if (defect == "missing_effect") answer["effects"] = new JsonObject();
        if (defect == "foreign_effect") answer["effects"]!["foreign"] = new JsonObject();
        if (defect == "foreign_input") answer["effects"]!.AsObject().First().Value!["inputs"] = new JsonArray("foreign");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, group.Decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseCoverage(state, group, answer));
    }

    [Fact]
    public void IndependentlyProvenRequiredBoundariesCannotCollectivelyDefer()
    {
        var state = OperationAdmissionTests.State("Perform the first transformation. Independently perform another.");
        Add(state, 0); Add(state, 1);
        var groups = OperationEffectFixtures.Groups(state); Assert.Equal(2, groups.Length);
        foreach (var group in groups)
        {
            Assert.All(group.Plans.Values, p => Assert.All(p, c => Assert.Equal("supports", c.Disposition)));
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(new JsonObject { ["status"] = "not_an_effect" }, group.Decision.Schema));
        }
    }
}
