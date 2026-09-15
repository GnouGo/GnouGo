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

    private static PlanningSnapshot State()
    {
        var state = OperationAdmissionTests.State("Transform the supplied value. Independently transform another value. Run a separate workflow.");
        state.Request.Baseline = new() { Workflows = [new() { Key = "main",
            Inputs = [new() { Name = "value", Required = true, Schema = new() { Type = "string" } }],
            Outputs = [new() { Name = "result", Schema = new() { Type = "string" } }] }] };
        PolicyGroundingTests.Add(state, "request", "Run a separate workflow.", "other_scope", "workflow_boundary");
        foreach (var source in PlanningOperations.SourceScopes(state).Where(s => s.Source.Id == "request").Take(2))
            PlanningFixtures.Runtime(state, source.Clause);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        return state;
    }

    private static string Invocation(PlanningSnapshot state, PlanningOperations.Scope scope, string workflow) =>
        PlanningOperations.EffectDomain(state, scope.Evidence!).Single(p => p.Value.WorkflowScope == workflow && p.Value.BoundaryReference == scope.Evidence!.ActionReference).Key;

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
        var independent = OperationEffectFixtures.Answer(state, scope, [Invocation(state, scope, "other_scope")]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(independent, schema));
        Assert.Equal("other_scope", Assert.Single(PlanningOperations.ParseEffect(state, scope, independent).Candidates).WorkflowScope);
        Assert.Contains(state.Obligations, o => o.Id == "other_scope");
    }

    [Theory]
    [InlineData("inputs")]
    [InlineData("outputs")]
    [InlineData("producers")]
    public void ForeignDataflowIsImpossibleInTheResponseSchema(string field)
    {
        var state = State(); var scope = PlanningOperations.Scopes(state)[0];
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
            [Invocation(state, scope, "main"), Invocation(state, scope, "other_scope")], contribution);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.EffectDecision(state, scope).Schema));
        Assert.Throws<WorkflowRuntimeException>(() => PlanningOperations.ParseEffect(state, scope, answer));
    }

    [Fact]
    public void AmbiguousEffectsWithinOneScopeRemainSelectable()
    {
        var state = State(); var scopes = PlanningOperations.Scopes(state);
        var answer = OperationEffectFixtures.Answer(state, scopes[0], scopes.Select(s => Invocation(state, s, "main")));
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.EffectDecision(state, scopes[0]).Schema));
        var proof = PlanningOperations.ParseEffect(state, scopes[0], answer);
        Assert.Equal(2, proof.Candidates.Count);
        var decision = PlanningOperations.IdentityDecision(state, scopes[0], proof, answer["effects"]!.AsArray().Select(v => v!.ToString()).ToArray());
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
        var baselineSource = PlanningOperations.SourceScopes(state).First(s => s.Source.Authority == PlanningSourceAuthority.ExistingBehavior);
        var call = PlanningFixtures.Runtime(state, baselineSource.Clause, baseline: baseline.Key);
        var request = PlanningOperations.SourceScopes(state).Single(s => s.Source.Id == "request");
        var local = PlanningFixtures.Runtime(state, request.Clause);
        PlanningDeclarations.Commit(state, [], PlanningDeclarations.EvidenceFingerprint(state));
        var scope = PlanningOperations.Scopes(state).Single(s => s.Evidence!.Id == local.Id);
        var callId = PlanningOperations.EffectDomain(state, call).Single().Key;
        var result = PlanningOperations.EffectDomain(state, local).Single(p => p.Value.BoundaryKind == "result_realization" && p.Value.WorkflowScope == "main").Key;
        var answer = OperationEffectFixtures.Answer(state, scope, [result], producers: [callId]);
        Assert.Empty(PlanningContractValidation.ValidateInstance(answer, PlanningOperations.EffectDecision(state, scope).Schema));
        var foreign = answer.DeepClone().AsObject();
        foreign["producers"] = OperationEffectFixtures.Strings([PlanningOperations.EffectDomain(state, local)
            .Single(p => p.Value.BoundaryKind == "result_realization" && p.Value.WorkflowScope == "child").Key]);
        Assert.NotEmpty(PlanningContractValidation.ValidateInstance(foreign, PlanningOperations.EffectDecision(state, scope).Schema));
        OperationEffectFixtures.Seed(state, _ => answer);
        await PlanningOperations.ResolveAsync(state, NoModel(), Ct);
        Assert.Equal(2, state.Obligations.Count(PlanningSourceDecisions.IsOperation));
        Assert.Contains(new PlanningObligationRelation(callId, result, "data"), PlanningOperations.EffectRelations(state.Obligations));
    }

    [Fact]
    public async Task RestartRetainsScopedRequestsReceiptsAndIdentity()
    {
        var state = State(); var scopes = PlanningOperations.Scopes(state); PlanningSnapshot? saved = null;
        var expected = scopes.ToDictionary(s => PlanningOperations.EffectDecisionId(s.Evidence!), s => OperationEffectFixtures.Answer(state, s, [Invocation(state, s, "main")]));
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        { CompletionStatus = "completed", Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
            new KeyValuePair<string, JsonNode?>(p.Key, expected[p.Key].DeepClone()))) }) };
        runtime.OnCheckpoint = s =>
        {
            if (saved is null && s.DecisionPages.Any(p => p.Status == "completed"))
            { saved = PlanningContext.Clone(s); throw new OperationCanceledException("Synthetic crash after a scoped receipt."); }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => PlanningOperations.ResolveAsync(state, runtime, Ct));
        Assert.NotNull(saved);
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
