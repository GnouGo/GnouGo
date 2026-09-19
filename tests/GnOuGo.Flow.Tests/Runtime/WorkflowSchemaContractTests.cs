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
    [Fact]
    public void ExpressionInferenceUsesDeclaredTypesAndNeverExecutesUnknownComputations()
    {
        var args = new Dictionary<string, JsonObject> { ["values"] = JsonNode.Parse("""{"type":"array","items":{"type":"number"}}""")!.AsObject() };
        Assert.Equal("number", ExpressionContractInference.Infer("values.map(value => value * 2)", args)!["items"]!["type"]!.ToString());
        Assert.Null(ExpressionContractInference.Infer("unknown(values)", args));
    }
}
