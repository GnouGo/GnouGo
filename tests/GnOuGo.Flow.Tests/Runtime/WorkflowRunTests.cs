using System.Text.Json.Nodes;
using Xunit;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowRunTests
{
    private const string Simple = """
        version: 1
        workflows:
          main:
            steps:
              - { id: write, type: test.effect, input: { value: written } }
              - { id: consume, type: set, input: { value: "${data.steps.write.value}" } }
            finally:
              - { id: cleanup, type: test.effect, input: { value: cleanup } }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrashAroundReceipt_NeverRepeatsUncertainEffect_AndDefersCleanup(bool receiptPersisted)
    {
        var store = new InMemoryWorkflowRunStore();
        var effect = new Effect();
        var fault = new FaultStore(store, r => r.Invocations.Values.Any(i => i.Id.EndsWith("/write") && i.Status == "completed"), receiptPersisted);
        var engine = Engine(fault, effect);
        await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteAsync(Compile(Simple), null, TestContext.Current.CancellationToken));
        Assert.Equal(["written"], effect.Values);
        var saved = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        Assert.False(saved.FinalizationStarted);
        var recovered = Engine(store, effect);
        var result = await recovered.ResumeAsync("tenant", "run", saved.Revision, Compile(Simple), TestContext.Current.CancellationToken);
        if (receiptPersisted)
        {
            Assert.True(result.Success, result.Error?.Message);
            Assert.Equal(["written", "cleanup"], effect.Values);
        }
        else
        {
            Assert.Equal("RUN_NEEDS_RECONCILIATION", result.Error?.Code);
            Assert.Equal(["written"], effect.Values);
            saved = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
            var reconciled = await recovered.ReconcileAsync("tenant", "run", saved.Revision, "/workflow/main/step/write",
                "Operator observed the external operation stopped; its result is unavailable.", TestContext.Current.CancellationToken);
            result = await recovered.ResumeAsync("tenant", "run", reconciled.Revision, Compile(Simple), TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Equal(["written", "cleanup"], effect.Values);
        }
    }

    [Fact]
    public async Task CrashBeforeDispatch_ResumesPreparedIntent_WithoutReplenishingStepBudget()
    {
        var store = new InMemoryWorkflowRunStore();
        var effect = new Effect();
        var fault = new FaultStore(store, r => r.Events.Last().Kind == "dispatch", false);
        var engine = Engine(fault, effect);
        engine.Limits.MaxTotalStepsExecuted = 2;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteAsync(Compile(Simple), null, TestContext.Current.CancellationToken));
        Assert.Empty(effect.Values);
        var run = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        var result = await Engine(store, effect).ResumeAsync("tenant", "run", run.Revision, Compile(Simple), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        run = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        Assert.Equal(2, run.StepsStarted);
        Assert.Equal(1, run.FinalizationStepsStarted);
        Assert.Equal(2, run.Limits.MaxTotalStepsExecuted);
    }

    [Fact]
    public async Task NestedCallsLoopsAndParallelBranches_HaveDistinctReceiptsAcrossRecovery()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: fanout
                    type: parallel
                    branches:
                      - steps:
                          - id: call_left
                            type: workflow.call
                            input: { ref: { kind: local, name: child }, args: { value: first } }
                      - steps:
                          - id: call_right
                            type: workflow.call
                            input: { ref: { kind: local, name: child }, args: { value: second } }
              child:
                steps:
                  - id: repeat
                    type: loop.sequential
                    input: { times: 2 }
                    steps:
                      - id: write
                        type: test.effect
                        input: { value: "${data.inputs.value}" }
            """;
        var store = new InMemoryWorkflowRunStore();
        var effects = new Effect();
        var fault = new FaultStore(store, r => r.Invocations.Values.Count(i => i.StepType == "test.effect" && i.Status == "completed") == 4, true);
        await Assert.ThrowsAnyAsync<Exception>(() => Engine(fault, effects).ExecuteAsync(Compile(yaml), null, TestContext.Current.CancellationToken));
        Assert.Equal(4, effects.Values.Count);
        var run = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        var result = await Engine(store, effects).ResumeAsync("tenant", "run", run.Revision, Compile(yaml), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(4, effects.Values.Count);
        run = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        var ids = run.Invocations.Values.Where(i => i.StepType == "test.effect").Select(i => i.Id).ToArray();
        Assert.Equal(4, ids.Distinct().Count());
        Assert.Contains(ids, id => id.Contains("/branch/1/step/call_right/workflow/child/step/repeat/iteration/1/step/write"));
    }

    [Fact]
    public async Task HumanInput_IsRetainedAndPresentedWithStableInvocationIdentity()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - { id: before, type: test.effect, input: { value: before } }
                  - { id: ask, type: human.input, input: { mode: text, prompt: "Answer?" } }
                  - { id: after, type: set, input: { value: "${data.steps.ask.response}" } }
                finally:
                  - { id: cleanup, type: test.effect, input: { value: cleanup } }
            """;
        var store = new InMemoryWorkflowRunStore();
        var effects = new Effect();
        var first = Engine(store, effects);
        var interrupted = new Human(null);
        first.HumanInputProvider = interrupted;
        var stopped = await first.ExecuteAsync(Compile(yaml), null, TestContext.Current.CancellationToken);
        Assert.False(stopped.Success);
        var run = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        Assert.Equal(WorkflowRunStatus.WaitingForHuman, run.Status);
        Assert.Equal(["before"], effects.Values);
        Assert.Equal("Answer?", run.Invocations[interrupted.Id!].ResolvedInput!["prompt"]!.GetValue<string>());
        var resumed = Engine(store, effects);
        var answered = new Human("42");
        resumed.HumanInputProvider = answered;
        var result = await resumed.ResumeAsync("tenant", "run", run.Revision, Compile(yaml), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(interrupted.Id, answered.Id);
        Assert.Equal("42", result.StepResults.Single(s => s.StepId == "after").Output!["value"]!.GetValue<string>());
        Assert.Equal(["before", "cleanup"], effects.Values);
    }

    [Fact]
    public async Task OwnershipRevisionAndTenantIsolation_AreEnforced()
    {
        var store = new InMemoryWorkflowRunStore();
        await store.CreateAsync(new WorkflowRun { TenantId = "tenant", RunId = "run", Limits = new() { TenantId = "tenant", RunId = "run" } }, TestContext.Current.CancellationToken);
        Assert.Null(await store.ReadAsync("other", "run", TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListAsync("other", TestContext.Current.CancellationToken));
        await using var owner = await store.AcquireAsync("tenant", "run", 0, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => store.AcquireAsync("tenant", "run", 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => store.CancelAsync("tenant", "run", 1, TestContext.Current.CancellationToken));
        await store.CancelAsync("tenant", "run", 0, TestContext.Current.CancellationToken);
        Assert.True(await owner.IsCancellationRequestedAsync(TestContext.Current.CancellationToken));
        await owner.SaveAsync(TestContext.Current.CancellationToken);
        Assert.True(owner.Run.CancelRequested);
        Assert.Equal(2, owner.Run.Revision);
    }

    [Fact]
    public async Task ResumeRejectsChangedWorkflow_AndOldSchemaIsNeverMutated()
    {
        var store = new InMemoryWorkflowRunStore();
        var engine = Engine(store, new Effect());
        Assert.True((await engine.ExecuteAsync(Compile(Simple), null, TestContext.Current.CancellationToken)).Success);
        var saved = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        await Assert.ThrowsAsync<WorkflowRunConflictException>(() => engine.ResumeAsync("tenant", "run", saved.Revision,
            Compile(Simple.Replace("written", "changed")), TestContext.Current.CancellationToken));
        const string legacy = "{\"schemaVersion\":8,\"tenantId\":\"tenant\",\"runId\":\"run\"}";
        var error = Assert.Throws<WorkflowRunConflictException>(() => WorkflowRunStorage.Read(legacy, "tenant", "run"));
        Assert.Contains("Regenerate and approve", error.Message);
        Assert.Equal(saved.Revision, (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!.Revision);
    }

    [Fact]
    public async Task AnswerCommittedBeforeAcknowledgement_IsConsumedAfterProcessRestart()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - { id: ask, type: human.input, input: { mode: text, prompt: "Answer?" } }
                  - { id: consume, type: set, input: { value: "${data.steps.ask.response}" } }
            """;
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryWorkflowRunStore();
        var initial = Engine(store, new Effect());
        initial.HumanInputProvider = new Human(null);
        await initial.ExecuteAsync(Compile(yaml), null, ct);
        var run = (await store.ReadAsync("tenant", "run", ct))!;
        run = await store.AnswerAsync("tenant", "run", run.Revision, "/workflow/main/step/ask", new JsonObject { ["response"] = "42" }, ct);
        var restarted = Engine(store, new Effect());
        var human = new Human("incorrect");
        restarted.HumanInputProvider = human;
        var result = await restarted.ResumeAsync("tenant", "run", run.Revision, Compile(yaml), ct);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Null(human.Id);
        Assert.Equal("42", result.StepResults.Single(s => s.StepId == "consume").Output!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task ParallelNestedFinalization_WaitsUntilOtherExternalWorkStops()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: fanout
                    type: parallel
                    branches:
                      - steps:
                          - { id: left, type: workflow.call, input: { ref: { kind: local, name: child } } }
                      - steps:
                          - { id: right, type: workflow.call, input: { ref: { kind: local, name: child } } }
              child:
                steps:
                  - { id: work, type: test.active }
                finally:
                  - { id: cleanup, type: test.cleanup }
            """;
        var store = new InMemoryWorkflowRunStore();
        var state = new ActiveState();
        var engine = Engine(store, new Effect());
        engine.Registry.Register(new ActiveEffect(state));
        engine.Registry.Register(new CleanupEffect(state));
        var result = await engine.ExecuteAsync(Compile(yaml), null, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, state.Cleanups);
        Assert.False(state.Raced);
    }

    private sealed class ActiveState
    {
        public int Count, Entered, Cleanups;
        public bool Raced;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ActiveEffect(ActiveState state) : IStepExecutor
    {
        public string StepType => "test.active";
        public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref state.Count);
            if (Interlocked.Increment(ref state.Entered) == 2) state.Started.TrySetResult();
            await state.Started.Task.WaitAsync(ct);
            if (ctx.InvocationId.Contains("branch/1", StringComparison.Ordinal)) await Task.Delay(40, ct);
            Interlocked.Decrement(ref state.Count);
            return new JsonObject();
        }
    }
    private sealed class CleanupEffect(ActiveState state) : IStepExecutor
    {
        public string StepType => "test.cleanup";
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            if (Volatile.Read(ref state.Count) != 0) state.Raced = true;
            Interlocked.Increment(ref state.Cleanups);
            return Task.FromResult<JsonNode?>(new JsonObject());
        }
    }

    private static CompiledWorkflow Compile(string yaml)
    {
        var compiled = new WorkflowCompiler().Compile(WorkflowParser.Parse(yaml));
        return compiled.Workflows[compiled.Entrypoint!];
    }
    private static WorkflowEngine Engine(IWorkflowRunStore store, Effect effect)
    {
        var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "tenant", RunId = "run" } };
        engine.Registry.Register(effect);
        return engine;
    }
    private sealed class Effect : IStepExecutor
    {
        public string StepType => "test.effect";
        public List<string> Values { get; } = [];
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        {
            var input = ctx.Engine.GetResolvedInput(ctx);
            lock (Values) Values.Add(input!["value"]!.GetValue<string>());
            return Task.FromResult(input?.DeepClone());
        }
    }
    private sealed class Human(string? answer) : IHumanInputProvider
    {
        public string? Id { get; private set; }
        public Task<JsonNode?> RequestInputAsync(HumanInputRequest request, CancellationToken ct)
        {
            Id = request.StepId;
            if (answer is null) throw new OperationCanceledException();
            return Task.FromResult<JsonNode?>(new JsonObject { ["response"] = answer });
        }
    }
    private sealed class FaultStore(IWorkflowRunStore inner, Func<WorkflowRun, bool> fault, bool after) : IWorkflowRunStore
    {
        private bool _fired;
        private readonly Func<WorkflowRun, bool> _fault = fault;
        private readonly bool _after = after;
        public Task<WorkflowRun?> ReadAsync(string tenantId, string runId, CancellationToken ct = default) => inner.ReadAsync(tenantId, runId, ct);
        public Task<IReadOnlyList<WorkflowRun>> ListAsync(string tenantId, CancellationToken ct = default) => inner.ListAsync(tenantId, ct);
        public Task CreateAsync(WorkflowRun run, CancellationToken ct = default) => inner.CreateAsync(run, ct);
        public Task<WorkflowRun> AnswerAsync(string tenantId, string runId, long expectedRevision, string invocationId, JsonNode? response, CancellationToken ct = default)
            => inner.AnswerAsync(tenantId, runId, expectedRevision, invocationId, response, ct);
        public Task<WorkflowRun> CancelAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default) => inner.CancelAsync(tenantId, runId, expectedRevision, ct);
        public async Task<IWorkflowRunLease> AcquireAsync(string tenantId, string runId, long expectedRevision, CancellationToken ct = default)
            => new FaultLease(this, await inner.AcquireAsync(tenantId, runId, expectedRevision, ct));
        private sealed class FaultLease(FaultStore owner, IWorkflowRunLease innerLease) : IWorkflowRunLease
        {
            public WorkflowRun Run => innerLease.Run;
            public async Task SaveAsync(CancellationToken ct = default)
            {
                var fail = !owner._fired && owner._fault(Run);
                if (fail) owner._fired = true;
                if (fail && !owner._after) throw new IOException("Injected persistence crash before commit.");
                await innerLease.SaveAsync(ct);
                if (fail) throw new IOException("Injected process crash after commit.");
            }
            public Task<bool> IsCancellationRequestedAsync(CancellationToken ct = default) => innerLease.IsCancellationRequestedAsync(ct);
            public ValueTask DisposeAsync() => innerLease.DisposeAsync();
        }
    }
}
