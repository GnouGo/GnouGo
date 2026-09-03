using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Parsing;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Runtime;

public sealed class DecisionEvaluateExecutorTests
{
    [Fact]
    public async Task SelectsExactlyOneMatchingCase()
    {
        var result = await ExecuteAsync("""
            decisions:
              outcome:
                allowed_values: [ACCEPT, REJECT, NONE]
                cases:
                  - when: "${data.inputs.accept}"
                    value: ACCEPT
                  - when: "${data.inputs.reject}"
                    value: REJECT
                default: NONE
            """, new JsonObject { ["accept"] = true, ["reject"] = false });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("ACCEPT", result.StepResults[0].Output!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task SelectsAllowedNoEffectDefault()
    {
        var result = await ExecuteAsync("""
            decisions:
              outcome:
                allowed_values: [WRITE, NO_EFFECT]
                cases:
                  - when: false
                    value: WRITE
                default: NO_EFFECT
            """);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("NO_EFFECT", result.StepResults[0].Output!["outcome"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("true", "true", ErrorCodes.DecisionEvaluationUnresolved)]
    [InlineData("false", "false", ErrorCodes.DecisionEvaluationUnresolved)]
    public async Task UnresolvedSelectionFailsClosed(string first, string second, string expectedCode)
    {
        var input = $$"""
            decisions:
              outcome:
                allowed_values: [FIRST, SECOND, NONE]
                cases:
                  - when: {{first}}
                    value: FIRST
                  - when: {{second}}
                    value: SECOND
            """;
        if (first != "false")
            input += "\n    default: NONE";
        var result = await ExecuteAsync(input);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.Null(result.StepResults[0].Output);
    }

    [Theory]
    [InlineData("allowed_values: [A, A]\ncases: [{ when: true, value: A }]", "unique strings")]
    [InlineData("allowed_values: [A]\ncases: [{ when: yes, value: A }]", "boolean")]
    [InlineData("allowed_values: [A]\ncases: [{ when: true, value: B }]", "allowed_values")]
    [InlineData("allowed_values: [A]\ncases: [{ when: true, value: A }, { when: false, value: A }]", "case values must be unique")]
    [InlineData("allowed_values: [A]\ncases: [{ when: true, value: A }]\nextra: value", "unknown field")]
    public async Task MalformedContractsUseInputValidation(string contract, string expectedMessage)
    {
        var result = await ExecuteAsync("decisions:\n  outcome:\n" + Indent(contract, 4));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains(expectedMessage, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedDecisionSetUsesSwitchCaseLimit()
    {
        var decisions = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 3).Select(index => $$"""
              decision_{{index}}:
                allowed_values: [VALUE]
                cases: [{ when: true, value: VALUE }]
            """));
        var result = await ExecuteAsync(
            "decisions:\n" + decisions,
            limits: new ExecutionLimits { MaxSwitchCases = 2 });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("Decision count (3) exceeds limit (2)", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedCaseSetUsesSwitchCaseLimit()
    {
        var result = await ExecuteAsync("""
            decisions:
              outcome:
                allowed_values: [A, B, C]
                cases:
                  - { when: true, value: A }
                  - { when: false, value: B }
                  - { when: false, value: C }
            """, limits: new ExecutionLimits { MaxSwitchCases = 2 });

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.InputValidation, result.Error!.Code);
        Assert.Contains("case count (3) exceeds limit (2)", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureOfLaterDecisionDoesNotExposePartialOutput()
    {
        var result = await ExecuteAsync("""
            decisions:
              resolved:
                allowed_values: [VALUE]
                cases: [{ when: true, value: VALUE }]
              unresolved:
                allowed_values: [VALUE]
                cases: [{ when: false, value: VALUE }]
            """);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.DecisionEvaluationUnresolved, result.Error!.Code);
        Assert.Null(result.StepResults[0].Output);
    }

    private static async Task<RunResult> ExecuteAsync(
        string inputYaml,
        JsonObject? inputs = null,
        ExecutionLimits? limits = null)
    {
        var yaml = $$"""
            version: 1
            workflows:
              main:
                steps:
                  - id: evaluate
                    type: decision.evaluate
                    input:
            {{Indent(inputYaml, 10)}}
            """;
        var document = WorkflowParser.Parse(yaml);
        var workflow = new WorkflowCompiler().Compile(document).Workflows["main"];
        var engine = new WorkflowEngine();
        if (limits is not null)
            engine.Limits = limits;
        return await engine.ExecuteAsync(workflow, inputs ?? new JsonObject(), CancellationToken.None);
    }

    private static string Indent(string value, int spaces)
    {
        var prefix = new string(' ', spaces);
        return string.Join(Environment.NewLine, value.Split('\n').Select(line => prefix + line.TrimEnd('\r')));
    }
}
