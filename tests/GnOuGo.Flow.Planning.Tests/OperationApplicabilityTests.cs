using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class OperationApplicabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoCalls() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("Unexpected provider call") };
    private static PlanningSnapshot Result(bool wrongKind = false)
    {
        var state = OperationAdmissionTests.State("Transform the supplied value. This execution uses deterministic in-memory processing.");
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var clauses = PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").ToArray();
        PlanningFixtures.Runtime(state, clauses[0].Clause, independentBoundary: false);
        PlanningFixtures.Runtime(state, clauses[1].Clause, wrongKind ? "external_execute" : "local_processing", independentBoundary: false);
        return state;
    }
    private static void Qualify(PlanningSnapshot state, Func<PlanningOperations.Scope, JsonObject> answer)
    {
        OperationEffectFixtures.SeedBoundaries(state);
        var values = OperationEffectFixtures.QualificationAnswers(state, answer);
        var properties = OperationEffectFixtures.SeparateRequestAuthority(state, values);
        OperationEffectFixtures.SeedPages(state, PlanningOperations.ContributionDecisions(state), properties);
    }
    private static JsonObject Property(PlanningOperations.Scope scope) => new()
    { ["status"] = "qualified", ["units"] = new JsonArray((JsonNode)new JsonObject
        { ["role"] = "governing_property", ["governingKind"] = "descriptive_property", ["scope"] = scope.Clause.Id, ["evidence"] = scope.Evidence!.ActionReference }) };
    private static void Cover(PlanningSnapshot state)
    {
        var groups = PlanningOperations.CoverageGroups(state);
        OperationEffectFixtures.SeedPages(state, groups.Select(g => g.Decision).ToArray(), new JsonObject(groups.Select(g =>
            new KeyValuePair<string, JsonNode?>(g.Id, OperationEffectFixtures.CoverageAnswer(state, g)))));
    }
    private static void Prepare(PlanningSnapshot state)
    {
        var first = PlanningOperations.Scopes(state).First().Evidence!.Id;
        Qualify(state, s => s.Evidence!.Id == first ? OperationEffectFixtures.ContributionAnswer(state, s) : Property(s));
        Cover(state);
    }

    [Fact]
    public async Task WrongExternalHintCannotSupportButCannotVetoLocalPropertyApplicability()
    {
        var state = Result(true); var property = PlanningOperations.Scopes(state).Last();
        var qualification = OperationEffectFixtures.PropertyDecision(state, property);
        Assert.Empty(PlanningOperations.EffectDomain(state, property.Evidence!));
        Assert.Empty(PlanningContractValidation.ValidateInstance(Property(property), qualification.Schema));
        Assert.DoesNotContain("requested_result_production", qualification.Schema.ToJsonString());
        Prepare(state);
        Assert.All(PlanningOperations.ReadCoverage(state).SelectMany(p => p.Contributions), c => Assert.Equal("supports", c.Disposition));
        var decision = Assert.Single(PlanningOperations.ApplicabilityDecisions(state));
        Assert.Single(decision.Context["realized"]!.AsObject()); // Singleton is still a semantic question.
        Assert.DoesNotContain("external_execute", decision.Context.ToJsonString());
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
            { Json = OperationEffectFixtures.Response(state, request), CompletionStatus = "completed" }) };
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        var operation = Assert.Single(state.Obligations, PlanningSourceDecisions.IsOperation);
        Assert.Equal("local_processing", operation.Kind); Assert.True(operation.Required);
        Assert.Single(operation.OperationAdmission!.Assignments, a => a.Disposition == "supports");
        var attachment = Assert.Single(operation.OperationAdmission.Assignments, a => a.Disposition == "attach");
        Assert.Equal(property.Evidence!.Id, attachment.RuntimeEvidenceId); Assert.Equal("local_processing", attachment.Kind);
        Assert.Equal("external_execute", state.RuntimeEvidence.Single(e => e.Id == attachment.RuntimeEvidenceId).Kind);
        Assert.Equal(PlanningApplicabilityOrigin.ModelApplicability, Assert.Single(operation.OperationAdmission.GoverningApplicability).Origin);
        Assert.Single(runtime.Requests);
        Assert.Equal(0, state.Events.Single(e => e.Kind == "operation_identity_model").Count);
        var serialized = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var reload = JsonSerializer.Deserialize(serialized, PlanningJsonContext.Default.PlanningSnapshot)!;
        var replay = NoCalls(); replay.OnCheckpoint = _ => throw new InvalidOperationException("Unexpected checkpoint write");
        await PlanningOperations.ResolveAsync(reload, replay, Ct);
        Assert.Equal(serialized, JsonSerializer.Serialize(reload, PlanningJsonContext.Default.PlanningSnapshot));
    }

    [Fact]
    public void NoExecutionDoesNotImplyNoOperationRelevance()
    {
        var state = Result(true); var scope = PlanningOperations.Scopes(state).Last();
        var decision = OperationEffectFixtures.PropertyDecision(state, scope);
        var exclusion = Property(scope); exclusion["units"]![0]!["role"] = "excluded";
        exclusion["units"]![0]!.AsObject().Remove("governingKind");
        exclusion["units"]![0]!["basis"] = "no_requested_execution";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(exclusion, decision.Schema));
        exclusion["units"]![0]!["basis"] = "no_operation_relevance";
        Assert.Empty(PlanningContractValidation.ValidateInstance(exclusion, decision.Schema));
        Assert.Equal("excluded", Assert.Single(OperationEffectFixtures.ParseQualification(state, scope, exclusion).Contributions).Role);
        var bound = Property(scope); bound["units"]![0]!["effect"] = "possible";
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(bound, decision.Schema));
    }

    [Theory]
    [InlineData("foreign_target")]
    [InlineData("foreign_reference")]
    [InlineData("duplicate_target")]
    [InlineData("unresolved")]
    public async Task InvalidApplicabilityStopsBeforeAtomicAdmission(string defect)
    {
        var state = Result(true); Prepare(state);
        var decision = Assert.Single(PlanningOperations.ApplicabilityDecisions(state));
        var answer = OperationEffectFixtures.ApplicabilityAnswer(decision);
        if (defect == "foreign_target") answer["bindings"]![0]!["target"] = "foreign";
        if (defect == "foreign_reference") answer["bindings"]![0]!["evidence"] = new JsonArray("foreign");
        if (defect == "duplicate_target") answer["bindings"]!.AsArray().Add(answer["bindings"]![0]!.DeepClone());
        if (defect == "unresolved") answer = new() { ["status"] = "unresolved" };
        OperationEffectFixtures.SeedPages(state, [decision], new() { [decision.Id] = OperationEffectFixtures.ApplicabilityAnswer(decision) });
        state.DecisionPages.Single(p => p.Decisions.Contains(decision.Id)).Candidate![decision.Id] = answer;
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ReadApplicability(state));
        var error = await Record.ExceptionAsync(() => PlanningOperations.ResolveAsync(state, NoCalls(), Ct));
        Assert.True(error is WorkflowRuntimeException or PlanningConflictException);
        Assert.Null(state.OperationAdmissionFingerprint); Assert.DoesNotContain(state.Obligations, PlanningSourceDecisions.IsOperation);
    }

    [Fact]
    public void SameClauseParentAndWorkflowDoNotProvePropertyApplicability()
    {
        var state = OperationAdmissionTests.State("Transform the value deterministically.");
        state.Request.Baseline = TypedPlannerTests.Graph(); state.Request.Baseline.Workflows[0].Steps.Clear();
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request").Clause, independentBoundary: false);
        var scope = Assert.Single(PlanningOperations.Scopes(state));
        var answer = OperationEffectFixtures.SplitProperty(state, scope, 3);
        Qualify(state, _ => answer); Cover(state);
        Assert.Single(PlanningOperations.ApplicabilityDecisions(state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedPropertyHasPerTargetEvidenceAndNoExecutionAuthority(bool shared)
    {
        var state = OperationAdmissionTests.State("Read the input once. Independently transform it. This property concerns execution.");
        var clauses = PlanningOperations.SourceScopes(state);
        PlanningFixtures.Runtime(state, clauses[0].Clause, "external_read");
        PlanningFixtures.Runtime(state, clauses[1].Clause);
        var property = PlanningFixtures.Runtime(state, clauses[2].Clause, "human_interaction", independentBoundary: false);
        Qualify(state, s => s.Evidence!.Id == property.Id ? Property(s) : OperationEffectFixtures.ContributionAnswer(state, s)); Cover(state);
        var decision = Assert.Single(PlanningOperations.ApplicabilityDecisions(state));
        Assert.Equal(2, decision.Context["realized"]!.AsObject().Count);
        var operations = PlanningOperations.MaterializeCoverage(state);
        var targets = operations.Where(o => shared || o.Kind == "external_read").Select(o => o.Id).ToArray();
        OperationEffectFixtures.SeedPages(state, [decision], new() { [decision.Id] = OperationEffectFixtures.ApplicabilityAnswer(decision, targets) });
        OperationEffectFixtures.SeedDependencies(state, OperationEffectFixtures.Staged(state));
        await PlanningOperations.ResolveAsync(state, NoCalls(), Ct);
        Assert.Equal(shared ? 2 : 1, state.Obligations.Count(o => o.OperationAdmission!.Assignments.Any(a => a.Disposition == "attach")));
        Assert.All(state.Obligations, o => Assert.Single(o.OperationAdmission!.Assignments, a => a.Disposition == "supports"));
        Assert.DoesNotContain(state.Obligations, o => o.Kind == "human_interaction");
    }

    [Fact]
    public async Task RestartAndPackingReuseApplicabilityWithoutChangingFingerprints()
    {
        var state = Result(true); Prepare(state);
        var reverse = PlanningContext.Clone(state); reverse.RuntimeEvidence.Reverse(); reverse.References.Reverse();
        Assert.Equal(PlanningOperations.ApplicabilityDecisions(state).Select(d => d.EvidenceFingerprint),
            PlanningOperations.ApplicabilityDecisions(reverse).Select(d => d.EvidenceFingerprint));
        foreach (var snapshot in new[] { state, reverse })
        {
            OperationEffectFixtures.SeedApplicability(snapshot);
            await PlanningOperations.ResolveAsync(snapshot, NoCalls(), Ct);
        }
        Assert.Equal(state.OperationAdmissionFingerprint, reverse.OperationAdmissionFingerprint);
        var operation = Assert.Single(reverse.Obligations, PlanningSourceDecisions.IsOperation);
        var proof = operation.OperationAdmission!;
        reverse.Obligations[reverse.Obligations.IndexOf(operation)] = PlanningOperations.Prove(reverse, operation,
            proof with { GoverningApplicability = [proof.GoverningApplicability[0] with { RealizedSetFingerprint = "stale" }] });
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.RequireCurrent(reverse));
    }

    [Fact]
    public async Task DownstreamPolicyApplicabilityIsReusedButUnrelatedResponsibilitiesRemain()
    {
        var state = Result(); var scope = PlanningOperations.Scopes(state).Last();
        var policy = new PlanningObligation("owned-policy", [scope.Evidence!.ActionReference!], "workflow", "implementation_policy", true);
        state.Obligations.Add(policy with { Grounding = PlanningSourceGroundingRules.Create(state, policy) });
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        Prepare(state); OperationEffectFixtures.SeedApplicability(state);
        await PlanningOperations.ResolveAsync(state, NoCalls(), Ct);
        await PlanningSourceDecisions.RelateAsync(state, NoCalls(), Ct);
        Assert.Contains(state.ObligationRelations, r => r.Producer == "owned-policy" && r.Role == "policy");
        Assert.DoesNotContain(state.DecisionPages.SelectMany(p => p.Decisions), d => d.StartsWith("relation_", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactOwnedPropertyHasInactiveOmissionOrRevisionProofWithoutResurrection(bool revision)
    {
        var state = OperationAdmissionTests.State("Optionally perform this separate invocation deterministically.");
        if (revision) state.BehaviorRevision = new() { Text = "Remove that invocation." };
        PlanningFixtures.Runtime(state, PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request").Clause, required: false);
        Qualify(state, scope =>
        {
            var answer = OperationEffectFixtures.SplitProperty(state, scope, 5);
            return answer;
        });
        var group = Assert.Single(PlanningOperations.CoverageGroups(state));
        OperationEffectFixtures.SeedPages(state, [group.Decision], new() { [group.Id] = OperationEffectFixtures.CoverageAnswer(state, group,
            revision ? null : _ => new JsonObject { ["status"] = "omitted" }) });
        Assert.Empty(PlanningOperations.ApplicabilityDecisions(state));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (phase, request, _) =>
        {
            Assert.Equal("behavior_revision_obligations", phase);
            return Task.FromResult(new LLMResponse { CompletionStatus = "completed", Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject()
                .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value!["enum"]![1]!.DeepClone()))) });
        } };
        await PlanningOperations.ResolveAsync(state, runtime, Ct);
        Assert.Empty(state.Obligations); Assert.NotNull(state.OperationAdmissionFingerprint);
        var proof = Assert.Single(PlanningOperations.ReadApplicability(state));
        if (!revision) { Assert.Equal("inactive", proof.Outcome); Assert.NotNull(proof.InactiveProofFingerprint); }
        var serialized = JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot);
        var restored = JsonSerializer.Deserialize(serialized, PlanningJsonContext.Default.PlanningSnapshot)!;
        await PlanningOperations.ResolveAsync(restored, NoCalls(), Ct);
        Assert.Equal(serialized, JsonSerializer.Serialize(restored, PlanningJsonContext.Default.PlanningSnapshot));
    }

}
