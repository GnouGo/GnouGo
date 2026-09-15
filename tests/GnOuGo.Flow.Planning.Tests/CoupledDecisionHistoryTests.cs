using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class CoupledDecisionHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningDecisionPages.Decision Group(string id = "joint") => new(id, PlanningHoleRequests.Enum("ok"), new(), "evidence", SourceDecisionIds: ["first", "second"]);

    [Fact]
    public void MissingHistoricalLineageRemainsAbsentFromSerialization()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PlanningSnapshot { DecisionPages = [new() { Id = "ordinary" }] }, PlanningJsonContext.Default.PlanningSnapshot);
        Assert.DoesNotContain("sourceDecisionIds", json);
    }

    [Fact]
    public async Task AGroupConsumesItsOriginalDecisionsAcrossPagesGatesAndRestart()
    {
        var state = TypedPlannerTests.Session(); var group = Group();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, r, _) => Task.FromResult(new LLMResponse
            { Json = new JsonObject(r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("ok")))) }) };
        await PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, "repair", "$plan", PlanningGates.Response, [group], Ct);
        Assert.Equal(["first", "second"], state.DecisionCorrections.Select(c => c.DecisionId).Order(StringComparer.Ordinal));
        var restored = PlanningContext.Clone(state);
        await PlanningDecisionPages.ResolveCorrectionsAsync(restored, runtime, "repair", "$plan", PlanningGates.Response, [group with { SourceDecisionIds = ["second", "first"] }], Ct);
        Assert.Single(runtime.Requests); Assert.Equal(1, restored.RepairAllowances.Sum(a => a.Attempts));
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveCorrectionsAsync(restored, runtime,
            "repair", "$plan", PlanningGates.Semantic, [new("new-projection", PlanningHoleRequests.Enum("ok"), new(), "evidence", CorrectionId: "second")], Ct));
        Assert.Equal("DECISION_CORRECTION_EXHAUSTED", error.Code); Assert.Single(runtime.Requests);
        restored.DecisionPages[0].SourceDecisionIds = ["foreign"];
        await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningDecisionPages.ResolveCorrectionsAsync(restored, runtime, "repair", "$plan", PlanningGates.Response, [group], Ct));
        Assert.Single(runtime.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupingCannotReplenishAnOriginalDecisionsOutputEscalation(bool groupFirst)
    {
        var state = TypedPlannerTests.Session(); var group = Group(); var origin = new PlanningDecisionPages.Decision("second", PlanningHoleRequests.Enum("ok"), new(), "evidence");
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, r, _) => Task.FromResult(new LLMResponse
        {
            CompletionStatus = r.MaxTokens == 8192 ? "output_limit" : "completed",
            Json = r.MaxTokens == 8192 ? null : new JsonObject(r.StructuredOutputSchema!["properties"]!.AsObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("ok"))))
        }) };
        if (groupFirst) await PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, "intent", "$plan", PlanningGates.Response, [group], Ct);
        else await PlanningDecisionPages.ResolveAsync(state, runtime, "intent", "$plan", [origin], Ct);
        state = PlanningContext.Clone(state);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(async () =>
        {
            if (groupFirst) await PlanningDecisionPages.ResolveAsync(state, runtime, "intent", "$plan", [origin], Ct);
            else await PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, "intent", "$plan", PlanningGates.Response, [group], Ct);
        });
        Assert.Equal("DECISION_OUTPUT_LIMIT", error.Code);
        Assert.Equal([8192, 16384, 8192], runtime.Requests.Select(r => r.MaxTokens));
        Assert.Equal(1, state.RepairAllowances.Sum(a => a.Attempts));
        Assert.Single(state.DecisionPages, p => p.OutputBudgetEscalation is not null);
    }
}
