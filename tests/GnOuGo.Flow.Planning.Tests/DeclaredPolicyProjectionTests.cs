using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DeclaredPolicyProjectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningSnapshot State()
    {
        var state = OperationAdmissionTests.State("Describe the requested workflow.");
        const string text = "The .Hidden directory is reserved. Require permission before a mutation.";
        state.Request.Options["policy"] = new JsonObject { ["instructions"] = text,
            ["declared_evidence"] = JsonSerializer.SerializeToNode(new PlanningDeclaredPolicyEvidence(1, PlanningGraphCompiler.Fingerprint(text),
                [new(0, 34, [new("implementation_policy", true)]), new(35, text.Length - 35, [new("confirmation_required", true)])]), PlanningJsonContext.Default.PlanningDeclaredPolicyEvidence) };
        return state;
    }

    [Fact]
    public async Task ProducerMeaningRemovesPolicyQuestionsWithoutActionAuthority()
    {
        var state = State(); var decision = Assert.Single(PlanningSourceDecisions.InterpretationDecisions(state));
        Assert.Equal("user_request", decision.Context["role"]!.ToString());
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse { Json = new JsonObject(
            request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key,
                new JsonObject { ["obligations"] = new JsonArray(), ["runtime"] = new JsonArray(new JsonObject { ["role"] = "planning_directive" }) }))) }) };
        await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct);
        Assert.Equal(2, state.Obligations.Count); Assert.All(state.Obligations, o => Assert.NotNull(o.Grounding!.DeclaredPolicyFingerprint));
        Assert.Equal(2, state.RuntimeEvidence.Count(e => e.Origin == PlanningRuntimeEvidenceOrigin.EngineSourceAuthority));
        Assert.DoesNotContain(state.RuntimeEvidence, e => e.Role is "runtime_action" or "local_behavior");
        PlanningSourceGroundingRules.ValidateAll(PlanningContext.Clone(state)); PlanningOperations.RequireRuntimeEvidence(PlanningContext.Clone(state));
        var before = state.RequestAccounting.Count;
        await PlanningSourceDecisions.InterpretAsync(state, runtime, Ct);
        Assert.Equal(before, state.RequestAccounting.Count);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("coverage")]
    [InlineData("operation")]
    [InlineData("overlap")]
    [InlineData("version")]
    public void InvalidDeclaredMeaningNeverFallsBackToModelInterpretation(string fault)
    {
        var state = State(); var evidence = state.Request.Options["policy"]!["declared_evidence"]!;
        if (fault == "fingerprint") evidence["sourceFingerprint"] = "stale";
        if (fault == "version") evidence["version"] = 0;
        if (fault == "coverage") evidence["clauses"]!.AsArray().RemoveAt(1);
        if (fault == "operation") evidence["clauses"]![0]!["meanings"]![0]!["kind"] = "external_write";
        if (fault == "overlap") evidence["clauses"]![1]!["start"] = 0;
        Assert.Equal("INTENT_SOURCE_AUTHORITY_UNPROVEN", Assert.Throws<WorkflowRuntimeException>(() => PlanningSourceDecisions.InterpretationDecisions(state)).Code);
        Assert.Empty(state.RequestAccounting);
    }

    [Fact]
    public void RawPolicyStillNeedsBoundedMeaningAndExecutionFactsHaveOneResponseAuthority()
    {
        var state = State(); state.Request.Options["policy"]!.AsObject().Remove("declared_evidence");
        var decisions = PlanningSourceDecisions.InterpretationDecisions(state);
        Assert.Contains(decisions, d => d.Context["role"]!.ToString() == "host_constraint");
        foreach (var d in decisions)
        foreach (var kind in PlanningSourceGroundingRules.OperationKinds)
            Assert.DoesNotContain(kind, d.Schema["properties"]!["obligations"]!.ToJsonString());
        Assert.Contains("local_processing", decisions.Single(d => d.Context["role"]!.ToString() == "user_request").Schema["properties"]!["runtime"]!.ToJsonString());
    }
}
