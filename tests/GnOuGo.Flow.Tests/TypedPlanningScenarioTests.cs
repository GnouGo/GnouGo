using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class TypedPlanningScenarioTests
{
    [Theory]
    [InlineData("previous", "more", "previous == null || previous.response.more", true)]
    [InlineData("precedent", "suite", "precedent == null || precedent.response.suite", true)]
    [InlineData("previous", "more", "true", false)]
    [InlineData("previous", "more", "false", false)]
    public async Task ExplicitObservationSequenceTestsContinuationAndTermination(string alias, string field, string condition, bool passes)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: pages
                    type: loop.sequential
                    input:
                      while: "${(() => { const ALIAS = data._loop_previous_pages?.read; return CONDITION; })()}"
                      max_times: 4
                    steps:
                      - id: read
                        type: mcp.call
                        input: {server: renamed, method: observe, request: {}}
                finally:
                  - id: cleanup
                    type: set
                    input: {closed: true}
            """.Replace("ALIAS", alias, StringComparison.Ordinal).Replace("CONDITION", condition, StringComparison.Ordinal));
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","properties":{"response":{"type":"object","properties":{"FIELD":{"type":"boolean"}},"required":["FIELD"]}},"required":["response"]}""".Replace("FIELD", field, StringComparison.Ordinal))!;
        var observations = new System.Text.Json.Nodes.JsonObject { ["main:read"] = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = schema,
            ["responses"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["response"] = new System.Text.Json.Nodes.JsonObject { [field] = true } },
                new System.Text.Json.Nodes.JsonObject { ["response"] = new System.Text.Json.Nodes.JsonObject { [field] = false } })
        } };
        var results = await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken, observations: observations);
        var nominal = Assert.Single(results, s => s.Id == "nominal");
        Assert.Equal(passes, nominal.Outcome == "passed");
        if (passes) Assert.All(results, s => Assert.Equal("passed", s.Outcome));
        else Assert.Contains(nominal.Diagnostics, d => d.Code == "SCENARIO_OBSERVATIONS_UNCONSUMED" || d.Message.Contains("SCENARIO_OBSERVATIONS_EXHAUSTED", StringComparison.Ordinal));
        observations["main:read"]!["responses"]![0]!["response"]![field] = "invalid";
        var invalid = Assert.Single(await WorkflowPlanScenarioValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken, observations: observations), s => s.Id == "nominal");
        Assert.NotEqual("passed", invalid.Outcome);
    }

    [Fact]
    public async Task StructuredPostProcessingUsesTheSyntheticHostModelWithoutChangingTheArtifact()
    {
        var document = WorkflowParser.Parse("""
            version: 1
            entrypoint: main
            workflows:
              main:
                steps:
                  - id: read
                    type: mcp.call
                    input:
                      server: neutral
                      kind: tool
                      method: read
                      request: {}
                      structured_output:
                        schema_inline:
                          type: object
                          properties: {value: {type: string}}
                          required: [value]
                          additionalProperties: false
                        strict: true
                  - id: use
                    type: assert.non_null
                    input: {value: "${data.steps.read.json.value}"}
                finally:
                  - id: cleanup
                    type: set
                    input: {closed: true}
            """);
        var factory = new InMemoryMcpClientFactory();
        factory.RegisterServer("neutral", new() { Tools = [new() { Name = "read", InputSchema = new System.Text.Json.Nodes.JsonObject { ["type"] = "object" } }],
            ToolHandlers = new() { ["read"] = _ => new() { Content = System.Text.Json.Nodes.JsonValue.Create("Opaque original response") } } });
        var results = await WorkflowPlanScenarioValidator.ValidateAsync(document, factory, TestContext.Current.CancellationToken);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.Outcome == "passed", string.Join("; ", result.Diagnostics.Select(d => d.Message))));
        Assert.Null(document.Workflows["main"].Steps[0].Input!["structured_output"]!["model"]);
    }

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
