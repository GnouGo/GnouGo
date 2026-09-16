using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

/// <summary>Synthetic scope assignments; historical responses retain their original schemas.</summary>
public sealed class OperationEffectScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static TypedPlannerTests.FakeRuntime NoModel() => new() { OnCall = (_, _, _) => throw new InvalidOperationException("No new request expected.") };

    private static PlanningSnapshot State(bool sameScope = false)
    {
        var state = OperationAdmissionTests.State("Transform the supplied value. Independently transform another value. Run a separate workflow. Apply one of the requested effects.");
        state.Request.Baseline = new() { Workflows = [new() { Key = "main",
            Inputs = [new() { Name = "value", Required = true, Schema = new() { Type = "string" } }],
            Outputs = [new() { Name = "result", Schema = new() { Type = "string" } }] }] };
        PolicyGroundingTests.Add(state, "request", "Run a separate workflow.", "other_scope", "workflow_boundary");
        foreach (var source in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").Take(2))
            PlanningFixtures.Runtime(state, source.Clause);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var roots = PlanningOperations.Scopes(state);
        OperationEffectFixtures.SeedBoundaries(state, s => sameScope || s.Evidence!.Id == roots[0].Evidence!.Id ? "main" : "other_scope");
        return state;
    }

    private static string Invocation(PlanningSnapshot state, PlanningOperations.Scope scope, string workflow) =>
        PlanningOperations.Scopes(state).SelectMany(s => PlanningOperations.EffectDomain(state, s.Evidence!)).Where(p => p.Value.WorkflowScope == workflow && p.Value.OccurrenceProof is not null)
            .OrderBy(p => p.Value.BoundaryReference != scope.Evidence!.ActionReference).First().Key;

    [Fact]
    public void SameScopeValuesAreValidAndIndependentOutputlessScopeRemainsAvailable()
    {
        var state = State(); var scope = PlanningOperations.Scopes(state)[0];
        var schema = PlanningOperations.EffectDecision(state, scope).Schema;
        var input = state.Declarations.Single(d => d.Direction == "input").Id;
        var output = state.Declarations.Single(d => d.Direction == "output").Id;
        var main = OperationEffectFixtures.Answer(state, scope, [Invocation(state, scope, "main")], inputs: [input], outputs: [output]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(main, schema));
        Assert.Single(PlanningOperations.ParseEffect(state, scope, main).Candidates);
        var independentScope = PlanningOperations.Scopes(state)[1];
        var independent = OperationEffectFixtures.Answer(state, independentScope, [Invocation(state, independentScope, "other_scope")]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(independent, PlanningOperations.EffectDecision(state, independentScope).Schema));
        Assert.Equal("other_scope", Assert.Single(PlanningOperations.ParseEffect(state, independentScope, independent).Candidates).WorkflowScope);
        Assert.Contains(state.Obligations, o => o.Id == "other_scope");
    }

    [Theory]
    [InlineData("inputs")]
    [InlineData("outputs")]
    [InlineData("producers")]
    public void ForeignDataflowIsImpossibleInTheResponseSchema(string field)
    {
        var state = State(); var scope = PlanningOperations.Scopes(state)[1];
        var answer = OperationEffectFixtures.Answer(state, scope, [Invocation(state, scope, "other_scope")]);
        answer[field] = OperationEffectFixtures.Strings([field == "producers" ? Invocation(state, scope, "main") :
            state.Declarations.Single(d => d.Direction == (field == "inputs" ? "input" : "output")).Id]);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.EffectDecision(state, scope).Schema));
        Assert.Equal("INTENT_OPERATION_UNRESOLVED", Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseEffect(state, scope, answer)).Code);
    }

    [Theory]
    [InlineData("realizes")]
    [InlineData("governs")]
    [InlineData("shared_rule")]
    public void NoContributionCanCombineScopesEvenWithoutPublicValues(string contribution)
    {
        var state = State(); var scope = PlanningOperations.Scopes(state)[0];
        var answer = OperationEffectFixtures.Answer(state, scope,
            [Invocation(state, scope, "main"), Invocation(state, scope, "other_scope")], contribution, outputs: []);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.EffectDecision(state, scope).Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseEffect(state, scope, answer));
    }

    [Fact]
    public void AmbiguousEffectsWithinOneScopeRemainSelectable()
    {
        var state = State(true); var scopes = PlanningOperations.Scopes(state);
        var reference = PlanningOperations.SourceScopes(state).Last(s => s.Source.Id == "request").Clause;
        var query = PlanningFixtures.Runtime(state, reference, independentBoundary: false);
        OperationEffectFixtures.SeedBoundaries(state);
        var selection = PlanningOperations.Scopes(state).Single(s => s.Evidence!.Id == query.Id);
        var answer = OperationEffectFixtures.Answer(state, selection, scopes.Select(s => Invocation(state, s, "main")));
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.EffectDecision(state, selection).Schema));
        var proof = PlanningOperations.ParseEffect(state, selection, answer);
        Assert.Equal(2, proof.Candidates.Count);
        var decision = PlanningOperations.IdentityDecision(state, selection, proof, answer["effects"]!.AsArray().Select(v => v!.ToString()).ToArray());
        Assert.Equal(2, decision.Schema["enum"]!.AsArray().Count);
    }

    [Fact]
    public async Task EstablishedCallResultIsVisibleThroughCallerEffectNotCalleeDeclarations()
    {
        var graph = TypedPlannerTests.Graph();
        graph.Workflows[0].Steps = [new() { Key = "greeting", Type = "workflow.call", Input = TypedPlannerTests.Obj(
            ("ref", new() { Kind = "workflow", Source = "child" }), ("args", TypedPlannerTests.Obj())) }];
        var child = TypedPlannerTests.Graph().Workflows[0]; child.Key = "child"; graph.Workflows.Add(child);
        // An existing executable interface, not an inferred transfer between scopes.
        var yaml = new PlanningGraphCompiler().Compile(graph, TypedPlannerTests.Preparation());
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        var execution = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), Ct);
        Assert.True(execution.Success, execution.Error?.Message);
        Assert.Equal("Hello", execution.Outputs?["message"]?.GetValue<string>());

        var state = OperationAdmissionTests.State("Transform the returned value."); state.Request.Baseline = graph;
        var baseline = PlanningSourceGroundingRules.BaselineNodes(state).Single(p => p.Value.Workflow == "main");
        var baselineSource = PlanningOperations.SourceScopes(state).First(s => s.Source.Baseline is { OwnerKind: "node", Field: null, Workflow: "main" });
        var call = PlanningFixtures.Runtime(state, baselineSource.Clause, baseline: baseline.Key);
        var request = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        var local = PlanningFixtures.Runtime(state, request.Clause, independentBoundary: false);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var scope = PlanningOperations.Scopes(state).Single(s => s.Evidence!.Id == local.Id);
        OperationEffectFixtures.SeedBoundaries(state);
        var callId = PlanningOperations.EffectDomain(state, call).Single().Key;
        var result = PlanningOperations.EffectDomain(state, local).Single(p => p.Value.BoundaryKind == "result_realization" && p.Value.WorkflowScope == "main").Key;
        var answer = OperationEffectFixtures.Answer(state, scope, [result]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.EffectDecision(state, scope).Schema));
        var foreign = answer.DeepClone().AsObject();
        foreign["producers"] = OperationEffectFixtures.Strings([PlanningOperations.EffectDomain(state, local)
            .Single(p => p.Value.BoundaryKind == "result_realization" && p.Value.WorkflowScope == "child").Key]);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(foreign, PlanningOperations.EffectDecision(state, scope).Schema));
        OperationEffectFixtures.Seed(state, _ => answer, dependency: (producer, consumer) => producer == callId && consumer == result);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal(3, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.Contains(new PlanningObligationRelation(callId, result, "data"), PlanningOperations.EffectRelations(state.Obligations));
    }

    [Fact]
    public async Task RestartRetainsScopedRequestsReceiptsAndIdentity()
    {
        var state = State(true); var scopes = PlanningOperations.Scopes(state); PlanningSnapshot? saved = null;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { CompletionStatus = "completed", Json = OperationEffectFixtures.Response(state, request) }) };
        runtime.OnCheckpoint = s =>
        {
            if (saved is null && s.DecisionPages.Any(p => p.Status == "completed" && p.Decisions.Any(id => id.StartsWith("coverage_", StringComparison.Ordinal))))
            { saved = PlanningContext.Clone(s); throw new OperationCanceledException("Synthetic crash after a scoped receipt."); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(saved);
        OperationEffectFixtures.SeedDependencies(saved, OperationEffectFixtures.Staged(saved));
        var pageIds = saved.DecisionPages.Select(p => (p.Id, p.RequestId)).ToArray();
        var requests = JsonSerializer.Serialize(saved.RequestAccounting, PlanningJsonContext.Default.ListPlanningRequestAccounting);
        foreach (var scope in PlanningOperations.Scopes(saved))
            Assert.Equal(PlanningOperations.EffectDecision(state, scopes.Single(s => s.Evidence!.Id == scope.Evidence!.Id)).Schema.ToJsonString(),
                PlanningOperations.EffectDecision(saved, scope).Schema.ToJsonString());
        await PlanningOperations.ResolveAsync(saved, NoModel(), Ct);
        var clone = PlanningContext.Clone(saved); await PlanningOperations.ResolveAsync(clone, NoModel(), Ct);
        Assert.Equal(pageIds, clone.DecisionPages.Select(p => (p.Id, p.RequestId)));
        Assert.Equal(requests, JsonSerializer.Serialize(clone.RequestAccounting, PlanningJsonContext.Default.ListPlanningRequestAccounting));
        Assert.Equal(saved.OperationAdmissionFingerprint, clone.OperationAdmissionFingerprint);
        Assert.Empty(clone.RepairAllowances);
    }
}
