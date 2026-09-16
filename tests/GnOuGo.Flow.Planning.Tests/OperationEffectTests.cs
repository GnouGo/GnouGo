using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic effect-grounding decisions, kept separate from historical identity receipts.</summary>
public sealed class OperationEffectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("No unresolved identity is exposed.") };
    private static PlanningRuntimeEvidence Add(PlanningSnapshot state, int clause, string kind = "local_processing", string role = "action", string? resourceAction = null, bool independent = true) =>
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state)[clause].Clause, kind, evidenceRole: role, resourceAction: resourceAction, independentBoundary: independent);
    private static string Own(PlanningSnapshot state, PlanningRuntimeEvidence evidence) => PlanningOperations.EffectDomain(state, evidence)
        .Single(p => p.Value.WorkflowScope == "main" && p.Value.BoundaryReference == evidence.ActionReference).Key;

    [Fact]
    public async Task EffectGroundingReplacesBothProseIdentityDecisions()
    {
        var state = OperationAdmissionTests.State("Transform the supplied value. Apply the detailed transformation rules. This transformation is deterministic.");
        var first = Add(state, 0); Add(state, 1, independent: false); var description = Add(state, 2, independent: false);
        var target = Own(state, first);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("intent_operations", phase);
            var response = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(field =>
            {
                if (field.Key.StartsWith("contribution_", StringComparison.Ordinal))
                {
                    var scope = PlanningOperations.Scopes(state).Single(s => PlanningOperations.ContributionDecisionId(s.Evidence!) == field.Key);
                    return new KeyValuePair<string, JsonNode?>(field.Key, OperationEffectFixtures.ContributionAnswer(state, scope,
                        OperationEffectFixtures.Answer(state, scope, [target], scope.Evidence!.Id == description.Id ? "governs" : "realizes")));
                }
                if (field.Key.StartsWith("applicability_", StringComparison.Ordinal))
                    return new KeyValuePair<string, JsonNode?>(field.Key, OperationEffectFixtures.ApplicabilityAnswer(
                        PlanningOperations.ApplicabilityDecisions(state).Single(d => d.Id == field.Key), [target]));
                Assert.StartsWith("coverage_", field.Key);
                var group = PlanningOperations.CoverageGroups(state).Single(g => g.Id == field.Key);
                return new KeyValuePair<string, JsonNode?>(field.Key, OperationEffectFixtures.CoverageAnswer(state, group,
                    scope => OperationEffectFixtures.Answer(state, scope, [target], scope.Evidence!.Id == description.Id ? "governs" : "realizes")));
            }));
            return Task.FromResult(new LLMResponse { Json = response, CompletionStatus = "completed" });
        } };
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal(target, operation.Id); Assert.True(operation.Required);
        Assert.Equal(3, operation.OperationAdmission!.Assignments.Count);
        Assert.All(operation.OperationAdmission.Assignments, a => Assert.Equal("deterministic", a.ResolutionOrigin));
        Assert.Contains(operation.OperationAdmission.Assignments, a => a.RuntimeEvidenceId == description.Id && a.Disposition == "attach");
        Assert.All(state.DecisionPages, p => Assert.All(p.Decisions, id => Assert.True(id.StartsWith("coverage_", StringComparison.Ordinal) || id.StartsWith("contribution_", StringComparison.Ordinal) || id.StartsWith("applicability_", StringComparison.Ordinal))));
        Assert.Empty(state.RepairAllowances);
    }

    [Fact]
    public async Task SameOwnedActionWithContradictoryFactsStopsBeforeGrounding()
    {
        var state = OperationAdmissionTests.State("Perform the requested action.");
        Add(state, 0); Add(state, 0, "external_read");
        var failure = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", failure.Code); Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public async Task SharedPublicEndpointsDoNotMergeSeparatelyEvidencedInvocations()
    {
        var state = OperationAdmissionTests.State("First transform the value. Independently transform it again.");
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var clauses = PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").ToArray();
        foreach (var clause in clauses) PlanningFixtures.Runtime(state, clause.Clause);
        var output = Assert.Single(state.Declarations, d => d.Direction == "output");
        foreach (var scope in PlanningOperations.Scopes(state))
            Assert.Equal("invocation", Assert.Single(PlanningOperations.EffectDomain(state, scope.Evidence!)).Value.BoundaryKind);
        OperationEffectFixtures.Seed(state, scope => OperationEffectFixtures.Answer(state, scope, outputs: [output.Id]));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.All(state.Obligations.Where(PlanningSourceDecisions.IsOperation), o =>
            Assert.Contains(output.Id, o.OperationAdmission!.Assignments[0].Effect!.Outputs));
    }

    [Fact]
    public async Task RequestedResultCanReuseExistingPublicContractWithoutBorrowingBaselineNodeAuthority()
    {
        var state = OperationAdmissionTests.State("Produce the requested result.");
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var clause = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        var evidence = PlanningFixtures.Runtime(state, clause.Clause, independentBoundary: false);
        var result = PlanningOperations.EffectDomain(state, evidence).Single(p => p.Value.BoundaryKind == "result_realization");
        OperationEffectFixtures.Seed(state, scope => OperationEffectFixtures.Answer(state, scope, [result.Key]));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal(result.Key, operation.Id); Assert.Null(operation.OperationAdmission!.BaselineReference);
        Assert.NotNull(Assert.Single(state.Declarations).BaselineReference);
    }

    [Fact]
    public void SingletonDomainNeverExposesAnIdentityDecisionAndContractEvidenceCannotChangeItsId()
    {
        var state = OperationAdmissionTests.State("Transform a value. Apply these conditions."); var action = Add(state, 0);
        var scope = PlanningOperations.Scopes(state).Single(); var id = Own(state, action);
        var proof = PlanningOperations.ParseEffect(state, scope, OperationEffectFixtures.Answer(state, scope, [id]));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.IdentityDecision(state, scope, proof, [id]));
        Add(state, 1, role: "governing");
        var optional = PlanningOperations.SealRuntime(state, action with { Necessity = PlanningOperationNecessity.Optional, NecessityReference = action.ActionReference });
        state.RuntimeEvidence.Remove(action); state.RuntimeEvidence.Add(optional); PlanningFixtures.EmptyRuntime(state);
        Assert.Equal(id, Own(state, optional));
    }

    [Fact]
    public async Task IndependentInvocationsRemainDistinctEvenWhenKindsAgree()
    {
        var state = OperationAdmissionTests.State("Transform the first value. Independently transform the second value.");
        Add(state, 0); Add(state, 1); OperationEffectFixtures.Seed(state);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public async Task NoProvenIdentityStopsWithoutAStandaloneIdentityRequest()
    {
        var state = OperationAdmissionTests.State("This transformation is deterministic."); Add(state, 0, role: "governing");
        OperationEffectFixtures.SeedContributions(state);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, NoModel(), Ct));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", error.Code); Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public async Task GoverningEvidenceSelectsOnlyIssuedProvenIdentities()
    {
        var state = OperationAdmissionTests.State("Transform the first value. Transform the second value. This condition governs one transformation.");
        var first = Add(state, 0); var second = Add(state, 1); Add(state, 2, role: "governing");
        OperationEffectFixtures.Seed(state, rootsOnly: true);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            if (request.StructuredOutputSchema!["properties"]!.AsObject().All(p => p.Key.StartsWith("data_", StringComparison.Ordinal)))
                return Task.FromResult(new LLMResponse { Json = OperationEffectFixtures.Response(state, request), CompletionStatus = "completed" });
            var field = request.StructuredOutputSchema!["properties"]!.AsObject().Single();
            var decision = PlanningOperations.ApplicabilityDecisions(state).Single(d => d.Id == field.Key);
            Assert.Equal(new[] { Own(state, first), Own(state, second) }.Order(StringComparer.Ordinal), decision.Context["realized"]!.AsObject().Select(p => p.Key));
            var answer = OperationEffectFixtures.ApplicabilityAnswer(decision, [Own(state, first)]);
            var foreign = answer.DeepClone().AsObject(); foreign["bindings"]![0]!["target"] = first.ClauseReference;
            Assert.NotEmpty(PlanningContractValidation.ValidateInstance(foreign, field.Value!.AsObject()));
            return Task.FromResult(new LLMResponse { Json = new JsonObject { [field.Key] = answer }, CompletionStatus = "completed" });
        } };
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.DoesNotContain(runtime.Requests, r => r.StructuredOutputSchema!["properties"]!.AsObject().Any(p => p.Key.StartsWith("effect_governing_", StringComparison.Ordinal))); Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.Equal(1, state.Obligations.Count(o => o.OperationAdmission!.Assignments.Count == 2));
    }

    [Fact]
    public async Task SharedRuleAttachesToEveryProvenTargetWithoutAnIdentityDecision()
    {
        var state = OperationAdmissionTests.State("Transform the first value. Transform the second value. Apply the same constraint to both transformations.");
        var first = Add(state, 0); var second = Add(state, 1); Add(state, 2, role: "governing");
        OperationEffectFixtures.Seed(state, scope => scope.Evidence!.EvidenceRole == "governing"
            ? OperationEffectFixtures.Answer(state, scope, [Own(state, first), Own(state, second)], "shared_rule") : OperationEffectFixtures.Answer(state, scope));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.All(state.Obligations, o => Assert.Equal(2, o.OperationAdmission!.Assignments.Count));
    }

    [Theory]
    [InlineData("external_read", null)]
    [InlineData("external_write", null)]
    [InlineData("human_interaction", null)]
    [InlineData("resource_lifecycle", "create")]
    [InlineData("cleanup", "delete")]
    public async Task RepeatedExternalInteractionAndResourceOccurrencesAreNotMerged(string kind, string? action)
    {
        var state = OperationAdmissionTests.State("Perform the first requested effect. Perform another independent effect.");
        Add(state, 0, kind, resourceAction: action); Add(state, 1, kind, resourceAction: action);
        OperationEffectFixtures.Seed(state); await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
    }

    [Fact]
    public async Task ReadProducerIsProjectedWithoutReaskingTheDataflowRelationship()
    {
        var state = OperationAdmissionTests.State("Read the value. Transform the read result.");
        var read = Add(state, 0, "external_read"); var local = Add(state, 1);
        OperationEffectFixtures.Seed(state, dependency: (producer, consumer) => producer == Own(state, read) && consumer == Own(state, local));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            var established = request.StructuredOutputSchema!["properties"]!["relation_" + Own(state, read) + "_" + Own(state, local)]!;
            Assert.DoesNotContain("data", established["enum"]!.AsArray().Select(v => v!.ToString()));
            // Data proof must not suppress independent permission/failure evidence.
            Assert.Contains("decision", established["enum"]!.AsArray().Select(v => v!.ToString()));
            return Task.FromResult(new LLMResponse { Json = new JsonObject(request.StructuredOutputSchema["properties"]!.AsObject().Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("none")))) });
        } };
        await PlanningSourceDecisions.RelateAsync(state, runtime, Ct);
        Assert.Contains(new PlanningObligationRelation(Own(state, read), Own(state, local), "data"), state.ObligationRelations);
    }

    [Fact]
    public async Task RestartReusesEffectReceiptsBeforeAtomicOperationCommit()
    {
        var state = OperationAdmissionTests.State("Transform one value. This transformation is deterministic.");
        var first = Add(state, 0); Add(state, 1, role: "governing");
        PlanningSnapshot? checkpoint = null;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { CompletionStatus = "completed", Json = OperationEffectFixtures.Response(state, request) }) };
        runtime.OnCheckpoint = snapshot =>
        {
            if (checkpoint is null && snapshot.DecisionPages.Any(p => p.Status == "completed" && p.Decisions.Any(id => id.StartsWith("applicability_", StringComparison.Ordinal))))
            { checkpoint = PlanningContext.Clone(snapshot); throw new OperationCanceledException("Synthetic crash after verified receipt."); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(checkpoint); Assert.DoesNotContain(checkpoint.Obligations, PlanningSourceDecisions.IsOperation);
        var accounting = checkpoint.RequestAccounting.Count;
        await PlanningOperations.ResolveAsync(checkpoint, NoModel(), Ct);
        var restored = PlanningContext.Clone(checkpoint); await PlanningOperations.ResolveAsync(restored, NoModel(), Ct);
        Assert.Equal(accounting, restored.RequestAccounting.Count); Assert.Equal(checkpoint.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
        Assert.Empty(restored.RepairAllowances);
    }

    [Theory]
    [InlineData("input")]
    [InlineData("boundary")]
    [InlineData("page")]
    [InlineData("version")]
    [InlineData("occurrence")]
    public async Task RestoredEffectProofCannotBeAltered(string defect)
    {
        var state = OperationAdmissionTests.State("Transform the supplied value."); Add(state, 0); OperationEffectFixtures.Seed(state);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var proof = state.Obligations.Single().OperationAdmission!;
        if (defect == "input") proof.Assignments[0].Effect!.Inputs.Add("foreign");
        if (defect == "boundary") proof.Assignments[0].Effect!.Candidates[0] = proof.Assignments[0].Effect!.Candidates[0] with { BoundaryReference = "foreign" };
        if (defect == "page") state.DecisionPages.Clear();
        if (defect == "version") proof.Assignments[0] = proof.Assignments[0] with { Effect = proof.Assignments[0].Effect! with { Version = 0 } };
        if (defect == "occurrence") proof.Assignments[0].Effect!.Candidates[0] = proof.Assignments[0].Effect!.Candidates[0] with
        { OccurrenceProof = proof.Assignments[0].Effect!.Candidates[0].OccurrenceProof! with { Version = 0 } };
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(state));
    }

    [Fact]
    public async Task CapturedClassifierEffectRetainsCanonicalPortsAndEliminatesStandaloneIdentityCalls()
    {
        var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "operation-admission-stage1.json"), Ct), PlanningJsonContext.Default.PlanningSnapshot)!;
        PlanningFixtures.ReassessSyntheticSources(state); // Explicit synthetic current-proof fixture, not receipt replay.
        PlanningFixtures.EmptyRuntime(state);
        var fragments = new[] { ("classifying a single record.", "action"),
            ("Classify as rejected when approved is false, high when approved is true and amount>=threshold, and standard otherwise.", "action"),
            ("when approved is false,", "governing"), ("when approved is true and amount>=threshold,", "governing"),
            ("standard otherwise.", "governing"), ("This is deterministic, local, in-memory business processing.", "action"),
            ("Preserve the original id and amount.", "action") };
        string? descriptive = null;
        foreach (var (text, role) in fragments)
        {
            var start = state.Request.Prompt.IndexOf(text, StringComparison.Ordinal); Assert.True(start >= 0);
            var clause = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request" && s.Clause.Start <= start && s.Clause.Start + s.Clause.Length >= start + text.Length).Clause;
            var reference = clause with { Id = "synthetic_effect_" + start, Start = start, Length = text.Length }; state.References.Add(reference);
            var evidence = PlanningFixtures.Runtime(state, reference, evidenceRole: role, independentBoundary: false);
            if (text.StartsWith("This is", StringComparison.Ordinal)) descriptive = evidence.Id;
        }
        PlanningDeclarations.Commit(state, state.DeclarationAssignments, PlanningDeclarations.EvidenceFingerprint(state));
        var declarationProof = state.DeclarationFingerprint;
        var inputIds = state.Declarations.Where(d => d.Direction == "input").Select(d => d.Id).ToArray();
        var output = Assert.Single(state.Declarations, d => d.Direction == "output");
        OperationEffectFixtures.Seed(state, scope => OperationEffectFixtures.Answer(state, scope,
            [PlanningOperations.EffectDomain(state, scope.Evidence!).Single(p => p.Value.OwnerReference == output.Id).Key],
            scope.Evidence!.Id == descriptive || scope.Evidence.EvidenceRole == "governing" ? "governs" : "realizes", inputIds));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.True(operation.Required); Assert.Equal("local_processing", operation.Kind);
        Assert.Equal(4, operation.OperationAdmission!.Assignments.Count(a => a.Disposition == "attach"));
        Assert.Equal(inputIds.Order(), operation.OperationAdmission.Assignments.SelectMany(a => a.Effect!.Inputs).Distinct().Order());
        Assert.All(operation.OperationAdmission.Assignments.Where(a => a.Effect!.Contribution == "realizes"), a => Assert.Contains(output.Id, a.Effect!.Outputs));
        Assert.Single(PlanningOperations.DeclarationExclusions(state)); Assert.Equal(declarationProof, state.DeclarationFingerprint);
        Assert.Equal(["record", "threshold"], state.Declarations.Where(d => d.Direction == "input").Select(d => PlanningDeclarations.Name(state, d)).Order());
        Assert.Equal("classifiedResult", PlanningDeclarations.Name(state, output));
        var threshold = state.Declarations.Single(d => PlanningDeclarations.Name(state, d) == "threshold");
        Assert.False(threshold.Required); Assert.Equal(100m, PlanningDeclarations.Default(state, threshold)!.Number);
        await PlanningSourceDecisions.RelateAsync(state, NoModel(), Ct);
        Assert.Equal(2, state.ObligationRelations.Count); Assert.Empty(state.RequestAccounting);
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), id => id.StartsWith("operation_", StringComparison.Ordinal));
    }
}
