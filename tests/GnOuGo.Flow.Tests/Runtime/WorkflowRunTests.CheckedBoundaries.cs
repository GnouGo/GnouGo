using System.Text.Json.Nodes;
using Xunit;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed partial class WorkflowRunTests
{
    [Theory]
    [InlineData("control", false)]
    [InlineData("control", true)]
    [InlineData("result", false)]
    [InlineData("result", true)]
    public async Task CheckedControlAndChildOutputUseExistingCommitsWithoutRepeatingEffects(string boundary, bool committed)
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                - id: route
                  type: switch
                  expr: '${(() => { const allowed = true; return allowed; })()}'
                  control_contracts:
                    /expr:
                      allowed: {source: allowed, order: 0, origin: originalGuard, schema: {type: boolean}}
                  cases:
                  - value: 'true'
                    steps:
                    - id: child
                      type: workflow.call
                      input: {ref: {kind: local, name: child}, args: {}}
                finally:
                - id: cleanup
                  type: test.effect
                  input: {value: cleanup}
              child:
                steps:
                - id: write
                  type: test.effect
                  input: {value: written}
                outputs:
                  value:
                    type: string
                    expr: '${(() => { const original = data.steps.write.value; return original; })()}'
                    expression_contracts:
                      '':
                        original: {source: original, order: 0, origin: checkedExport, schema: {type: string}}
            """;
        var store = new InMemoryWorkflowRunStore(); var effects = new Effect();
        var fault = new FaultStore(store, run => boundary == "control"
            ? run.Invocations.Values.Any(i => i.StepType == "switch" && i.Control.ContainsKey("branch"))
            : run.Invocations.Values.Any(i => i.StepType == "workflow.call" && i.Status == "completed"), committed);
        await Assert.ThrowsAnyAsync<Exception>(() => Engine(fault, effects).ExecuteAsync(Compile(yaml), null, TestContext.Current.CancellationToken));
        Assert.Equal(boundary == "control" ? Array.Empty<string>() : ["written"], effects.Values);
        var saved = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        Assert.False(saved.FinalizationStarted);
        var result = await Engine(store, effects).ResumeAsync("tenant", "run", saved.Revision, Compile(yaml), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(["written", "cleanup"], effects.Values);
        var complete = (await store.ReadAsync("tenant", "run", TestContext.Current.CancellationToken))!;
        var child = Assert.Single(complete.Invocations.Values, i => i.StepType == "workflow.call");
        Assert.Equal("written", child.Output!["outputs"]!["value"]!.GetValue<string>());
        Assert.DoesNotContain(complete.Invocations.Values, i => i.StepType == "set");
    }
}
