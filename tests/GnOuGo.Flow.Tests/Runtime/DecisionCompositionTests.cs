using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class DecisionCompositionTests
{
    [Theory]
    [InlineData("true", "false", false, "FIRST")]
    [InlineData("false", "true", false, "SECOND")]
    [InlineData("false", "false", true, "NONE")]
    [InlineData("true", "true", true, null)]
    [InlineData("true", "true", false, null)]
    [InlineData("false", "false", false, null)]
    [InlineData("\"true\"", "false", true, null)]
    [InlineData("null", "false", true, null)]
    public async Task CheckThenRouteRejectsAmbiguityAndNonBooleanConditions(string first, string second, bool allowDefault, string? expected)
    {
        var result = await Run(new() { ["first"] = JsonNode.Parse(first), ["second"] = JsonNode.Parse(second), ["allowDefault"] = allowDefault, ["later"] = "VALID" });
        Assert.Equal(expected is not null, result.Success);
        if (expected is not null) Assert.Equal(expected, result.Outputs!["selected"]!.GetValue<string>());
        else
        {
            Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
            Assert.DoesNotContain(result.StepResults, s => s.StepId == "route");
            Assert.DoesNotContain(result.StepResults, s => s.StepId == "decisions" && s.Output is not null);
        }
    }

    [Fact]
    public async Task InvalidLaterDecisionPublishesNoPartialResult()
    {
        var result = await Run(new() { ["first"] = true, ["second"] = false, ["allowDefault"] = false, ["later"] = "OUTSIDE" });
        Assert.False(result.Success);
        Assert.Null(Assert.Single(result.StepResults, s => s.StepId == "decisions").Output);
        Assert.DoesNotContain(result.StepResults, s => s.StepId == "route");
    }

    private static Task<RunResult> Run(JsonObject inputs)
    {
        var document = WorkflowParser.Parse("""
            version: 1
            workflows:
              main:
                steps:
                  - id: conditions
                    type: set
                    input: {first: '${data.inputs.first}', second: '${data.inputs.second}'}
                    output_schema:
                      type: object
                      required: [first, second]
                      properties: {first: {type: boolean}, second: {type: boolean}}
                  - id: decisions
                    type: set
                    input:
                      exclusive: '${data.steps.conditions.first !== data.steps.conditions.second || (data.inputs.allowDefault === true && !data.steps.conditions.first && !data.steps.conditions.second)}'
                      outcome: '${data.steps.conditions.first ? "FIRST" : data.steps.conditions.second ? "SECOND" : "NONE"}'
                      later: '${data.inputs.later}'
                    output_schema:
                      type: object
                      required: [exclusive, outcome, later]
                      properties:
                        exclusive: {type: boolean, enum: [true]}
                        outcome: {type: string, enum: [FIRST, SECOND, NONE]}
                        later: {type: string, enum: [VALID]}
                  - id: route
                    type: switch
                    expr: '${data.steps.decisions.outcome}'
                    cases:
                      - value: FIRST
                        steps: [{id: first, type: set, input: {selected: FIRST}}]
                      - value: SECOND
                        steps: [{id: second, type: set, input: {selected: SECOND}}]
                    default:
                      - {id: none, type: set, input: {selected: NONE}}
                outputs:
                  selected: '${data.steps.decisions.outcome}'
            """);
        return new WorkflowEngine().ExecuteAsync(new WorkflowCompiler().Compile(document).Workflows["main"], inputs, TestContext.Current.CancellationToken);
    }
}
