using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Explicit synthetic occurrence proofs; no historical response is reinterpreted.</summary>
public sealed class OccurrenceBoundaryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("No identity decision permitted.") };
    private static PlanningSnapshot ResultState()
    {
        var state = OperationAdmissionTests.State("Transform the supplied value. Apply its detailed rules. This transformation is deterministic. Create another workflow.");
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        PolicyGroundingTests.Add(state, "request", "Create another workflow.", "other", "workflow_boundary");
        foreach (var scope in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").Take(3))
            PlanningFixtures.Runtime(state, scope.Clause, independentBoundary: false);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        return state;
    }

    [Fact]
    public async Task ActionProseRulesAndDescriptionsDoNotIssueInvocationCopies()
    {
        var state = ResultState(); var scopes = PlanningOperations.Scopes(state);
        foreach (var scope in scopes)
        {
            var anchor = Assert.Single(PlanningOperations.EffectDomain(state, scope.Evidence!));
            Assert.Equal("result_realization", anchor.Value.BoundaryKind); Assert.Equal("main", anchor.Value.WorkflowScope);
            Assert.DoesNotContain("invocation", PlanningOperations.EffectDecision(state, scope).Schema.ToJsonString());
        }
        var target = PlanningOperations.EffectDomain(state, scopes[0].Evidence!).Single().Key;
        OperationEffectFixtures.Seed(state, s => OperationEffectFixtures.Answer(state, s, [target],
            s.Evidence!.Id == scopes[0].Evidence!.Id ? "realizes" : "governs"));
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal(2, operation.OperationAdmission!.Assignments.Count(a => a.Disposition == "attach"));
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), id => id.StartsWith("operation_", StringComparison.Ordinal) || id.StartsWith("boundary_", StringComparison.Ordinal));
        Assert.Contains(state.Obligations, o => o.Id == "other");
    }

    [Fact]
    public async Task ActionWithoutResultOrIndependentBoundaryRemainsUnresolved()
    {
        var state = OperationAdmissionTests.State("Describe the requested transformation.");
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state)[0].Clause, independentBoundary: false);
        var scope = Assert.Single(PlanningOperations.Scopes(state)); Assert.Empty(PlanningOperations.EffectDomain(state, scope.Evidence!));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
            p.Key.StartsWith("execution_request_", StringComparison.Ordinal)
                ? new JsonObject(PlanningOperations.RequestCohorts(state).Single(c => c.Id == p.Key).Scopes.Select(s =>
                    new KeyValuePair<string, JsonNode?>(s.Clause.Id, new JsonObject { ["s"] = "unresolved" })))
                : new JsonObject { ["status"] = "unresolved" }))) }) };
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", (await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct))).Code);
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), id => id.StartsWith("operation_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("invocation")]
    [InlineData("intermediate_result")]
    [InlineData("iteration")]
    [InlineData("call")]
    public async Task OutputlessIndependentBoundariesRemainExecutableWithoutCartesianIterations(string kind)
    {
        var state = OperationAdmissionTests.State("Execute the independently requested stage. Repeat unrelated work.");
        var scopes = PlanningOperations.SourceScopes(state);
        var evidence = PlanningFixtures.Runtime(state, scopes[0].Clause);
        var replacement = PlanningOperations.SealRuntime(state, evidence with { OccurrenceBoundary = evidence.OccurrenceBoundary! with { Kind = kind } });
        state.RuntimeEvidence[state.RuntimeEvidence.IndexOf(evidence)] = replacement;
        PolicyGroundingTests.Add(state, "request", "Repeat unrelated work.", "unrelated_iteration", "iteration");
        PlanningFixtures.EmptyRuntime(state);
        var anchor = Assert.Single(PlanningOperations.EffectDomain(state, replacement)).Value;
        Assert.Equal(kind, anchor.BoundaryKind); Assert.Equal(1, anchor.OccurrenceProof!.Version);
        Assert.Equal(kind == "iteration" ? replacement.OccurrenceBoundary!.BoundaryReference : null, anchor.IterationReference);
        OperationEffectFixtures.Seed(state); await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        var restored = PlanningContext.Clone(state); PlanningOperations.RequireCurrent(restored);
        Assert.Equal(state.OperationAdmissionFingerprint, restored.OperationAdmissionFingerprint);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("kind")]
    public void UnownedOrIncompatibleBoundaryEvidenceCannotIssueIdentities(string fault)
    {
        var state = OperationAdmissionTests.State("Execute one independent stage.");
        var evidence = PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state)[0].Clause);
        var invalid = fault switch
        {
            "foreign" => evidence with { OccurrenceBoundary = evidence.OccurrenceBoundary! with { OwnerReference = "foreign" } },
            "governing" => evidence with { EvidenceRole = "governing" },
            _ => evidence with { OccurrenceBoundary = evidence.OccurrenceBoundary! with { Kind = "resource_transition" } }
        };
        Assert.ThrowsAny<Exception>(() => PlanningOperations.ValidateRuntime(state, PlanningOperations.SealRuntime(state, invalid)));
    }

    [Fact]
    public void BoundaryProofAndIdentityAreStableAcrossEvidenceEnumeration()
    {
        var state = OperationAdmissionTests.State("Run the first independent stage. Run the second independent stage.");
        foreach (var scope in PlanningOperations.SourceScopes(state)) PlanningFixtures.Runtime(state, scope.Clause);
        string Proofs(PlanningSnapshot s) => string.Join('|', PlanningOperations.Scopes(s).SelectMany(scope => PlanningOperations.EffectDomain(s, scope.Evidence!))
            .DistinctBy(p => p.Key).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + ":" + JsonSerializer.Serialize(p.Value, PlanningJsonContext.Default.PlanningOperationEffectAnchor)));
        var before = Proofs(state); state.RuntimeEvidence.Reverse(); state.References.Reverse();
        Assert.Equal(before, Proofs(state)); Assert.Equal(before, Proofs(PlanningContext.Clone(state)));
    }

    [Fact]
    public async Task RestartReusesTheCompletedBoundaryScopeReceiptBeforeRealization()
    {
        var state = OperationAdmissionTests.State("Execute an independent stage. Declare another workflow.");
        PolicyGroundingTests.Add(state, "request", "Declare another workflow.", "child", "workflow_boundary");
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state)[0].Clause);
        PlanningSnapshot? saved = null;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { CompletionStatus = "completed", Json = OperationEffectFixtures.Response(state, request) }) };
        runtime.OnCheckpoint = snapshot =>
        {
            if (saved is null && snapshot.DecisionPages.Any(p => p.Status == "completed" && p.Decisions.Any(id => id.StartsWith("boundary_", StringComparison.Ordinal))))
            { saved = PlanningContext.Clone(snapshot); throw new OperationCanceledException("Synthetic crash after durable boundary receipt."); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(saved); Assert.DoesNotContain(saved.Obligations, PlanningSourceDecisions.IsOperation);
        var boundaryPage = Assert.Single(saved.DecisionPages, p => p.Decisions.Any(id => id.StartsWith("boundary_", StringComparison.Ordinal)));
        var resumed = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            Assert.DoesNotContain(request.StructuredOutputSchema!["properties"]!.AsObject(), p => p.Key.StartsWith("boundary_", StringComparison.Ordinal));
            return Task.FromResult(new LLMResponse { CompletionStatus = "completed", Json = OperationEffectFixtures.Response(saved, request) });
        } };
        await PlanningOperations.ResolveAsync(saved, resumed, Ct);
        var clone = PlanningContext.Clone(saved); await PlanningOperations.ResolveAsync(clone, NoModel(), Ct);
        Assert.Equal(saved.OperationAdmissionFingerprint, clone.OperationAdmissionFingerprint);
        Assert.Equal(boundaryPage.RequestId, clone.DecisionPages.Single(p => p.Id == boundaryPage.Id).RequestId);
        Assert.Equal(saved.RequestAccounting.Count, clone.RequestAccounting.Count); Assert.Empty(clone.RepairAllowances);
    }
}
