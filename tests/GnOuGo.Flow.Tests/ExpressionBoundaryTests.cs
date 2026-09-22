using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using Xunit;

namespace GnOuGo.Flow.Tests;

public sealed class ExpressionBoundaryTests
{
    [Theory]
    [InlineData("${({a:{b:7}}).a.b}", "7")]
    [InlineData("result ${({a:'}'}).a}", "result }")]
    [InlineData("${1} and ${2}", "1 and 2")]
    [InlineData("${1 /* } */ + 2}", "3")]
    [InlineData("${1 // }\n + 2}", "3")]
    [InlineData("${1 // can't close here }\n + 2}", "3")]
    [InlineData("${1 /* can't close here } */\n + 2}", "3")]
    [InlineData("${/}/.test('}')}", "true")]
    [InlineData("${`value ${1 + 2}`}", "value 3")]
    [InlineData("${'line\nbreak'}", "line\nbreak")]
    public void CompilationAndInterpolationUseTheSameBoundaries(string expression, string expected)
    {
        var document = new WorkflowDocument { Skill = new() { Description = "Validate expression contract", Inputs = new(), Outputs = new() }, Workflows = new() { ["main"] = new() { Steps = [new() { Id = "value", Type = "set", Input = new JsonObject { ["value"] = expression } }] } } };
        Assert.Empty(new WorkflowValidator().Validate(document));
        var result = new StringInterpolator(new ExpressionEvaluator()).Interpolate(expression, new JsonObject());
        Assert.Equal(expected, ExpressionEvaluator.GetString(result));
    }

    [Theory]
    [InlineData("${({a:1})")]
    [InlineData("text ${'unterminated}")]
    [InlineData("${1 + }")]
    public void MalformedExpressionCannotPassCompilation(string expression)
    {
        var document = new WorkflowDocument { Skill = new() { Description = "Validate expression contract", Inputs = new(), Outputs = new() }, Workflows = new() { ["main"] = new() { Steps = [new() { Id = "value", Type = "set", Input = new JsonObject { ["value"] = expression } }] } } };
        Assert.Contains(new WorkflowValidator().Validate(document), d => d.Code == ErrorCodes.ExprParse);
    }
}
