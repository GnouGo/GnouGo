using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class TypedPlanningScenarioTests
{
    [Theory]
    [InlineData("https://example.test/resources/7")]
    [InlineData("https://renamed.test/items/9")]
    public async Task ExplicitValidationInputsDoNotBecomeRuntimeDefaults(string resource)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                inputs:
                  resource: {type: string, required: true}
                steps:
                  - id: parse
                    type: set
                    input: {host: "${new URL(data.inputs.resource).hostname}"}
            """);
        var withoutFixture = Assert.Single(await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken));
        Assert.Equal("inconclusive", withoutFixture.Outcome);
        var inputs = new System.Text.Json.Nodes.JsonObject { ["resource"] = resource };
        var withFixture = Assert.Single(await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken, inputs));
        Assert.Equal("passed", withFixture.Outcome);
        Assert.Null(document.Workflows["main"].Inputs!["resource"].Default);
        Assert.Equal(resource, inputs["resource"]!.GetValue<string>());
    }

    [Fact]
    public async Task FailedExecutionRetainsItsStepAndActionableCause()
    {
        var document = WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: required_value
                    type: assert.non_null
                    input: {value: null}
            """);
        var result = Assert.Single(await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken));
        var finding = Assert.Single(result.Diagnostics);
        Assert.Equal("workflow:main/step:required_value", finding.Location);
        Assert.Contains("null", finding.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("passed", result.Outcome);
    }

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
    public async Task EmptyLoopUsesDeclaredItemFixtureOnlyForExplicitPathCoverage()
    {
        var document = WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: each
                    type: loop.sequential
                    item_var: entry
                    input: {items: []}
                    steps:
                      - id: check
                        type: assert.non_null
                        input: {value: '${data.entry.name}'}
                      - id: model
                        type: llm.call
                        input: {model: fake, prompt: '${data.entry.name}'}
                finally:
                  - id: cleanup
                    type: set
                    input: {closed: true}
            """);
        var missing = await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, missing.Count(r => r.Diagnostics.Any(d => d.Code == "SCENARIO_UNREACHED")));
        var schemas = System.Text.Json.Nodes.JsonNode.Parse("""{"main:each":{"type":"object","required":["name"],"properties":{"name":{"type":"string"}}}}""")!.AsObject();
        var covered = await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken, loopItemSchemas: schemas);
        Assert.Equal(3, covered.Count); Assert.All(covered, r => Assert.Equal("passed", r.Outcome));
        Assert.Empty(document.Workflows["main"].Steps[0].Input!["items"]!.AsArray());
        Assert.Contains(covered, r => r.Id == "nominal" && r.Outcome == "passed");
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
