using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Planning.Tests;

// Synthetic receipts exercise policy; they are never substituted for historical live evidence.
public sealed class OutputBudgetEscalationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static PlanningDecisionPages.Decision Decision(string id = "choice") =>
        new(id, PlanningHoleRequests.Enum("yes", "no"), new() { ["fact"] = "owned" }, "evidence", CorrectionId: "semantic-choice");
    private static LLMResponse Answer(LLMRequest request, bool invalid = false) => new()
    {
        CompletionStatus = request.MaxTokens == 8192 ? "output_limit" : "completed",
        Json = new JsonObject(request.StructuredOutputSchema!["properties"]!.AsObject().Select(p =>
            new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(invalid ? "invalid" : "yes")))),
        Usage = new JsonObject() { ["input_tokens"] = 100, ["output_tokens"] = request.MaxTokens == 8192 ? 8192 : 20 }
    };

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task SingletonEscalatesExactRequestWithoutRepairAllowance(int allowance)
    {
        var state = TypedPlannerTests.Session(); state.Request.MaxRepairsPerWorkflowGate = allowance;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(Answer(request)) };
        var result = await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main", [Decision()], Ct);
        Assert.Equal("yes", result["choice"]!.ToString()); Assert.Equal(2, runtime.Requests.Count);
        var parent = runtime.Requests[0]; var child = runtime.Requests[1];
        PlanningGenerationPolicy.ValidateOutputEscalation(child, parent, Answer(parent), state.Request.SessionId);
        Assert.Equal(8192, parent.MaxTokens); Assert.Equal(16384, child.MaxTokens);
        Assert.Empty(state.RepairAllowances); Assert.Empty(state.DecisionCorrections);
        Assert.All(state.RequestAccounting, a => Assert.False(a.Repair));
        Assert.Equal("semantic-choice", child.OutputBudgetEscalation!.CanonicalDecisionId);
        Assert.Equal(2, state.RequestAccounting.Count(a => a.Evidence == "receipt"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RestartRetainsExactNestedTreeReceiptsAndCompletedSiblings(int checkpoint)
    {
        var state = TypedPlannerTests.Session(); state.Request.MaxRepairsPerWorkflowGate = 0;
        var decisions = new[] { "a", "b", "c", "d", "e" }.Select(id => Decision(id) with { CorrectionId = id }).ToArray();
        var receipts = new Dictionary<string, LLMResponse>(); var scopes = new List<string>();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            if (receipts.TryGetValue(request.ClientRequestId!, out var receipt)) return Task.FromResult(receipt);
            var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
            scopes.Add(string.Join(',', fields.Select(p => p.Key)) + ":" + request.MaxTokens);
            var response = Answer(request);
            response.CompletionStatus = fields.Count is 5 or 3 || fields.ContainsKey("c") && request.MaxTokens == 8192 ? "output_limit" : "completed";
            receipts.Add(request.ClientRequestId!, response); return Task.FromResult(response);
        } };
        while (PlanningDecisionPages.Next(state, "behavior", "main", decisions) is { } next)
        {
            var id = next.Call.Id;
            if (checkpoint == 1) { state = PlanningContext.Clone(state); next = PlanningDecisionPages.Next(state, "behavior", "main", decisions)!; }
            Assert.Equal(id, next.Call.Id);
            var response = await PlanningModelCalls.DispatchAsync(state, runtime, next.Call, Ct);
            if (checkpoint == 2)
            {
                state = PlanningContext.Clone(state); next = PlanningDecisionPages.Next(state, "behavior", "main", decisions)!;
                Assert.Equal(id, next.Call.Id); response = await PlanningModelCalls.DispatchAsync(state, runtime, next.Call, Ct);
            }
            PlanningDecisionPages.Accept(state, next, response);
            if (checkpoint == 3) state = PlanningContext.Clone(state);
        }
        Assert.Equal(["a,b,c,d,e:8192", "a,b:8192", "c,d,e:8192", "c:8192", "c:16384", "d,e:8192"], scopes);
        Assert.Equal(6, state.RequestAccounting.Count); Assert.Equal(6, receipts.Count);
        Assert.Empty(state.DecisionCorrections); Assert.Empty(state.RepairAllowances);
        Assert.Equal(5, PlanningDecisionPages.Completed(PlanningContext.Clone(state), "behavior", "main", decisions)!.Count);
        Assert.All(state.DecisionPages, p => Assert.Equal("completed", p.Status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SemanticCorrectionPartitionsRetainOneOriginalChargeAndRestriction(bool invalid)
    {
        var state = TypedPlannerTests.Session();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(Answer(request, invalid)) };
        var run = () => PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, "behavior", "main", PlanningGates.Behavior, [Decision()], Ct);
        if (invalid) Assert.Equal("DECISION_CORRECTION_INVALID", (await Assert.ThrowsAsync<WorkflowRuntimeException>(run)).Code);
        else Assert.Equal("yes", (await run())["choice"]!.ToString());
        Assert.Equal(2, runtime.Requests.Count); Assert.Single(state.DecisionCorrections);
        Assert.Equal(1, Assert.Single(state.RepairAllowances).Attempts);
        var escalated = Assert.Single(state.DecisionPages, p => p.Origin == PlanningDecisionPageOrigin.OutputBudgetEscalation);
        Assert.True(escalated.Correction); Assert.Equal(PlanningGates.Behavior, escalated.Gate);
        Assert.Single(state.RequestAccounting, a => a.Repair == true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedGateRevisionOrProjectionCannotReplenishEscalation(bool correction)
    {
        var state = TypedPlannerTests.Session();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(Answer(request)) };
        await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main", [Decision()], Ct);
        state = PlanningContext.Clone(state); state.Revision++;
        var next = Decision("new-coordinate");
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => correction
            ? PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, "repair", "main", PlanningGates.Semantic, [next], Ct)
            : PlanningDecisionPages.ResolveAsync(state, runtime, "semantic_review", "main", [next], Ct));
        Assert.Equal("DECISION_OUTPUT_LIMIT", error.Code); Assert.Equal(3, runtime.Requests.Count);
        Assert.Single(state.DecisionPages, p => p.OutputBudgetEscalation is not null);
        Assert.Equal(correction ? 1 : 0, state.RepairAllowances.Sum(a => a.Attempts));
    }

    [Fact]
    public async Task PageRepackingRetainsTheConsumedCanonicalAllowance()
    {
        var state = TypedPlannerTests.Session();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(Answer(request)) };
        await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main", [Decision("a")], Ct);
        state = PlanningContext.Clone(state); state.Revision++;
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main",
            [Decision("a"), Decision("b") with { CorrectionId = "other-decision" }], Ct));
        Assert.Equal("DECISION_OUTPUT_LIMIT", error.Code);
        Assert.Equal(new int?[] { 8192, 16384, 8192, 8192 }, runtime.Requests.Select(r => r.MaxTokens));
        Assert.Single(state.DecisionPages, p => p.OutputBudgetEscalation is not null);
        Assert.Empty(state.RepairAllowances); Assert.Empty(state.DecisionCorrections);
    }

    [Fact]
    public async Task InvalidEscalatedAnswerMayBeCorrectedButCannotEscalateAgain()
    {
        var state = TypedPlannerTests.Session();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(Answer(request, invalid: true)) };
        Assert.Equal("DECISION_OUTPUT_LIMIT", (await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main", [Decision()], Ct))).Code);
        Assert.Equal(new int?[] { 8192, 16384, 8192 }, runtime.Requests.Select(r => r.MaxTokens));
        Assert.Single(state.DecisionCorrections); Assert.Equal(1, state.RepairAllowances.Sum(a => a.Attempts));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public async Task OtherConfiguredCeilingsNeverEscalate(int ceiling)
    {
        var state = TypedPlannerTests.Session(); state.Request.Generation.MaxOutputTokens = ceiling;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }) };
        Assert.Equal("DECISION_OUTPUT_LIMIT", (await Assert.ThrowsAsync<WorkflowRuntimeException>(() =>
            PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main", [Decision()], Ct))).Code);
        Assert.Single(runtime.Requests); Assert.DoesNotContain(state.DecisionPages, p => p.OutputBudgetEscalation is not null);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("receipt")]
    [InlineData("lineage")]
    public async Task CorruptedEscalationStopsBeforeDispatch(string corruption)
    {
        var state = TypedPlannerTests.Session();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(Answer(request)) };
        var first = PlanningDecisionPages.Next(state, "behavior", "main", [Decision()])!;
        PlanningDecisionPages.Accept(state, first, await PlanningModelCalls.DispatchAsync(state, runtime, first.Call, Ct));
        state = PlanningContext.Clone(state);
        if (corruption == "pending") state.Construction.PendingCalls.Clear();
        if (corruption == "receipt") state.RequestAccounting[0].ReceiptFingerprint = "foreign";
        if (corruption == "lineage") state.DecisionPages.Single(p => p.OutputBudgetEscalation is not null).OutputBudgetEscalation =
            state.DecisionPages.Single(p => p.OutputBudgetEscalation is not null).OutputBudgetEscalation! with { CanonicalDecisionId = "foreign" };
        Assert.Throws<PlanningConflictException>(() => PlanningDecisionPages.Next(state, "behavior", "main", [Decision()]));
        Assert.Single(runtime.Requests);
    }
}
