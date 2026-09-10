using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class WholeWorkflowConstructionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static PlanningSnapshot Ready(PlanningBehaviorPlan? behavior = null)
    {
        var state = Session(PlanningStatus.Generating);
        state.Request.MaxRepairs = 3;
        state.BehaviorPlan = behavior ?? BehaviorPlan(); state.Preparation = Preparation();
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        state.Graph = PlanningBehaviorPlans.Display(state.BehaviorPlan, state.Preparation);
        PlanningDataflowResolver.Resolve(state);
        return state;
    }
    internal static Task<PlanningSnapshot> Advance(PlanningSnapshot state, IPlanningRuntime runtime, string kind = "advance") =>
        new TypedWorkflowPlanner().AdvanceAsync(state, new() { Kind = kind, ExpectedRevision = state.Revision, ArtifactHash = state.ArtifactHash }, runtime, Ct);
    private static PlanningSnapshot Clone(PlanningSnapshot state) => JsonSerializer.Deserialize(
        JsonSerializer.Serialize(state, PlanningJsonContext.Default.PlanningSnapshot), PlanningJsonContext.Default.PlanningSnapshot)!;

    [Fact]
    public async Task CompleteWorkflowPassesEveryGateAndExactApproval()
    {
        var state = Ready(); var runtime = new FakeRuntime();
        for (var i = 0; i < 8 && !PlanningStatus.IsWaiting(state.Status); i++) state = await Advance(state, runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, JsonSerializer.Serialize(state.Diagnostics, PlanningJsonContext.Default.ListPlanningDiagnostic));
        Assert.Single(runtime.Phases, p => p == "construction");
        Assert.Contains("semantic_review", runtime.Phases);
        Assert.True(runtime.ScenarioCalls > 0);
        Assert.Equal(5, state.Validation.Stage);
        Assert.Equal(PlanningGraphCompiler.Fingerprint(state.Yaml!), state.ArtifactHash);
        state = await Advance(state, runtime, "approve");
        Assert.Equal(PlanningStatus.Approved, state.Status);
    }

    [Fact]
    public async Task TruncatedWorkflowPausesWithoutCommittingOrRedispatching()
    {
        var state = Ready(); var before = PlanningGraphCompiler.Fingerprint(state.Graph!);
        var runtime = new FakeRuntime
        {
            OnCall = (_, request, _) =>
        {
            Assert.Equal(8_192, request.MaxTokens); Assert.True(request.DisableTransportRetries);
            return Task.FromResult(new LLMResponse { CompletionStatus = "output_limit" });
        }
        };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_OUTPUT_LIMIT");
        Assert.Empty(state.Construction.PendingCalls);
        Assert.Equal(before, PlanningGraphCompiler.Fingerprint(state.Graph!));
        state = await Advance(state, runtime); Assert.Single(runtime.Requests);
    }

    [Fact]
    public async Task OversizedRequestPausesBeforeDispatch()
    {
        var state = Ready(); state.Request.Generation.MaxInputTokensPerRequest = 1_000;
        state.BehaviorPlan!.Workflows[0].Purpose = new string('x', 40_000);
        state.ApprovedBehaviorHash = PlanningBehaviorPlans.Fingerprint(state.BehaviorPlan);
        var runtime = new FakeRuntime(); state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == "MODEL_INPUT_LIMIT");
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task PendingRequestReusesIdentityAndExactPayloadAfterRestart()
    {
        var state = Ready(); PlanningSnapshot? durable = null;
        var runtime = new FakeRuntime
        {
            OnCheckpoint = s => { durable = Clone(s); return Task.CompletedTask; },
            OnCall = (_, _, _) => throw new IOException("Interrupted after reservation")
        };
        state = await Advance(state, runtime);
        var pending = Assert.Single(durable!.Construction.PendingCalls);
        state = await Advance(durable, runtime, "retry");
        var replay = new FakeRuntime(); state = await Advance(state, replay);
        var actual = Assert.Single(replay.Requests);
        Assert.Equal(pending.Id, actual.ClientRequestId);
        Assert.Equal(pending.Request.Prompt, actual.Prompt);
        Assert.Equal(1, state.Construction.Workflows[0].Calls);
    }

    [Fact]
    public async Task ActiveTimeLimitInterruptsDispatchAndRetainsItsReservation()
    {
        var state = Ready(); state.Request.Options["llm_budget"] = new JsonObject { ["max_elapsed_ms"] = 100 };
        state.WaitingSinceUtc = DateTimeOffset.UtcNow.AddHours(-4);
        var runtime = new FakeRuntime
        {
            OnCall = async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token); return new LLMResponse();
        }
        };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningStatus.Recovery, state.Status);
        Assert.Contains(state.Diagnostics, d => d.Code == GnOuGo.Flow.Core.Models.ErrorCodes.LlmBudgetExceeded);
        Assert.Single(state.Construction.PendingCalls);
        Assert.True(state.HumanWaitMilliseconds >= TimeSpan.FromHours(4).TotalMilliseconds);
        Assert.InRange(state.ActiveMilliseconds, 90, 5_000);
    }

    [Fact]
    public void DependenciesIncludeNestedFinalizersAndRejectCyclesAndMissingTargets()
    {
        var state = Ready(); var graph = state.Graph!;
        graph.Workflows.Add(new() { Key = "cleanup" });
        graph.Workflows[0].Finally.Add(new() { Key = "outer", Type = "sequence", Steps = [Call("cleanup")] });
        PlanningDataflowResolver.Resolve(state);
        Assert.Equal(["cleanup"], state.Construction.Workflows[0].Dependencies);
        graph.Workflows[1].Steps.Add(Call("main"));
        Assert.Contains("cycle", Assert.Throws<InvalidOperationException>(() => PlanningDataflowResolver.Resolve(state)).Message);
        graph.Workflows[1].Steps.Clear(); graph.Workflows.RemoveAt(1);
        Assert.Contains("callee", Assert.Throws<InvalidOperationException>(() => PlanningDataflowResolver.Resolve(state)).Message);
    }

    private static PlanningNode Call(string key) => new() { Key = "call_" + key, Type = "workflow.call", Input = Obj(("ref", new() { Kind = "workflow", Source = key }), ("args", Obj())) };
}
