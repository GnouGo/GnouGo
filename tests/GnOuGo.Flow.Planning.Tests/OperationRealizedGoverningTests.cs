using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Explicit synthetic realizations and governing responses, not historical model evidence.</summary>
public sealed class OperationRealizedGoverningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("No model decision expected.") };
    private static PlanningRuntimeEvidence Add(PlanningSnapshot state, int clause, string role = "action") =>
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state)[clause].Clause, evidenceRole: role);
    private static string Own(PlanningSnapshot state, PlanningRuntimeEvidence evidence) => PlanningOperations.EffectDomain(state, evidence)
        .Single(p => p.Value.WorkflowScope == "main" && p.Value.BoundaryReference == evidence.ActionReference).Key;

    [Fact]
    public async Task DeferredDescriptionCannotSelectItsUnusedInvocationOrCreateARoot()
    {
        var state = OperationAdmissionTests.State("Transform the value. The transformation is deterministic.");
        var action = Add(state, 0); var description = Add(state, 1);
        var unused = Own(state, description); var selected = Own(state, action);
        var phases = new List<string>();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            var answers = new JsonObject();
            foreach (var field in request.StructuredOutputSchema!["properties"]!.AsObject())
            {
                var scope = OperationEffectFixtures.Scope(state, field.Key);
                var governing = field.Key == PlanningOperations.EffectDecisionId(scope.Evidence!, true);
                phases.Add(governing ? "governing" : "realization");
                if (governing)
                {
                    Assert.Empty(state.Obligations); // no partially committed operation graph
                    Assert.DoesNotContain(unused, field.Value!.ToJsonString());
                    var forged = OperationEffectFixtures.Answer(state, scope, [unused], "governs");
                    Assert.NotEmpty(PlanningContractValidation.ValidateInstance(forged, field.Value.AsObject()));
                    answers[field.Key] = OperationEffectFixtures.Answer(state, scope, [selected], "governs");
                }
                else if (scope.Evidence!.Id == description.Id)
                {
                    var illegal = OperationEffectFixtures.Answer(state, scope, [unused], "governs");
                    Assert.NotEmpty(PlanningContractValidation.ValidateInstance(illegal, field.Value!.AsObject()));
                    answers[field.Key] = OperationEffectFixtures.Defer(scope);
                }
                else answers[field.Key] = OperationEffectFixtures.Answer(state, scope, [selected]);
            }
            return Task.FromResult(new LLMResponse { Json = answers, CompletionStatus = "completed" });
        } };
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations);
        Assert.Equal(selected, operation.Id);
        Assert.Equal("governing", phases[^1]); Assert.Equal(2, phases.Count(p => p == "realization"));
        Assert.Contains(operation.OperationAdmission!.Assignments, a => a.RuntimeEvidenceId == description.Id && a.Disposition == "attach");
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), id => id.StartsWith("operation_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleRealizationWithTheSameOwnedGoverningClauseAttachesDeterministically()
    {
        var state = OperationAdmissionTests.State("Transform the value according to its rules.");
        Add(state, 0); Add(state, 0, "governing");
        OperationEffectFixtures.Seed(state, rootsOnly: true);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var operation = Assert.Single(state.Obligations);
        var attachment = Assert.Single(operation.OperationAdmission!.Assignments, a => a.Disposition == "attach");
        Assert.Equal("deterministic", attachment.Effect!.Origin);
        Assert.Empty(state.RequestAccounting);
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), id => id.StartsWith("effect_governing_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("governs")]
    [InlineData("shared_rule")]
    public void GoverningDomainsContainSelectedRealizationsOnly(string contribution)
    {
        var state = OperationAdmissionTests.State("Transform the first value. Independently transform the second. These rules govern transformations.");
        var first = Add(state, 0); var second = Add(state, 1); var description = Add(state, 2);
        OperationEffectFixtures.Seed(state, s => s.Evidence!.Id == description.Id ? OperationEffectFixtures.Defer(s) : OperationEffectFixtures.Answer(state, s), rootsOnly: true);
        var realized = PlanningOperations.ReadRealizations(state);
        var scope = PlanningOperations.Scopes(state).Single(s => s.Evidence!.Id == description.Id);
        var decision = PlanningOperations.EffectDecision(state, scope, realized);
        var targets = new[] { Own(state, first), Own(state, second) };
        Assert.Equal(targets.Order(), PlanningOperations.RealizedDomain(state, description, realized).Keys.Order());
        var answer = OperationEffectFixtures.Answer(state, scope, contribution == "shared_rule" ? targets : [targets[0]], contribution);
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        Assert.Equal(contribution, PlanningOperations.ParseEffect(state, scope, answer, realized).Contribution);
        answer["effects"] = OperationEffectFixtures.Strings([Own(state, description)]);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseEffect(state, scope, answer, realized));
    }

    [Fact]
    public async Task NoRealizationStopsBeforeGoverningDispatch()
    {
        var state = OperationAdmissionTests.State("This processing is deterministic."); Add(state, 0);
        OperationEffectFixtures.Seed(state, OperationEffectFixtures.Defer, rootsOnly: true);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code);
        Assert.Empty(state.RequestAccounting); Assert.Empty(state.Obligations);
    }

    [Fact]
    public async Task GoverningMayPrecedeTheOutputlessRealizationInSourceOrder()
    {
        var state = OperationAdmissionTests.State("The processing is deterministic. Perform the transformation.");
        Add(state, 0, "governing"); var action = Add(state, 1);
        OperationEffectFixtures.Seed(state);
        var reverse = PlanningContext.Clone(state); reverse.RuntimeEvidence.Reverse();
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        await PlanningOperations.ResolveAsync(reverse, NoModel(), Ct);
        Assert.Equal(Own(state, action), Assert.Single(state.Obligations).Id);
        Assert.Equal(state.OperationAdmissionFingerprint, reverse.OperationAdmissionFingerprint);
        Assert.Equal(2, state.Obligations[0].OperationAdmission!.Assignments.Count);
        Assert.Empty(state.Obligations[0].OperationAdmission!.Assignments[0].Effect!.Outputs);
    }

    [Fact]
    public async Task RestartReusesRealizationsAndGoverningDomainRequiresTheSameSelectedSet()
    {
        var state = OperationAdmissionTests.State("Transform one value. Independently transform another. Describe the transformation.");
        var first = Add(state, 0); var second = Add(state, 1); var description = Add(state, 2, "governing");
        OperationEffectFixtures.Seed(state, s => s.Evidence!.Id == second.Id ? new() { ["status"] = "not_an_effect" } : s.Evidence.EvidenceRole == "governing" ? OperationEffectFixtures.Defer(s) : OperationEffectFixtures.Answer(state, s), rootsOnly: true);
        var before = PlanningOperations.ReadRealizations(state);
        var scope = PlanningOperations.Scopes(state).Single(s => s.Evidence!.Id == description.Id);
        var original = PlanningOperations.EffectDecision(state, scope, before);
        var restored = PlanningContext.Clone(state);
        Assert.Equal(original.EvidenceFingerprint, PlanningOperations.EffectDecision(restored, scope, PlanningOperations.ReadRealizations(restored)).EvidenceFingerprint);
        Assert.Equal(original.Schema.ToJsonString(), PlanningOperations.EffectDecision(restored, scope, PlanningOperations.ReadRealizations(restored)).Schema.ToJsonString());
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
            { Json = OperationEffectFixtures.Response(restored, request), CompletionStatus = "completed" }) };
        await PlanningOperations.ResolveAsync(restored, runtime, Ct); Assert.Single(runtime.Requests);
        var completed = PlanningContext.Clone(restored);
        await PlanningOperations.ResolveAsync(completed, NoModel(), Ct);
        Assert.Equal(restored.RequestAccounting.Count, completed.RequestAccounting.Count);
        Assert.Equal(restored.OperationAdmissionFingerprint, completed.OperationAdmissionFingerprint);
        // A changed selected root changes both target domain and durable evidence fingerprint.
        var rootScope = PlanningOperations.Scopes(state).Single(s => s.Evidence!.Id == first.Id);
        var page = state.DecisionPages.Single(p => p.Candidate?.ContainsKey(PlanningOperations.EffectDecisionId(first)) == true);
        page.Candidate![PlanningOperations.EffectDecisionId(first)] = OperationEffectFixtures.Answer(state, rootScope, [Own(state, second)]);
        var changed = PlanningOperations.ReadRealizations(state);
        var next = PlanningOperations.EffectDecision(state, scope, changed);
        Assert.NotEqual(original.EvidenceFingerprint, next.EvidenceFingerprint);
        var stale = OperationEffectFixtures.Answer(state, scope, [Own(state, first)], "governs");
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(stale, next.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseEffect(state, scope, stale, changed));
    }
}
