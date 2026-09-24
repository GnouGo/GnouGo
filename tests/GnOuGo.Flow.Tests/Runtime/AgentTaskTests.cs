using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class AgentTaskTests
{
    [Fact]
    public async Task CompletedTurnWithoutExecutionEvidenceCannotSatisfyTask()
    {
        var runner = new Runner { Result = Result() with { Evidence = [] } };
        var result = await Execute(runner);
        Assert.False(result.Success);
        Assert.Equal("AGENT_VERIFICATION_FAILED", result.Error?.Code);
        Assert.DoesNotContain(result.StepResults, s => s.StepId == "consume");
    }

    [Fact]
    public async Task VerifiedOutputCanCrossTheStageBoundary()
    {
        var result = await Execute(new Runner());
        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Outputs?["done"]?.GetValue<bool>());
    }

    [Fact]
    public async Task UnsupportedScopeStopsBeforeDispatch()
    {
        var runner = new Runner { Rejections = ["Cannot enforce the requested capability scope."] };
        var result = await Execute(runner);
        Assert.Equal("AGENT_SCOPE_UNSUPPORTED", result.Error?.Code);
        Assert.Equal(0, runner.Dispatches);
    }

    [Theory]
    [InlineData("output", "AGENT_OUTPUT_INVALID")]
    [InlineData("usage", "AGENT_BUDGET_EXCEEDED")]
    [InlineData("uncertain", "AGENT_OUTCOME_UNCERTAIN")]
    [InlineData("conflict", "AGENT_VERIFICATION_FAILED")]
    public async Task InvalidResultsDoNotReachConsumers(string defect, string code)
    {
        var baseline = Result();
        var runner = new Runner { Result = defect switch
        {
            "output" => baseline with { Output = new JsonObject { ["done"] = "claimed" } },
            "usage" => baseline with { Usage = new(9, 50, 1) },
            "uncertain" => baseline with { Status = "needs_reconciliation" },
            "conflict" => baseline with { Evidence = [.. baseline.Evidence, new("other", "process", "tests", new() { ["exit_code"] = 1 })] },
            _ => throw new InvalidOperationException()
        }};
        var result = await Execute(runner);
        Assert.False(result.Success);
        Assert.Equal(code, result.Error?.Code);
        Assert.DoesNotContain(result.StepResults, s => s.StepId == "consume");
    }

    [Fact]
    public async Task RejectedScopeIsARecordedFailureWithoutUncertainExternalWork()
    {
        var store = new InMemoryWorkflowRunStore();
        var runner = new Runner { Rejections = ["Unsupported capability"] };
        await Execute(runner, store);
        var run = await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken);
        Assert.Equal(0, runner.Dispatches);
        var invocation = Assert.Single(run!.Invocations.Values, i => i.StepType == "agent.run");
        Assert.Equal("failed", invocation.Status); Assert.NotNull(invocation.CompletedAt);
        Assert.True(invocation.ExternalCompletionObserved);
    }
    [Fact]
    public async Task FailedVerificationRetainsObservedEvidenceAndFindingsInJournal()
    {
        var store = new InMemoryWorkflowRunStore();
        await Execute(new Runner { Result = Result() with { Evidence = [] } }, store);
        var run = await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken);
        var invocation = Assert.Single(run!.Invocations.Values, i => i.StepType == "agent.run");
        Assert.False(invocation.Observation!["verification"]![0]!["passed"]!.GetValue<bool>());
        Assert.Equal("failed", invocation.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryVerifiesAdapterEvidenceBeforeReleasingDownstreamWork(bool fabricated)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryWorkflowRunStore();
        var initial = new Runner { Result = Result() with { Status = "needs_reconciliation" } };
        await Execute(initial, store);
        var saved = (await store.ReadAsync("tenant", "run", ct))!;
        Assert.Equal(WorkflowRunStatus.NeedsReconciliation, saved.Status);
        var runner = new Runner { Result = fabricated ? Result() with { Evidence = [] } : Result() };
        var recovered = new WorkflowEngine { RunStore = store }; recovered.AgentTaskRunners["fixture"] = runner;
        var invocation = Assert.Single(saved.Invocations.Values, i => i.StepType == "agent.run");
        var reconciled = await recovered.ReconcileAsync("tenant", "run", saved.Revision, invocation.Id, null, ct);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(saved.WorkflowYaml));
        var result = await recovered.ResumeAsync("tenant", "run", reconciled.Revision, document.Workflows["main"], ct);
        Assert.Equal(!fabricated, result.Success);
        Assert.Equal(0, runner.Dispatches); Assert.Equal(1, initial.Dispatches);
        if (fabricated) Assert.DoesNotContain(result.StepResults, s => s.StepId == "consume");
        else Assert.True(result.Outputs!["done"]!.GetValue<bool>());
    }
    private static Task<RunResult> Execute(Runner runner, IWorkflowRunStore? store = null)
    {
        var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "tenant", RunId = "run" } };
        engine.AgentTaskRunners["fixture"] = runner;
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: work
                    type: agent.run
                    input:
                      runner: fixture
                      objective: Complete the requested task and verify tests
                      workspace: fixture-workspace
                      capabilities: [fixture-execution]
                      output_schema:
                        type: object
                        required: [done]
                        properties:
                          done: { type: boolean }
                      budget:
                        max_elapsed_milliseconds: 10000
                        max_model_calls: 8
                        max_total_tokens: 1000
                      verification:
                        - id: tests
                          kind: process
                          subject: tests
                          facts_schema:
                            type: object
                            required: [exit_code]
                            properties:
                              exit_code: { type: integer, const: 0 }
                  - id: consume
                    type: set
                    input: { done: "${data.steps.work.output.done}" }
                outputs:
                  done: "${data.steps.consume.done}"
            """));
        return engine.ExecuteAsync(document.Workflows["main"], null, TestContext.Current.CancellationToken);
    }

    private static AgentTaskResult Result() => new("completed", new JsonObject { ["done"] = true },
        [new("test-receipt", "process", "tests", new() { ["exit_code"] = 0 })], [], new(1, 50, 1));

    private sealed class Runner : IAgentTaskRunner
    {
        public AgentTaskResult Result { get; init; } = AgentTaskTests.Result();
        public IReadOnlyList<string> Rejections { get; init; } = [];
        public int Dispatches { get; private set; }
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct) => Task.FromResult(Rejections);
        public Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct) { Dispatches++; return Task.FromResult(Result); }
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) => Task.FromResult(Result);
    }
}
