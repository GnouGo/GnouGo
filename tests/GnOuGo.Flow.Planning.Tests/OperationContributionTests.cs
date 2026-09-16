using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Explicit synthetic qualifications. No historical receipt is rewritten or used as a new answer.</summary>
public sealed class OperationContributionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoCalls() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected dispatch") };
    private static PlanningSnapshot Result(string text)
    {
        var state = OperationAdmissionTests.State(text);
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        foreach (var scope in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request"))
            PlanningFixtures.Runtime(state, scope.Clause, independentBoundary: false);
        return state;
    }
    private static JsonObject Answer(PlanningSnapshot state, PlanningOperations.Scope scope, string role = "supports") =>
        OperationEffectFixtures.ContributionAnswer(state, scope, OperationEffectFixtures.Answer(state, scope, contribution: role == "supports" ? "realizes" : "governs"));
    private static void Qualify(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject> answer)
    {
        OperationEffectFixtures.SeedBoundaries(state);
        OperationEffectFixtures.SeedPages(state, PlanningOperations.ContributionDecisions(state), new JsonObject(PlanningOperations.Scopes(state)
            .Where(s => s.Evidence!.BaselineReference is null).Select(s => new KeyValuePair<string, JsonNode?>(PlanningOperations.ContributionDecisionId(s.Evidence!), answer(s)))));
    }
    private static void Cover(PlanningSnapshot state)
    {
        var groups = PlanningOperations.CoverageGroups(state);
        var answers = new JsonObject(groups.Select(g =>
        {
            var plan = g.Plans.First(p => p.Value.All(c => c.Disposition != "omitted"));
            var effects = new JsonObject(g.Effects.Select(e => new KeyValuePair<string, JsonNode?>(e.Key, new JsonObject
            {
                ["inputs"] = OperationEffectFixtures.Strings(state.Declarations.Where(d => d.Direction == "input" && d.WorkflowScope == e.Value.WorkflowScope).Select(d => d.Id)),
                ["outputs"] = OperationEffectFixtures.Strings(e.Value.BoundaryKind == "result_realization" ? [e.Value.OwnerReference] : [])
            })));
            return new KeyValuePair<string, JsonNode?>(g.Id, new JsonObject { ["status"] = "complete", ["mapping"] = plan.Key, ["effects"] = effects });
        }));
        OperationEffectFixtures.SeedPages(state, groups.Select(g => g.Decision).ToArray(), answers);
        OperationEffectFixtures.SeedDependencies(state, PlanningOperations.MaterializeCoverage(state));
    }

    [Fact]
    public async Task CanonicalQualificationSeparatesExecutionFromPropertiesDespitePreliminaryAction()
    {
        var state = Result("Transform the value. Select the outcome according to these rules. The transformation is deterministic.");
        var scopes = PlanningOperations.Scopes(state); var description = scopes.Last().Evidence!.Id;
        Assert.All(scopes, s => Assert.Equal("action", s.Evidence!.EvidenceRole)); // Historical hints confer no authority.
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.CoverageGroups(state));
        Assert.All(PlanningOperations.ContributionDecisions(state), d => Assert.DoesNotContain("EvidenceRole", d.Context.ToJsonString()));
        Qualify(state, s => Answer(state, s, s.Evidence!.Id == description ? "governs" : "supports"));
        var group = Assert.Single(PlanningOperations.CoverageGroups(state));
        Assert.All(group.Plans.Values.SelectMany(p => p).Where(c => c.RuntimeEvidenceId == description), c => Assert.Equal("governs", c.Disposition));
        Cover(state); await PlanningOperations.ResolveAsync(state, NoCalls(), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.True(operation.Required); Assert.Equal(11, operation.OperationAdmission!.Version);
        Assert.Equal(2, operation.OperationAdmission.RealizationCoverage!.Version);
        Assert.Equal(2, operation.OperationAdmission.Assignments.Count(a => a.Disposition == "supports"));
        Assert.Single(operation.OperationAdmission.Assignments, a => a.Disposition == "attach");
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), id => id.StartsWith("operation_", StringComparison.Ordinal));
        Assert.All(operation.OperationAdmission.ExecutionContributions, p => Assert.Equal(1, p.Version));
    }

    [Fact]
    public async Task MixedActionAndPropertySubspansHaveSeparateAuthorityWithOneRuntimeParent()
    {
        var state = OperationAdmissionTests.State("Fetch the record once using the read-only source.");
        var evidence = PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state).Single().Clause, "external_read");
        OperationEffectFixtures.SeedBoundaries(state);
        var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope);
        var execution = answer["contributions"]![0]!; execution["evidence"] = new JsonObject { ["start"] = "b0", ["end"] = "b4" };
        var property = Answer(state, scope, "governs")["contributions"]![0]!.DeepClone();
        property["evidence"] = new JsonObject { ["start"] = "b4", ["end"] = "b8" };
        answer["contributions"]!.AsArray().Add(property);
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.ContributionDecision(state, scope).Schema));
        Qualify(state, _ => answer); Cover(state); await PlanningOperations.ResolveAsync(state, NoCalls(), Ct);
        var assignments = Assert.Single(state.Obligations).OperationAdmission!.Assignments;
        Assert.Equal(2, assignments.Count); Assert.All(assignments, a => Assert.Equal(evidence.Id, a.RuntimeEvidenceId));
        Assert.Equal(2, assignments.Select(a => a.ContributionId).Distinct().Count());
        Assert.Equal("Fetch the record once", PlanningChoiceEvidence.Text(state, assignments.Single(a => a.Disposition == "supports").ActionReference));
        Assert.Equal("using the read-only source.", PlanningChoiceEvidence.Text(state, assignments.Single(a => a.Disposition == "attach").ActionReference));
        var before = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var restored = JsonSerializer.Deserialize(before, PlanningJsonContext.Default.PlanningSnapshot)!;
        var runtime = NoCalls(); runtime.OnCheckpoint = _ => throw new InvalidOperationException("Unexpected write");
        await PlanningOperations.ResolveAsync(restored, runtime, Ct);
        Assert.Equal(before, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSnapshot));
    }

    [Theory]
    [InlineData("external_read")]
    [InlineData("external_write")]
    [InlineData("human_interaction")]
    [InlineData("resource_lifecycle")]
    [InlineData("local_processing")]
    public void OwnedOccurrenceSupportRequiresExactPositiveBasis(string kind)
    {
        var state = OperationAdmissionTests.State("Perform the separately requested execution.");
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state).Single().Clause, kind, resourceAction: kind == "resource_lifecycle" ? "create" : null);
        OperationEffectFixtures.SeedBoundaries(state); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var decision = PlanningOperations.ContributionDecision(state, scope); var answer = Answer(state, scope);
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        Assert.Equal("requested_owned_occurrence", answer["contributions"]![0]!["basis"]!.ToString());
        foreach (var field in new[] { "basis", "owner", "boundary", "effect" })
        {
            var forged = answer.DeepClone(); forged["contributions"]![0]![field] = "foreign";
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(forged, decision.Schema));
            Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, forged.AsObject()));
        }
    }

    [Fact]
    public async Task PropertiesAndPublicOutputCannotEstablishRealization()
    {
        var state = Result("This processing is deterministic.");
        Qualify(state, s => Answer(state, s, "governs"));
        Assert.Empty(Assert.Single(PlanningOperations.CoverageGroups(state)).Plans);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoCalls(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code); Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public void QualificationCannotLoseEvidenceOrInventOmission()
    {
        var state = Result("Transform the value using deterministic processing."); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope); var decision = PlanningOperations.ContributionDecision(state, scope);
        answer["contributions"]![0]!["role"] = "omitted";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        answer = Answer(state, scope); answer["contributions"]![0]!["evidence"] = new JsonObject { ["start"] = "b0", ["end"] = "b2" };
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
    }

    [Fact]
    public void AnExclusionCannotContradictOwnedExecutableSupport()
    {
        var state = Result("Transform the supplied value."); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope);
        answer["contributions"]!.AsArray().Add((JsonNode)new JsonObject
        { ["role"] = "excluded", ["basis"] = "no_requested_execution", ["evidence"] = scope.Evidence!.ActionReference });
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseContributions(state, scope, answer));
    }

    [Fact]
    public async Task ExactSpanPolicyMeaningDoesNotVetoRequestedExecution()
    {
        var state = Result("Transform the supplied value."); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var policy = new PlanningObligation("meaning", [scope.Evidence!.ActionReference!], "workflow", "implementation_policy", true);
        state.Obligations.Add(policy with { Grounding = PlanningSourceGroundingRules.Create(state, policy) });
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        Qualify(state, s => Answer(state, s)); Cover(state); await PlanningOperations.ResolveAsync(state, NoCalls(), Ct);
        Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Contains(state.Obligations, o => o.Id == policy.Id);
    }

    [Fact]
    public void RuntimeSchemaNoLongerExposesSupportRoleOrCanonicalIdentity()
    {
        var state = Result("Transform the supplied value."); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var schema = PlanningOperations.RuntimeSchema(state, PlanningSourceAuthority.RequestedBehavior, scope.Boundaries);
        var variants = schema["items"]!["anyOf"]!.AsArray();
        Assert.All(variants, v => { Assert.Null(v!["properties"]!["evidence"]); Assert.Null(v["properties"]!["subject"]); Assert.Null(v["properties"]!["occurrence"]); });
    }

    [Fact]
    public async Task ContributionProofsAreOrderIndependentAndRejectStaleOrForgedAuthority()
    {
        var state = Result("Transform the supplied value. The transformation is deterministic.");
        var ids = PlanningOperations.Scopes(state).Select(s => s.Evidence!.Id).ToArray();
        var other = PlanningContext.Clone(state); other.RuntimeEvidence.Reverse(); other.References.Reverse();
        foreach (var snapshot in new[] { state, other })
        {
            Qualify(snapshot, s => Answer(snapshot, s, s.Evidence!.Id == ids[1] ? "governs" : "supports"));
            Cover(snapshot); await PlanningOperations.ResolveAsync(snapshot, NoCalls(), Ct);
        }
        Assert.Equal(state.OperationAdmissionFingerprint, other.OperationAdmissionFingerprint);
        Assert.Equal(PlanningOperations.ReadContributions(state).Select(p => p.ProofFingerprint), PlanningOperations.ReadContributions(other).Select(p => p.ProofFingerprint));
        var operation = Assert.Single(other.Obligations, PlanningSourceDecisions.IsOperation);
        var assignment = operation.OperationAdmission!.Assignments.Single(a => a.Disposition == "attach");
        operation.OperationAdmission.Assignments.Remove(assignment);
        operation.OperationAdmission.Assignments.Add(assignment with { Disposition = "supports" });
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(other));
    }
}
