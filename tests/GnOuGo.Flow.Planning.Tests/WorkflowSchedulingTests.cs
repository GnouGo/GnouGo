using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Planning;
using GnOuGo.Flow.Core.Runtime;
using static GnOuGo.Flow.Planning.Tests.TypedPlannerTests;
using static GnOuGo.Flow.Planning.Tests.HoleSessionTests;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class WorkflowSchedulingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IndependentCalleesRunTogetherAndCommitIdenticallyInEitherCompletionOrder()
    {
        var reverse = await Construct(reverse: true);
        var forward = await Construct(reverse: false);
        Assert.Equal(reverse, forward);
    }

    private static async Task<string> Construct(bool reverse)
    {
        var behavior = BehaviorPlan(); var main = behavior.Workflows[0];
        var keys = new[] { "a", "b", "c", "d" };
        main.Steps = keys.Select(k => new PlanningBehaviorNode { Key = "call_" + k, Kind = "workflow", WorkflowKey = k, Purpose = "Call the declared subworkflow", InputDependencies = [] }).ToList();
        foreach (var key in keys)
        {
            var callee = BehaviorPlan().Workflows[0]; callee.Key = key;
            behavior.Workflows.Add(callee);
        }
        var state = Ready(behavior); var reservations = 0; var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = keys.ToDictionary(k => k, _ => new TaskCompletionSource<LLMResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
        var constructed = keys.ToDictionary(k => k, k => { var w = FakeRuntime.ExecutableWorkflow(); w.Key = k; return w; });
        var caller = new PlanningWorkflow
        {
            Key = "main",
            Purpose = main.Purpose,
            Steps = keys.Select(k => new PlanningNode { Key = "call_" + k, Type = "workflow.call", Input = Obj(("ref", new() { Kind = "workflow", Source = k }), ("args", Obj())) }).ToList(),
            Outputs = [new() { Name = "message", Schema = new() { Type = "string" }, Value = new() { Kind = "output", Source = "call_d", Path = ["message"] } }]
        };
        var callers = 0; var round = 0;
        var fixture = new PlanningGraph { Workflows = new[] { caller }.Concat(keys.Select(k => constructed[k])).ToList() };
        var runtime = new FakeRuntime();
        runtime.OnCall = (phase, request, _) =>
        {
            if (phase == "semantic_review") return Task.FromResult(new LLMResponse { Json = new JsonObject { ["findings"] = new JsonArray() } });
            Assert.Equal(PlanningPhase.Construction, phase);
            var key = state.Construction.PendingCalls.Single(c => c.Id == request.ClientRequestId).WorkflowKey;
            if (key == "main")
            {
                Assert.All(state.Construction.Workflows.Where(w => w.WorkflowKey != "main"), w => Assert.Equal("validated", w.Status));
                Assert.DoesNotContain("Hello", request.Prompt);
                callers++;
                return Task.FromResult(new LLMResponse { Json = runtime.FillHoles(request, fixture) });
            }
            Assert.Equal(4, reservations);
            if (round == 0)
            {
                if (Interlocked.Increment(ref started) == 4) allStarted.TrySetResult();
                return completions[key].Task;
            }
            return Task.FromResult(new LLMResponse { Json = runtime.FillHoles(request, fixture) });
        };
        runtime.OnCheckpoint = snapshot => { state = snapshot; reservations = snapshot.Construction.PendingCalls.Count; return Task.CompletedTask; };
        var pending = Advance(state, runtime);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        foreach (var key in reverse ? keys.Reverse() : keys)
        {
            var request = state.Construction.PendingCalls.Single(c => c.WorkflowKey == key).Request;
            completions[key].SetResult(new LLMResponse { Json = runtime.FillHoles(request, fixture) });
        }
        state = await pending; round++;
        Assert.Equal(0, callers);
        Assert.Equal(["main", "a", "b", "c", "d"], state.Graph!.Workflows.Select(w => w.Key));
        for (var i = 0; i < 20 && !PlanningStatus.IsWaiting(state.Status); i++) state = await Advance(state, runtime);
        Assert.True(state.Status == PlanningStatus.FinalReview, string.Join("; ", state.Diagnostics.Select(d => d.Message)));
        Assert.True(callers > 0);
        Assert.All(state.Construction.Workflows, w => Assert.Equal(2, w.Calls));
        return state.Yaml!;
    }

    [Fact]
    public async Task AssignmentResponseCannotRemoveAnAcceptedFinalizer()
    {
        var behavior = BehaviorPlan(); behavior.Workflows[0].Finally.Add(new() { Key = "cleanup", Purpose = "Release the temporary state", InputDependencies = [] });
        var state = Ready(behavior); var original = PlanningGraphCompiler.Fingerprint(state.Graph!);
        var runtime = new FakeRuntime { OnCall = (_, _, _) => Task.FromResult(new LLMResponse
            { Json = new JsonObject { ["assignments"] = new JsonObject(), ["workflow"] = PlanningFixtures.Workflow(FakeRuntime.ExecutableWorkflow()) } }) };
        state = await Advance(state, runtime);
        Assert.Equal(PlanningPhase.Repair, state.CurrentPhase);
        Assert.NotEmpty(Assert.Single(state.Construction.Candidates).Diagnostics);
        Assert.Equal("cleanup", Assert.Single(state.Graph!.Workflows[0].Finally).Key);
        Assert.Equal(original, PlanningGraphCompiler.Fingerprint(state.Graph!));
    }
}
