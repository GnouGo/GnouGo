using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DecisionPagingTests
{
    [Fact]
    public async Task ANewProjectionOrGateCannotResetTheSameSemanticCorrection()
    {
        var state = TypedPlannerTests.Session();
        var initial = new PlanningDecisionPages.Decision("staged_coordinate", PlanningHoleRequests.Enum("fixed", "other"), new(), "governing-evidence", CorrectionId: "owned-hole");
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = new JsonObject { [initial.Id] = "fixed" } }) };
        await PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, "repair", "main", PlanningGates.Response, [initial], TestContext.Current.CancellationToken);
        var restored = PlanningContext.Clone(state);
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveCorrectionsAsync(restored, runtime,
            "repair", "main", PlanningGates.Semantic, [initial with { Id = "graph_coordinate" }], TestContext.Current.CancellationToken));
        Assert.Equal("DECISION_CORRECTION_EXHAUSTED", error.Code); Assert.Single(runtime.Requests);
        Assert.Equal("owned-hole", Assert.Single(restored.DecisionCorrections).DecisionId);
        Assert.Equal(1, restored.RepairAllowances.Sum(a => a.Attempts));
    }
    [Fact]
    public async Task EveryDecisionIsCoveredBelowTheTargetAndRestartReusesCompletedPages()
    {
        var state = TypedPlannerTests.Session();
        var decisions = Enumerable.Range(0, 30).Select(i => new PlanningDecisionPages.Decision("d" + i.ToString("D2"),
            PlanningHoleRequests.Enum("left", "right"), new JsonObject { ["obligation"] = new string((char)('a' + i % 26), 1400) }, "evidence")).ToArray();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        {
            Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("left"))))
        }) };
        var result = await PlanningDecisionPages.ResolveAsync(state, runtime, "capability_decisions", "$plan", decisions, TestContext.Current.CancellationToken);
        Assert.Equal(30, result.Count);
        Assert.InRange(runtime.Requests.Count, 2, 30);
        Assert.All(runtime.Requests, r => Assert.InRange(PlanningJsonTransport.EstimateInputTokens(r.Prompt!, r.StructuredOutputSchema!.AsObject()), 1, 9600));
        var calls = runtime.Requests.Count;
        var restored = PlanningContext.Clone(state);
        var replay = await PlanningDecisionPages.ResolveAsync(restored, runtime, "capability_decisions", "$plan", decisions, TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(result, replay)); Assert.Equal(calls, runtime.Requests.Count);
        Assert.All(restored.DecisionPages, p => Assert.Equal("completed", p.Status));
    }

    [Fact]
    public async Task AnInvalidNeighborIsCorrectedOnceWithoutRegeneratingAcceptedAssignments()
    {
        var state = TypedPlannerTests.Session(); state.Request.MaxRepairsPerWorkflowGate = 5;
        var decisions = new[] { "a", "b" }.Select(id => new PlanningDecisionPages.Decision(id, PlanningHoleRequests.Enum("yes", "no"), new(), "known")).ToArray();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, r, _) => Task.FromResult(new LLMResponse
        {
            Json = r.StructuredOutputSchema!["properties"]!.AsObject().Count == 2 ? new JsonObject { ["a"] = "yes", ["b"] = "invalid" } : new JsonObject { ["b"] = "no" }
        }) };
        var result = await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main", decisions, TestContext.Current.CancellationToken);
        Assert.Equal("yes", result["a"]!.ToString()); Assert.Equal("no", result["b"]!.ToString());
        Assert.Equal(2, runtime.Requests.Count);
        Assert.Equal("b", Assert.Single(runtime.Requests[1].StructuredOutputSchema!["properties"]!.AsObject()).Key);
        Assert.Single(state.DecisionCorrections);
        Assert.Equal(1, Assert.Single(state.RepairAllowances).Attempts);
    }
    [Fact]
    public async Task TruncatedPagesSplitOnceAndChargeOnlyDurableCorrectionReservations()
    {
        var state = TypedPlannerTests.Session(); state.Request.MaxRepairsPerWorkflowGate = 5;
        var decisions = new[] { "a", "b" }.Select(id => new PlanningDecisionPages.Decision(id, PlanningHoleRequests.Enum("yes", "no"), new(), "evidence")).ToArray();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(new LLMResponse
        {
            CompletionStatus = request.StructuredOutputSchema!["properties"]!.AsObject().Count == 2 ? "output_limit" : "completed",
            Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("yes"))))
        }) };
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "$plan", decisions, TestContext.Current.CancellationToken);
        Assert.Equal(2, values.Count); Assert.Equal(3, runtime.Requests.Count);
        Assert.Equal(2, state.DecisionCorrections.Count); Assert.Equal(2, Assert.Single(state.RepairAllowances).Attempts);
        Assert.Equal(PlanningGates.Behavior, state.RequestAccounting[0].Gate);
        Assert.All(state.RequestAccounting.Skip(1), r => { Assert.True(r.Repair); Assert.Equal(PlanningGates.Response, r.Gate); });
        var restored = PlanningContext.Clone(state);
        await PlanningDecisionPages.ResolveAsync(restored, runtime, "behavior", "$plan", decisions, TestContext.Current.CancellationToken);
        Assert.Equal(3, runtime.Requests.Count); Assert.Equal(2, Assert.Single(restored.RepairAllowances).Attempts);
    }

    [Fact]
    public async Task InvalidCorrectionStopsWithoutAThirdRequestAcrossRestart()
    {
        var state = TypedPlannerTests.Session();
        var decisions = new[] { new PlanningDecisionPages.Decision("a", PlanningHoleRequests.Enum("yes", "no"), new(), "unchanged") };
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = new JsonObject { ["a"] = "invalid" } }) };
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "$plan", decisions, TestContext.Current.CancellationToken));
        Assert.Equal("DECISION_CORRECTION_INVALID", error.Code); Assert.Equal(2, runtime.Requests.Count);
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveAsync(PlanningContext.Clone(state), runtime, "behavior", "$plan", decisions, TestContext.Current.CancellationToken));
        Assert.Equal(2, runtime.Requests.Count); Assert.Single(state.DecisionCorrections);
    }

    [Fact]
    public async Task OversizedCorrectionDoesNotConsumeAnAllowanceOrReserveARequest()
    {
        var state = TypedPlannerTests.Session();
        var decisions = new[] { new PlanningDecisionPages.Decision("a", PlanningHoleRequests.Enum("yes", "no"), new(), "unchanged") };
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { Json = new JsonObject { ["a"] = new string('x', 50000) } }) };
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "$plan", decisions, TestContext.Current.CancellationToken));
        Assert.Single(runtime.Requests); Assert.Empty(state.DecisionCorrections); Assert.Empty(state.RepairAllowances);
    }
}
