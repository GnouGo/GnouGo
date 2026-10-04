using System.Text.Json;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class RetiredPrimitiveTests
{
    public static TheoryData<string, string> Cases => new(
        from type in new[] { "decision.evaluate", "assert.non_null", "array.project", "value.validate", "value.project" }
        from placement in new[] { "steps", "finally", "nested", "branch", "default", "child" }
        select (type, placement));

    [Theory, MemberData(nameof(Cases))]
    public async Task RejectsRetiredStepsBeforeEffectsOrRunMutation(string type, string placement)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - {id: effect, type: probe, input: {}}
                  - {id: body, type: sequence, steps: [{id: nested, type: set, input: {}}]}
                  - {id: parallel, type: parallel, branches: [{steps: [{id: branch, type: set, input: {}}]}]}
                  - {id: choice, type: switch, default: [{id: default, type: set, input: {}}]}
                finally: [{id: cleanup, type: probe, input: {}}]
              child:
                steps: [{id: value, type: set, input: {}}]
            """);
        // This also exercises preflight for an already compiled, subsequently changed document.
        var compiled = new WorkflowCompiler().Compile(document);
        var main = document.Workflows["main"];
        var target = placement switch
        {
            "steps" => main.Steps[0], "finally" => main.Finally[0], "nested" => main.Steps[1].Steps![0],
            "branch" => main.Steps[2].Branches![0].Steps[0], "default" => main.Steps[3].Default![0],
            _ => document.Workflows["child"].Steps[0]
        };
        target.Type = type;
        var before = JsonSerializer.Serialize(document, WorkflowRunJsonContext.Default.WorkflowDocument);
        var failure = Assert.Throws<WorkflowCompilationException>(() => new WorkflowCompiler().Compile(document));
        Assert.Contains(failure.Errors, e => e.Code == ErrorCodes.StepTypeRetired && e.StepId == target.Id);
        var store = new InMemoryWorkflowRunStore(); var probe = new Probe();
        var engine = new WorkflowEngine { RunStore = store, Limits = new() { TenantId = "tenant-a", RunId = "run" } };
        engine.Registry.Register(probe);
        var error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => engine.ExecuteAsync(compiled.Workflows["main"], new JsonObject(), TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.StepTypeRetired, error.Code); Assert.Equal(0, probe.Calls);
        Assert.Empty(await store.ListAsync("tenant-a", TestContext.Current.CancellationToken));
        await store.CreateAsync(new() { TenantId = "tenant-a", RunId = "run", WorkflowName = "main", WorkflowYaml = "historical approved artifact", Limits = new() { TenantId = "tenant-a", RunId = "run" } }, TestContext.Current.CancellationToken);
        var saved = JsonSerializer.Serialize(await store.ReadAsync("tenant-a", "run", TestContext.Current.CancellationToken), WorkflowRunJsonContext.Default.WorkflowRun);
        error = await Assert.ThrowsAsync<WorkflowRuntimeException>(() => engine.ResumeAsync("tenant-a", "run", 0, compiled.Workflows["main"], TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.StepTypeRetired, error.Code);
        Assert.Equal(saved, JsonSerializer.Serialize(await store.ReadAsync("tenant-a", "run", TestContext.Current.CancellationToken), WorkflowRunJsonContext.Default.WorkflowRun));
        Assert.Empty(await store.ListAsync("tenant-b", TestContext.Current.CancellationToken));
        Assert.Equal(before, JsonSerializer.Serialize(document, WorkflowRunJsonContext.Default.WorkflowDocument));
    }

    [Fact]
    public async Task CustomExecutorRegistrationStillWorks()
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps: [{id: custom, type: probe, input: {}}]
            """);
        var probe = new Probe(); var engine = new WorkflowEngine(); engine.Registry.Register(probe);
        var result = await engine.ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.Equal(1, probe.Calls);
        foreach (var type in new[] { "decision.evaluate", "assert.non_null", "array.project", "value.validate", "value.project" })
        { Assert.False(engine.Registry.Has(type)); Assert.Null(BuiltInStepContracts.Get(type)); }
    }

    private sealed class Probe : IStepExecutor
    {
        public int Calls;
        public string StepType => "probe";
        public StepRecovery Recovery => StepRecovery.ReplaySafe;
        public Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
        { Calls++; return Task.FromResult<JsonNode?>(new JsonObject()); }
    }
}
