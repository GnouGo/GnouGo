using System.Text.Json.Nodes;
using Xunit;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
namespace GnOuGo.Flow.Tests.Runtime;

public sealed class WorkflowSchemaContractTests
{
    [Fact]
    public async Task SuccessfulFinalizationExposesOnlyProvablyAvailableResults()
    {
        const string yaml = """
            version: 1
            workflows:
              main:
                steps:
                  - id: acquire
                    type: set
                    input: { resource: acquired }
                finally:
                  - id: release
                    type: set
                    if: '${data.steps["acquire"] != null}'
                    input: { released: true }
                  - id: inspect_cleanup
                    type: set
                    input: { checked: true }
                  - id: verify
                    type: set
                    if: '${data.steps["release"] != null}'
                    input: { released: '${data.steps.release.released}' }
                outputs:
                  released: '${data.steps.verify.released}'
            """;
        var document = WorkflowParser.Parse(yaml);
        WorkflowPlanSemanticValidator.Validate(document);
        var result = await new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Error?.Message); Assert.True(result.Outputs!["released"]!.GetValue<bool>());
        var scenarios = await SimulatedWorkflowValidator.ValidateAsync(document, null, TestContext.Current.CancellationToken);
        Assert.All(scenarios, scenario => Assert.Equal("passed", scenario.Outcome));
        Assert.Contains(scenarios, scenario => scenario.Id == "guard:unavailable:main:release:acquire");
        Assert.Contains(scenarios, scenario => scenario.Id == "guard:unavailable:main:verify:release");
        foreach (var guard in new[] { "false", "data.steps[\"acquire\"] != null && false", "data.steps[\"missing\"] != null", "data.steps[\"acquire\"] != null || true" })
            Assert.Throws<WorkflowSemanticValidationException>(() => WorkflowPlanSemanticValidator.Validate(WorkflowParser.Parse(yaml.Replace("data.steps[\"acquire\"] != null", guard, StringComparison.Ordinal))));
        var conditional = WorkflowParser.Parse(yaml); conditional.Workflows["main"].Steps[0].If = "${false}";
        Assert.Throws<WorkflowSemanticValidationException>(() => WorkflowPlanSemanticValidator.Validate(conditional));
    }
    private const string Yaml = """
        version: 1
        workflows:
          main:
            inputs:
              amount:
                type: number
                default: 5
                schema: { type: number, minimum: 3, maximum: 10 }
            steps:
              - id: copy
                type: set
                input: { value: '${data.inputs.amount}' }
            outputs:
              result:
                type: number
                expr: '${data.steps.copy.value}'
                schema: { type: number, minimum: 3, maximum: 7 }
        """;
    [Fact]
    public async Task RuntimeEnforcesFullInputAndOutputContracts()
    {
        var document = new WorkflowCompiler().Compile(WorkflowParser.Parse(Yaml)); var main = document.Workflows[document.Entrypoint!]; var engine = new WorkflowEngine();
        var valid = await engine.ExecuteAsync(main, new JsonObject(), TestContext.Current.CancellationToken);
        Assert.True(valid.Success, valid.Error?.Message); Assert.Equal("5", valid.Outputs!["result"]!.ToString());
        await Assert.ThrowsAsync<WorkflowRuntimeException>(() => engine.ExecuteAsync(main, new JsonObject { ["amount"] = 2 }, TestContext.Current.CancellationToken));
        var invalid = await engine.ExecuteAsync(main, new JsonObject { ["amount"] = 8 }, TestContext.Current.CancellationToken);
        Assert.False(invalid.Success); Assert.Equal("INPUT_VALIDATION", invalid.Error!.Code);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedAuthoritativeOutputSchemaRetainsItsConstraints(bool nullable)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: value
                    type: set
                    input: { amount: 8 }
                outputs:
                  result:
                    type: object
                    expr: '${data.steps.value}'
                    properties:
                      amount:
                        type: number
                        schema: { type: number, maximum: 7 }
            """);
        document.Workflows["main"].Outputs!["result"].Nullable = nullable;
        var exported = JsonSchemaConverter.OutputDefToSchema(document.Workflows["main"].Outputs!["result"]);
        var body = nullable ? exported["anyOf"]![0]! : exported;
        Assert.Equal("7", body["properties"]!["amount"]!["maximum"]!.ToString());
        var compiled = new WorkflowCompiler().Compile(document);
        var result = await new WorkflowEngine().ExecuteAsync(compiled.Workflows[compiled.Entrypoint!], new JsonObject(), TestContext.Current.CancellationToken);
        Assert.False(result.Success); Assert.Equal("INPUT_VALIDATION", result.Error!.Code);
    }
    [Fact]
    public void PublicContractExportRetainsConstraintsAndDefaultsStaySeparate()
    {
        var document = WorkflowParser.Parse(Yaml); var main = document.Workflows["main"];
        Assert.Equal("10", JsonSchemaConverter.InputsToJsonSchema(main.Inputs!)["properties"]!["amount"]!["maximum"]!.ToString());
        Assert.Equal("7", JsonSchemaConverter.OutputsToJsonSchema(main.Outputs!)["properties"]!["result"]!["maximum"]!.ToString());
        Assert.Contains(new WorkflowValidator().Validate(WorkflowParser.Parse(Yaml.Replace("maximum: 10", "maximum: invalid", StringComparison.Ordinal))), d => d.Code == "INVALID_INPUT_SCHEMA");
    }
}
