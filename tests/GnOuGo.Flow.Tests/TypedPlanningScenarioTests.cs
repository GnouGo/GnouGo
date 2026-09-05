using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class TypedPlanningScenarioTests
{
    [Fact]
    public async Task IntegrationFailureAndCancellation_ExecuteCleanup()
    {
        var document = WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: model
                    type: llm.call
                    input: {model: fake, prompt: Return a greeting}
                finally:
                  - id: cleanup
                    type: set
                    input: {closed: true}
            """);
        var results = await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.Equal("passed", result.Outcome));
    }

    [Fact]
    public async Task NestedBranchesAndGuardedSteps_HaveExplicitPassingCoverage()
    {
        var document = WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: outer
                    type: switch
                    cases:
                      - when: '${false}'
                        steps:
                          - id: inner
                            type: switch
                            cases:
                              - when: '${false}'
                                steps:
                                  - id: value
                                    type: set
                                    if: '${false}'
                                    input: {answer: yes}
                            default: []
                    default: []
                finally:
                  - id: cleanup
                    type: set
                    input: {closed: true}
            """);
        var results = await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken);
        Assert.Equal(7, results.Count);
        Assert.All(results, result => Assert.Equal("passed", result.Outcome));
    }

    [Fact]
    public async Task FailedCleanup_IsNeverReportedAsPassed()
    {
        var document = WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: value
                    type: set
                    input: {ok: true}
                finally:
                  - id: cleanup
                    type: assert.non_null
                    input: {value: null}
            """);
        var result = Assert.Single(await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken));
        Assert.Equal("failed", result.Outcome);
        Assert.Contains(result.Diagnostics, d => d.Code == "FINALIZATION_NOT_EXECUTED");
    }
}
