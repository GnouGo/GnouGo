using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class DecisionBudgetTests
{
    [Fact]
    public void ReservationRejectsRequestsAboveTheHeadroomTargetWithoutChargingOrDispatching()
    {
        var state = TypedPlannerTests.Session();
        var schema = PlanningHoleRequests.Object(("choice", PlanningHoleRequests.Enum("a", "b")));
        var prompt = new string('x', (9601 - 256) * 3 - System.Text.Encoding.UTF8.GetByteCount(schema.ToJsonString()));
        Assert.Equal(9601, PlanningJsonTransport.EstimateInputTokens(prompt, schema));
        var error = Assert.Throws<WorkflowRuntimeException>(() => PlanningModelCalls.Reserve(state, "construction", "main", PlanningModelCalls.Request(state, prompt, schema)));
        Assert.Equal("MODEL_INPUT_LIMIT", error.Code);
        Assert.Empty(state.Construction.PendingCalls);
        Assert.Empty(state.RequestAccounting);
        Assert.Equal(0, state.Construction.ModelSequence);
    }

    [Theory]
    [InlineData("intent", "low")]
    [InlineData("workflow.plan.capability_matching", "low")]
    [InlineData("construction", "low")]
    [InlineData("behavior", "medium")]
    [InlineData("behavior_repair", "medium")]
    [InlineData("semantic_review", "medium")]
    public void TheReservedPhaseSelectsReasoningBeforeTheRequestHashIsLocked(string phase, string reasoning)
    {
        var state = TypedPlannerTests.Session();
        var call = PlanningModelCalls.Reserve(state, phase, "main", PlanningModelCalls.Request(state, "Choose.", PlanningHoleRequests.Object(("choice", PlanningHoleRequests.Enum("a", "b")))));
        Assert.Equal(reasoning, call.Request.Reasoning);
        var clone = PlanningContext.Clone(state);
        Assert.Equal(call.Id, PlanningModelCalls.Reserve(clone, phase, "main", new()).Id);
        Assert.Equal(reasoning, clone.Construction.PendingCalls[0].Request.Reasoning);
    }
}
