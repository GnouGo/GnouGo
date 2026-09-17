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
    private static PlanningSnapshot Result(string text, int supportCandidates = int.MaxValue)
    {
        var state = OperationAdmissionTests.State(text);
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        foreach (var (scope, index) in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").Select((scope, index) => (scope, index)))
            PlanningFixtures.Runtime(state, scope.Clause, index < supportCandidates ? "local_processing" : "external_execute", independentBoundary: false);
        return state;
    }
    private static JsonObject Answer(PlanningSnapshot state, PlanningOperations.Scope scope, string role = "supports") =>
        OperationEffectFixtures.ContributionAnswer(state, scope, OperationEffectFixtures.Answer(state, scope, contribution: role == "supports" ? "realizes" : "governs"));
    private static void Qualify(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject> answer)
    {
        OperationEffectFixtures.SeedBoundaries(state);
        var values = OperationEffectFixtures.QualificationAnswers(state, answer);
        var properties = OperationEffectFixtures.SeparateRequestAuthority(state, values);
        OperationEffectFixtures.SeedPages(state, PlanningOperations.ContributionDecisions(state), properties);
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
        OperationEffectFixtures.SeedApplicability(state);
        OperationEffectFixtures.SeedDependencies(state, OperationEffectFixtures.Staged(state));
    }

    [Fact]
    public async Task CanonicalQualificationSeparatesExecutionFromPropertiesDespitePreliminaryAction()
    {
        var state = Result("Transform the value. Select the outcome according to these rules. The transformation is deterministic.");
        var scopes = PlanningOperations.Scopes(state); var description = scopes.Last().Evidence!.Id;
        Assert.All(scopes, s => Assert.Equal("action", s.Evidence!.EvidenceRole)); // Historical hints confer no authority.
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.CoverageGroups(state));
        Assert.All(OperationEffectFixtures.PropertyDecisions(state), d => Assert.DoesNotContain("EvidenceRole", d.Context.ToJsonString()));
        Qualify(state, s => Answer(state, s, s.Evidence!.Id == description ? "governs" : "supports"));
        var group = Assert.Single(PlanningOperations.CoverageGroups(state));
        Assert.All(group.Plans.Values.SelectMany(p => p).Where(c => c.RuntimeEvidenceId == description), c => Assert.Equal("governs", c.Disposition));
        Cover(state); await PlanningOperations.ResolveAsync(state, NoCalls(), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.True(operation.Required); Assert.Equal(18, operation.OperationAdmission!.Version);
        Assert.Equal(3, operation.OperationAdmission.RealizationCoverage!.Version);
        Assert.Equal(2, operation.OperationAdmission.Assignments.Count(a => a.Disposition == "supports"));
        Assert.Single(operation.OperationAdmission.Assignments, a => a.Disposition == "attach");
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), id => id.StartsWith("operation_", StringComparison.Ordinal));
        Assert.All(operation.OperationAdmission.ExecutionContributions, p => Assert.Equal(8, p.Version));
    }

    [Fact]
    public async Task MixedActionAndPropertySubspansHaveSeparateAuthorityWithOneRuntimeParent()
    {
        var state = OperationAdmissionTests.State("Fetch the record once using the read-only source.");
        var evidence = PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state).Single().Clause, "external_read");
        OperationEffectFixtures.SeedBoundaries(state);
        var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope);
        var execution = answer["units"]![0]!["request"]!; execution["predicate"] = new JsonObject { ["start"] = "b0", ["end"] = "b1" };
        execution["evidence"] = new JsonArray((JsonNode)new JsonObject { ["start"] = "b0", ["end"] = "b4" });
        var property = Answer(state, scope, "governs")["units"]![0]!.DeepClone();
        property["evidence"] = new JsonObject { ["start"] = "b4", ["end"] = "b8" };
        answer["units"]!.AsArray().Add(property);
        Assert.NotNull(OperationEffectFixtures.ParseQualification(state, scope, answer));
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
        var joint = OperationEffectFixtures.QualificationAnswers(state, s => Answer(state, s));
        var cohort = Assert.Single(PlanningOperations.RequestCohorts(state));
        var answer = OperationEffectFixtures.RequestAnswer(state, cohort, joint);
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, cohort.Decision.Schema));
        var proof = PlanningOperations.ParseExecutionRequest(state, cohort, answer);
        Assert.Equal("requested_owned_occurrence", Assert.Single(proof.Units).Basis);
        foreach (var field in new[] { "basis", "owner", "boundary", "t" })
        {
            var forged = answer.DeepClone().AsObject(); forged[scope.Clause.Id]!["u"]![0]!["q"]![field] = "foreign";
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(forged, cohort.Decision.Schema));
            Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseExecutionRequest(state, cohort, forged));
        }
    }

    [Fact]
    public async Task PropertiesAndPublicOutputCannotEstablishRealization()
    {
        var state = Result("This processing is deterministic.");
        Qualify(state, s => Answer(state, s, "governs"));
        Assert.Empty(PlanningOperations.CoverageGroups(state));
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoCalls(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code); Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public void QualificationCannotLoseEvidenceOrInventOmission()
    {
        var state = Result("Transform the value using deterministic processing."); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope); var decision = OperationEffectFixtures.PropertyDecision(state, scope);
        answer["units"]![0]!["role"] = "omitted";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, decision.Schema));
        answer = Answer(state, scope); answer["units"]![0]!["request"]!["evidence"] = new JsonArray((JsonNode)new JsonObject { ["start"] = "b0", ["end"] = "b2" });
        var cohort = Assert.Single(PlanningOperations.RequestCohorts(state));
        var request = OperationEffectFixtures.RequestAnswer(state, cohort, new() { [PlanningOperations.ContributionDecisionId(scope)] = answer });
        Assert.Empty(PlanningContractValidation.ValidateInstance(request, cohort.Decision.Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseExecutionRequest(state, cohort, request));
    }

    [Fact]
    public void AnExclusionCannotContradictOwnedExecutableSupport()
    {
        var state = Result("Transform the supplied value."); var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = Answer(state, scope);
        answer["units"]!.AsArray().Add((JsonNode)new JsonObject
        { ["role"] = "excluded", ["scope"] = scope.Clause.Id, ["basis"] = "no_operation_relevance", ["evidence"] = scope.Evidence!.ActionReference });
        Assert.Throws<WorkflowRuntimeException>(() => OperationEffectFixtures.ParseQualification(state, scope, answer));
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

    [Fact]
    public async Task SeveralSupportsKeepOneEffectAcrossEnumerationAndQualificationPartitions()
    {
        var descriptions = string.Join(" ", Enumerable.Range(1, 5).Select(i =>
            $"Property {i} governs consistent deterministic evaluation of the supplied data and retains the documented processing restrictions for every permitted execution."));
        var state = Result("Transform the supplied value locally. Select the first result according to its condition. Select the fallback result otherwise. " + descriptions, supportCandidates: 3);
        var supports = PlanningOperations.Scopes(state).Where(s => s.Evidence!.Kind == "local_processing").Select(s => s.Evidence!.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(3, supports.Count);
        var other = PlanningContext.Clone(state);
        JsonObject Response(PlanningSnapshot snapshot, PlanningOperations.Scope scope)
        {
            if (!supports.Contains(scope.Evidence!.Id)) return new JsonObject { ["status"] = "qualified", ["units"] = new JsonArray((JsonNode)new JsonObject
                { ["role"] = "governing_property", ["governingKind"] = "descriptive_property", ["scope"] = scope.Clause.Id, ["evidence"] = scope.Evidence.ActionReference }) };
            var answer = Answer(snapshot, scope);
            if (PlanningChoiceEvidence.Text(snapshot, scope.Clause.Id) == "Transform the supplied value locally.")
                answer["units"]![0]!["qualifiers"] = new JsonArray((JsonNode)new JsonObject
                    { ["evidence"] = new JsonObject { ["start"] = "b4", ["end"] = "b5" }, ["governingKind"] = "descriptive_property" });
            return answer;
        }
        other.RuntimeEvidence.Reverse(); other.References.Reverse();
        foreach (var snapshot in new[] { state, other })
        {
            if (ReferenceEquals(snapshot, state)) Qualify(snapshot, scope => Response(snapshot, scope));
            else
            {
                var byId = PlanningOperations.Scopes(snapshot).ToDictionary(scope => PlanningOperations.ContributionDecisionId(scope.Evidence!));
                var properties = OperationEffectFixtures.SeparateRequestAuthority(snapshot, OperationEffectFixtures.QualificationAnswers(snapshot, scope => Response(snapshot, scope)));
                var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
                {
                    var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
                    return Task.FromResult(new GnOuGo.Flow.Core.Runtime.LLMResponse { CompletionStatus = fields.Count > 1 ? "output_limit" : "completed",
                        Json = fields.Count > 1 ? null : new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, properties[p.Key]!.DeepClone()))) });
                } };
                await PlanningDecisionPages.ResolveAsync(snapshot, runtime, "intent_operations", "$plan", OperationEffectFixtures.PropertyDecisions(snapshot), Ct);
                Assert.Contains(snapshot.DecisionPages, p => p.Origin == PlanningDecisionPageOrigin.OutputPartition);
            }
            Cover(snapshot);
            await PlanningOperations.ResolveAsync(snapshot, NoCalls(), Ct);
            var operation = Assert.Single(snapshot.Obligations, PlanningSourceDecisions.IsOperation);
            Assert.Equal(3, operation.OperationAdmission!.Assignments.Count(a => a.Disposition == "supports"));
            Assert.Empty(operation.OperationAdmission.Dependencies!.Assignments);
        }
        Assert.NotEqual(state.DecisionPages.Count(p => p.Decisions.Any(d => d.StartsWith("contribution_", StringComparison.Ordinal))),
            other.DecisionPages.Count(p => p.Decisions.Any(d => d.StartsWith("contribution_", StringComparison.Ordinal))));
        Assert.Equal(state.OperationAdmissionFingerprint, other.OperationAdmissionFingerprint);
        Assert.Equal(PlanningOperations.ReadContributions(state).Select(p => p.ProofFingerprint), PlanningOperations.ReadContributions(other).Select(p => p.ProofFingerprint));
        Assert.Equal(PlanningOperations.ReadCoverage(state).Select(p => p.ProofFingerprint), PlanningOperations.ReadCoverage(other).Select(p => p.ProofFingerprint));
    }

    [Fact]
    public async Task SeveralSupportsSurvivePartialQualificationRestartWithoutDuplicateAuthority()
    {
        var state = Result("Transform the supplied value. Select the first result according to its condition. Select the fallback result otherwise.");
        var uninterrupted = PlanningContext.Clone(state);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new GnOuGo.Flow.Core.Runtime.LLMResponse
            { Json = OperationEffectFixtures.Response(state, request), CompletionStatus = "completed" }) };
        PlanningSnapshot? saved = null;
        runtime.OnCheckpoint = s =>
        {
            if (saved is null && s.DecisionPages.Any(p => p.Status == "completed" && p.Decisions.Any(d => d.StartsWith("contribution_", StringComparison.Ordinal))))
            { saved = PlanningContext.Clone(s); throw new OperationCanceledException(); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(saved); Assert.Null(saved.OperationAdmissionFingerprint);
        var completed = saved.DecisionPages.Where(p => p.Status == "completed").Select(p => p.Id).ToArray();
        var resumed = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new GnOuGo.Flow.Core.Runtime.LLMResponse
            { Json = OperationEffectFixtures.Response(saved, request), CompletionStatus = "completed" }) };
        await PlanningOperations.ResolveAsync(saved, resumed, Ct);
        Qualify(uninterrupted, s => Answer(uninterrupted, s)); Cover(uninterrupted);
        await PlanningOperations.ResolveAsync(uninterrupted, NoCalls(), Ct);
        Assert.Equal(uninterrupted.OperationAdmissionFingerprint, saved.OperationAdmissionFingerprint);
        Assert.Equal(runtime.Requests.Count + resumed.Requests.Count, saved.RequestAccounting.Select(c => c.Id).Distinct().Count());
        Assert.All(completed, id => Assert.Single(saved.DecisionPages, p => p.Id == id && p.Status == "completed"));
        Assert.Equal(3, Assert.Single(saved.Obligations, PlanningSourceDecisions.IsOperation).OperationAdmission!.Assignments.Count(a => a.Disposition == "supports"));
        var readOnly = NoCalls(); readOnly.OnCheckpoint = _ => throw new InvalidOperationException("Unexpected write");
        var snapshot = JsonSerializer.Serialize(saved, PlanningJsonContext.Default.PlanningSnapshot);
        await PlanningOperations.ResolveAsync(saved, readOnly, Ct);
        Assert.Equal(snapshot, JsonSerializer.Serialize(saved, PlanningJsonContext.Default.PlanningSnapshot));
    }
}
