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
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task TruncationRecursivelyPartitionsFiveDecisionsWithoutSemanticRepairCharges(int allowance)
    {
        var state = TypedPlannerTests.Session(); state.Request.MaxRepairsPerWorkflowGate = allowance;
        var decisions = new[] { "a", "b", "c", "d", "e" }.Select(id => new PlanningDecisionPages.Decision(id, PlanningHoleRequests.Enum("yes", "no"), new(), "evidence")).ToArray();
        var scopes = new List<string>();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
            scopes.Add(string.Join(',', fields.Select(p => p.Key)));
            return Task.FromResult(new LLMResponse
            {
                CompletionStatus = fields.Count is 5 or 3 ? "output_limit" : "completed",
                Json = new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create("yes"))))
            });
        } };
        var values = await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "$plan", decisions, TestContext.Current.CancellationToken);
        Assert.Equal(["a,b,c,d,e", "a,b", "c,d,e", "c", "d,e"], scopes);
        Assert.Equal(5, values.Count); Assert.Equal(5, state.RequestAccounting.Count);
        Assert.Empty(state.DecisionCorrections); Assert.Empty(state.RepairAllowances);
        Assert.All(state.RequestAccounting, r => { Assert.False(r.Repair); Assert.Equal(PlanningGates.Behavior, r.Gate); });
        var restored = PlanningContext.Clone(state);
        var replay = await PlanningDecisionPages.ResolveAsync(restored, runtime, "behavior", "$plan", decisions.Reverse().ToArray(), TestContext.Current.CancellationToken);
        Assert.True(JsonNode.DeepEquals(values, replay)); Assert.Equal(5, runtime.Requests.Count);
        Assert.Empty(restored.RepairAllowances); Assert.Empty(restored.DecisionCorrections);
        Assert.Equal(4, restored.DecisionPages.Count(p => p.Origin == PlanningDecisionPageOrigin.OutputPartition));
        Assert.All(restored.DecisionPages, p => Assert.False(p.Correction));
    }

    // Synthetic transport evidence, independent of archived live receipts.
    private static PlanningDecisionPages.Decision[] FiveDecisions() => new[] { "a", "b", "c", "d", "e" }
        .Select(id => new PlanningDecisionPages.Decision(id, PlanningHoleRequests.Enum("yes", "no"), new(), "unchanged")).ToArray();
    private static LLMResponse PartitionResponse(LLMRequest request, string value = "yes")
    {
        var fields = request.StructuredOutputSchema!["properties"]!.AsObject();
        return new() { CompletionStatus = fields.Count is 5 or 3 ? "output_limit" : "completed",
            Json = new JsonObject(fields.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(value)))),
            Usage = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = fields.Count is 5 or 3 ? 8192 : 20 } };
    }

    [Theory]
    [InlineData(1)] // Before dispatch: exact reserved request survives.
    [InlineData(2)] // After receipt: journal replay precedes acceptance.
    [InlineData(3)] // After acceptance: completed siblings are reused.
    public async Task RestartAtEveryCheckpointRetainsTreeRequestsAndAccounting(int checkpoint)
    {
        var original = TypedPlannerTests.Session();
        var decisions = FiveDecisions();
        async Task<PlanningSnapshot> Run(bool restart)
        {
            var state = PlanningContext.Clone(original);
            var receipts = new Dictionary<string, LLMResponse>();
            var dispatches = new List<string>();
            var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
            {
                if (receipts.TryGetValue(request.ClientRequestId!, out var retained)) return Task.FromResult(retained);
                dispatches.Add(string.Join(',', request.StructuredOutputSchema!["properties"]!.AsObject().Select(p => p.Key)));
                var response = PartitionResponse(request); receipts.Add(request.ClientRequestId!, response); return Task.FromResult(response);
            } };
            while (PlanningDecisionPages.Next(state, "behavior", "$plan", decisions) is { } next)
            {
                var id = next.Call.Id;
                if (restart && checkpoint == 1)
                {
                    state = PlanningContext.Clone(state);
                    next = PlanningDecisionPages.Next(state, "behavior", "$plan", decisions)!;
                    Assert.Equal(id, next.Call.Id);
                }
                var response = await PlanningModelCalls.DispatchAsync(state, runtime, next.Call, TestContext.Current.CancellationToken);
                if (restart && checkpoint == 2)
                {
                    state = PlanningContext.Clone(state);
                    next = PlanningDecisionPages.Next(state, "behavior", "$plan", decisions)!;
                    Assert.Equal(id, next.Call.Id);
                    response = await PlanningModelCalls.DispatchAsync(state, runtime, next.Call, TestContext.Current.CancellationToken);
                }
                PlanningDecisionPages.Accept(state, next, response);
                if (restart && checkpoint == 3) state = PlanningContext.Clone(state);
            }
            Assert.Equal(["a,b,c,d,e", "a,b", "c,d,e", "c", "d,e"], dispatches);
            Assert.Equal(5, receipts.Count); Assert.Equal(5, state.RequestAccounting.Count);
            Assert.Equal(500, state.RequestAccounting.Sum(r => r.InputTokens));
            Assert.Empty(state.RepairAllowances); Assert.Empty(state.DecisionCorrections);
            Assert.Empty(state.Construction.PendingCalls);
            return state;
        }
        var uninterrupted = await Run(false); var recovered = await Run(true);
        Assert.Equal(uninterrupted.DecisionPages.Select(p => (p.Id, p.RequestId, p.ParentId, p.Gate, p.Origin)),
            recovered.DecisionPages.Select(p => (p.Id, p.RequestId, p.ParentId, p.Gate, p.Origin)));
        Assert.True(JsonNode.DeepEquals(PlanningDecisionPages.Completed(uninterrupted, "behavior", "$plan", decisions),
            PlanningDecisionPages.Completed(recovered, "behavior", "$plan", decisions)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedSemanticCorrectionRetainsItsGateScopeAndSingleCharge(bool invalid)
    {
        var state = TypedPlannerTests.Session(); state.Request.MaxRepairsPerWorkflowGate = 1;
        var decisions = FiveDecisions();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(PartitionResponse(request, invalid ? "invalid" : "yes")) };
        var task = PlanningDecisionPages.ResolveCorrectionsAsync(state, runtime, "typed_repair", "main", PlanningGates.Typed, decisions, TestContext.Current.CancellationToken);
        if (invalid)
        {
            var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => task);
            Assert.Equal("DECISION_CORRECTION_INVALID", error.Code); Assert.Equal(2, runtime.Requests.Count);
            await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveCorrectionsAsync(
                PlanningContext.Clone(state), runtime, "typed_repair", "main", PlanningGates.Typed, decisions, TestContext.Current.CancellationToken));
            Assert.Equal(2, runtime.Requests.Count);
        }
        else
        {
            Assert.Equal(5, (await task).Count); Assert.Equal(5, runtime.Requests.Count);
            await PlanningDecisionPages.ResolveCorrectionsAsync(PlanningContext.Clone(state), runtime, "typed_repair", "main", PlanningGates.Typed, decisions, TestContext.Current.CancellationToken);
            Assert.Equal(5, runtime.Requests.Count);
        }
        Assert.Equal(5, state.DecisionCorrections.Count); // One identity per original decision, never per partition.
        Assert.Equal(1, Assert.Single(state.RepairAllowances).Attempts);
        Assert.Single(state.RequestAccounting, a => a.Repair == true);
        Assert.All(state.DecisionPages, p => { Assert.True(p.Correction); Assert.Equal(PlanningGates.Typed, p.Gate); });
        Assert.All(state.RequestAccounting, a => Assert.Equal(PlanningGates.Typed, a.Gate));
        Assert.Equal(decisions.Select(d => d.Id), state.DecisionPages[0].Decisions);
    }

    [Fact]
    public async Task ZeroRepairAllowanceStillRejectsGenuineCorrections()
    {
        var state = TypedPlannerTests.Session(); state.Request.MaxRepairsPerWorkflowGate = 0;
        var runtime = new TypedPlannerTests.FakeRuntime();
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveCorrectionsAsync(
            state, runtime, "repair", "main", PlanningGates.Response, FiveDecisions(), TestContext.Current.CancellationToken));
        Assert.Equal("REPAIR_EXHAUSTED", error.Code); Assert.Empty(runtime.Requests);
        Assert.Empty(state.RequestAccounting); Assert.Empty(state.DecisionCorrections);
    }

    [Fact]
    public async Task SingletonTruncationStopsWithExactDecisionAndRequestEvidence()
    {
        var state = TypedPlannerTests.Session();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" }) };
        var error = await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveAsync(
            state, runtime, "behavior", "main", FiveDecisions(), TestContext.Current.CancellationToken));
        Assert.Equal("DECISION_OUTPUT_LIMIT", error.Code);
        var stopped = Assert.Single(state.DecisionPages, p => p.Status == "stopped");
        Assert.Equal("a", Assert.Single(stopped.Decisions)); Assert.Equal(4, runtime.Requests.Count);
        Assert.Equal(PlanningDecisionPageOrigin.OutputBudgetEscalation, stopped.Origin);
        Assert.Equal(16384, stopped.EffectiveOutputTokens);
        Assert.Equal(stopped.Id, error.Details!["pageId"]!.ToString()); Assert.Equal(stopped.RequestId, error.Details["requestId"]!.ToString());
        Assert.Equal("/decisions/a", error.Details["location"]!.ToString());
        Assert.Empty(state.RepairAllowances); Assert.Empty(state.DecisionCorrections);
        await Assert.ThrowsAsync<GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException>(() => PlanningDecisionPages.ResolveAsync(
            PlanningContext.Clone(state), runtime, "behavior", "main", FiveDecisions(), TestContext.Current.CancellationToken));
        Assert.Equal(4, runtime.Requests.Count);
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("gate")]
    [InlineData("origin")]
    [InlineData("overlap")]
    [InlineData("lost_children")]
    public async Task CorruptedPartitionTreeCannotAuthorizeDispatchOrReuse(string corruption)
    {
        var state = TypedPlannerTests.Session(); var decisions = FiveDecisions();
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) => Task.FromResult(PartitionResponse(request)) };
        await PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main", decisions, TestContext.Current.CancellationToken);
        var child = state.DecisionPages[1];
        switch (corruption)
        {
            case "membership": child.Decisions[0] = "foreign"; break;
            case "missing": state.DecisionPages.Remove(child); break;
            case "foreign": state.DecisionPages.Add(new() { Id = "foreign", ParentId = state.DecisionPages[0].Id }); break;
            case "gate": child.Gate = PlanningGates.Semantic; break;
            case "origin": child.Origin = PlanningDecisionPageOrigin.Unknown; break;
            case "overlap": state.DecisionPages[0].PartitionChildren[1] = child.Id; break;
            case "lost_children": state.DecisionPages[0].PartitionChildren.Clear(); break;
        }
        await Assert.ThrowsAsync<PlanningConflictException>(() => PlanningDecisionPages.ResolveAsync(PlanningContext.Clone(state), runtime, "behavior", "main", decisions, TestContext.Current.CancellationToken));
        Assert.Equal(5, runtime.Requests.Count);
    }

    [Theory]
    [InlineData("budget")]
    [InlineData("cancelled")]
    [InlineData("unverifiable")]
    public async Task PartitionDispatchStopsOnRuntimeFailureWithoutRetryOrRepair(string failure)
    {
        var state = TypedPlannerTests.Session(); var decisions = FiveDecisions(); var calls = 0;
        var runtime = new TypedPlannerTests.FakeRuntime { OnCall = (_, request, _) =>
        {
            if (++calls == 1) return Task.FromResult(PartitionResponse(request));
            if (failure == "cancelled") throw new OperationCanceledException();
            throw new GnOuGo.Flow.Core.Expressions.WorkflowRuntimeException(failure == "budget" ? "LLM_BUDGET_EXCEEDED" : "MODEL_DISPATCH_UNVERIFIABLE", "Stopped by durable runtime.");
        } };
        await Assert.ThrowsAnyAsync<Exception>(() => PlanningDecisionPages.ResolveAsync(state, runtime, "behavior", "main", decisions, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls); Assert.Equal(2, state.RequestAccounting.Count);
        Assert.Single(state.Construction.PendingCalls); Assert.Empty(state.DecisionCorrections); Assert.Empty(state.RepairAllowances);
        Assert.Equal("unverifiable", state.RequestAccounting[1].Evidence);
        var restored = PlanningContext.Clone(state);
        var reserved = PlanningDecisionPages.Next(restored, "behavior", "main", decisions)!;
        Assert.Equal(state.Construction.PendingCalls[0].Id, reserved.Call.Id);
        Assert.Equal(2, restored.RequestAccounting.Count); // Resume retains journal identity; it does not dispatch.
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
