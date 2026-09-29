using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class AgentInterruptionRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedBudgetStopPreservesLeafAndOnlyConfirmedCessationEnablesCleanup(bool uncertain)
    {
        var store = new InMemoryWorkflowRunStore(); var runner = new Runner { Uncertain = uncertain }; var cleanup = new Cleanup();
        var engine = Engine(store, runner, cleanup); var workflow = new WorkflowCompiler().Compile(WorkflowParser.Parse(Yaml)).Workflows["main"];
        var ct = TestContext.Current.CancellationToken;
        var result = await engine.ExecuteAsync(workflow, null, ct);
        Assert.False(result.Success);
        Assert.Equal(uncertain ? "RUN_NEEDS_RECONCILIATION" : "AGENT_BUDGET_EXHAUSTED", result.Error!.Code);
        Assert.Equal("work", result.Error.Details!["failed_step_id"]!.ToString());
        var run = (await store.ReadAsync("tenant", "run", ct))!;
        var work = Assert.Single(run.Invocations.Values, i => i.StepType == "agent.run");
        Assert.Equal(uncertain ? 0 : 2, cleanup.Calls);
        Assert.Equal(!uncertain, work.ExternalCompletionObserved);
        Assert.Equal("AGENT_BUDGET_EXHAUSTED", work.Observation!["failure"]!["code"]!.ToString());
        if (uncertain)
        {
            Assert.Equal(work.Id, result.Error.Details["invocation_id"]!.ToString());
            Assert.True(result.Error.Details["cleanup_blocked"]!.GetValue<bool>());
            Assert.Equal(work.Id, Assert.Single(result.Error.Details["unresolved_invocation_ids"]!.AsArray())!.ToString());
            var stopped = await engine.ResumeAsync("tenant", "run", run.Revision, workflow, ct);
            Assert.Equal("RUN_NEEDS_RECONCILIATION", stopped.Error!.Code); Assert.Equal(1, runner.Dispatches);
            run = (await store.ReadAsync("tenant", "run", ct))!;
            run = await engine.ReconcileAsync("tenant", "run", run.Revision, work.Id, null, ct);
            Assert.Equal(1, runner.Inspections); Assert.False(run.Invocations[work.Id].ExternalCompletionObserved);
            runner.Uncertain = false;
            run = await engine.ReconcileAsync("tenant", "run", run.Revision, work.Id, null, ct);
            Assert.Equal(2, runner.Inspections); Assert.Equal("AGENT_BUDGET_EXHAUSTED", run.Invocations[work.Id].Error!.Code);
            result = await engine.ResumeAsync("tenant", "run", run.Revision, workflow, ct);
            Assert.Equal("AGENT_BUDGET_EXHAUSTED", result.Error!.Code); Assert.Equal(2, cleanup.Calls);
        }
        run = (await store.ReadAsync("tenant", "run", ct))!;
        var count = run.StepsStarted;
        await engine.ResumeAsync("tenant", "run", run.Revision, workflow, ct);
        Assert.Equal(1, runner.Dispatches); Assert.Equal(2, cleanup.Calls);
        Assert.Equal(count, (await store.ReadAsync("tenant", "run", ct))!.StepsStarted);
        Assert.Null(await store.ReadAsync("other", "run", ct));
    }

    private static WorkflowEngine Engine(IWorkflowRunStore store, Runner runner, Cleanup cleanup)
    {
        var engine = new WorkflowEngine() { RunStore = store, Limits = new() { TenantId = "tenant", RunId = "run" } };
        engine.Registry.Register(cleanup); engine.AgentTaskRunners["renamed-runner"] = runner; return engine;
    }
    private sealed class Cleanup : IStepExecutor
    {
        public string StepType => "fixture.release";
        internal int Calls;
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext context, CancellationToken ct)
        { Calls++; return Task.FromResult<JsonNode?>(new JsonObject { ["released"] = true }); }
    }
    private sealed class Runner : IAgentTaskRunner
    {
        internal bool Uncertain; internal int Dispatches; internal int Inspections;
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct) { Dispatches++; return Task.FromResult(Result()); }
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) { Inspections++; return Task.FromResult(Result()); }
        private AgentTaskResult Result() => new(Uncertain ? "needs_reconciliation" : "budget_exhausted", null, [], [], new(4, 167402, 324299) { Metering = "reserved_upper_bound" })
        { Failure = new() { Code = "AGENT_BUDGET_EXHAUSTED", Message = "The approved allowance cannot admit another request.",
            Details = new JsonObject { ["dimension"] = "tokens", ["charged_tokens"] = 167402, ["max_total_tokens"] = 200000 } } };
    }
    private const string Yaml = """
        version: 1
        workflows:
          main:
            steps:
              - {id: child, type: workflow.call, input: {ref: {kind: local, name: nested}}}
            finally:
              - {id: outer_release, type: fixture.release}
          nested:
            steps:
              - id: work
                type: agent.run
                input:
                  runner: renamed-runner
                  objective: Inspect fixture data
                  workspace: fixture
                  capabilities: [fixture.read]
                  output_schema: {type: object}
                  budget: {max_elapsed_milliseconds: 1800000, max_model_calls: 20, max_total_tokens: 200000}
                  verification:
                    - {id: observed, kind: fixture, subject: result, facts_schema: {type: object}}
            finally:
              - {id: inner_release, type: fixture.release}
        """;
}
