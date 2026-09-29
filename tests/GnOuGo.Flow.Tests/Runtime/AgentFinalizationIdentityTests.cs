using System.Text.Json.Nodes;
using Xunit;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class AgentFinalizationIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FinalizersKeepOwnershipAndDistinctInvocationsThroughNestedCallsAndRecovery(bool nested, bool fail)
    {
        var runner = new Runner();
        var store = new InMemoryWorkflowRunStore();
        var engine = Engine(runner); engine.RunStore = store;
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(Yaml(nested, fail)));
        var result = await engine.ExecuteAsync(document.Workflows["main"], null, TestContext.Current.CancellationToken);
        Assert.Equal(!fail, result.Success);
        Assert.Equal(nested ? 2 : 1, runner.Contexts.Count);
        Assert.Equal(runner.Contexts.Count, runner.Contexts.Select(c => c.InvocationId).Distinct().Count());
        foreach (var context in runner.Contexts)
        {
            Assert.Equal("tenant", context.TenantId); Assert.Equal("run", context.RunId);
            Assert.Equal("execution", context.ExecutionId); Assert.Equal("agent", context.AgentId);
            Assert.Equal("Fixture", context.AgentName); Assert.Contains("/finally/", context.InvocationId);
        }
        var saved = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        Assert.Null(await store.ReadAsync("other", "run", TestContext.Current.CancellationToken));
        var recovered = Engine(runner); recovered.RunStore = store;
        var count = runner.Contexts.Count;
        var resumed = await recovered.ResumeAsync("tenant", "run", saved.Revision, document.Workflows["main"], TestContext.Current.CancellationToken);
        Assert.Equal(result.Success, resumed.Success); Assert.Equal(count, runner.Contexts.Count);
        if (fail) Assert.Null(result.Error!.Details?["finalization_errors"]);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelCleanupOrRemoveIdentity()
    {
        var runner = new Runner(); var engine = Engine(runner);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(Yaml(false, false)));
        var result = await engine.ExecuteAsync(document.Workflows["main"], null, cancel.Token);
        Assert.Equal("CANCELLED", result.Error!.Code);
        Assert.Equal("run", Assert.Single(runner.Contexts).RunId);
        Assert.Null(result.Error.Details?["finalization_errors"]);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplacePrimaryFailure()
    {
        var runner = new Runner { Fail = true }; var engine = Engine(runner);
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(Yaml(false, true)));
        var result = await engine.ExecuteAsync(document.Workflows["main"], null, TestContext.Current.CancellationToken);
        Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
        var secondary = Assert.Single(result.Error.Details!["finalization_errors"]!.AsArray())!;
        Assert.Equal("AGENT_ISOLATION_UNAVAILABLE", secondary["code"]!.ToString());
        Assert.Equal("run", Assert.Single(runner.Contexts).RunId);
    }

    private static WorkflowEngine Engine(Runner runner)
    {
        var engine = new WorkflowEngine { Limits = new() { TenantId = "tenant", RunId = "run", ExecutionId = "execution", AgentId = "agent", AgentName = "Fixture" } };
        engine.AgentTaskRunners["renamed-runner"] = runner;
        return engine;
    }
    private static string Yaml(bool nested, bool fail)
    {
        var cleanup = """
                - id: release
                  type: agent.run
                  input:
                    runner: renamed-runner
                    objective: Release a fixture resource
                    workspace: fixture
                    capabilities: [fixture]
                    output_schema: {type: object, required: [done], properties: {done: {type: boolean}}}
                    budget: {max_elapsed_milliseconds: 10000, max_model_calls: 1, max_total_tokens: 100}
                    verification:
                      - id: observed
                        kind: fixture
                        subject: released
                        facts_schema: {type: object, required: [done], properties: {done: {const: true}}}
        """;
        var step = fail ? "{id: primary, type: assert.non_null, input: {value: null}}" : "{id: primary, type: set, input: {done: true}}";
        var main = nested ? "{id: child, type: workflow.call, input: {ref: {kind: local, name: child}}}" : step;
        return "version: 1\nworkflows:\n  main:\n    steps:\n      - " + main + "\n    finally:\n" + cleanup + "\n" +
            (nested ? "  child:\n    steps:\n      - " + step + "\n    finally:\n" + cleanup + "\n" : "");
    }
    private sealed class Runner : IAgentTaskRunner
    {
        public List<AgentTaskContext> Contexts { get; } = [];
        internal bool Fail;
        public Task<IReadOnlyList<string>> ValidateAsync(AgentTaskContext context, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<AgentTaskResult> RunAsync(AgentTaskContext context, CancellationToken ct)
        {
            Assert.False(ct.IsCancellationRequested); Contexts.Add(context);
            if (Fail) return Task.FromResult(new AgentTaskResult("failed", null, [], [], new(0, 0, 0))
            { Failure = new() { Code = "AGENT_ISOLATION_UNAVAILABLE", Message = "Mandatory isolation is unavailable." } });
            return Task.FromResult(new AgentTaskResult("completed", new JsonObject { ["done"] = true },
                [new("receipt", "fixture", "released", new() { ["done"] = true })], [], new(0, 0, 0)));
        }
        public Task<AgentTaskResult> ReconcileAsync(AgentTaskContext context, CancellationToken ct) => throw new InvalidOperationException("Completed cleanup must not redispatch");
    }
}
