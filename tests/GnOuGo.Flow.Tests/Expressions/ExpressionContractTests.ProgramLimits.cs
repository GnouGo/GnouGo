using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Compilation;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;
using Xunit;

namespace GnOuGo.Flow.Tests.Expressions;

public sealed partial class ExpressionContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CheckedProgramAdmissionUsesTheRuntimeBoundary(int excess)
    {
        var program = "\"" + new string('x', ExpressionEvaluator.MaxCheckedProgramLength - 2 + excess) + "\"";
        var expression = JsonValue.Create("${" + program + "}");
        if (excess == 0)
        {
            ExpressionEvaluator.ValidateExpressionContracts(expression, Metadata());
            Assert.Equal(ExpressionEvaluator.MaxCheckedProgramLength - 2,
                new ExpressionEvaluator().EvaluateCheckedExpression(program, new JsonObject(), new())!.GetValue<string>().Length);
        }
        else
        {
            var error = Assert.Throws<WorkflowRuntimeException>(() => ExpressionEvaluator.ValidateExpressionContracts(expression, Metadata()));
            Assert.Equal(ErrorCodes.InputValidation, error.Code);
            Assert.Contains("65537 characters", error.Message);
            Assert.Contains("runtime limit is 65536", error.Message);
            Assert.Throws<WorkflowRuntimeException>(() => new ExpressionEvaluator().EvaluateCheckedExpression(program, new JsonObject(), new()));
        }
    }

    [Fact]
    public void OversizedGeneratedProgramsHaveALocatedDiagnosticBeforeExecution()
    {
        var program = "${({value:'" + new string('x', ExpressionEvaluator.MaxCheckedProgramLength) + "'})}";
        var document = new WorkflowDocument { Workflows = new() { ["main"] = new() { Steps = [new()
        {
            Id = "generated", Type = "set", Input = JsonValue.Create(program), ExpressionContracts = Metadata()
        }] } } };
        var finding = Assert.Single(new WorkflowValidator(new WorkflowEngine().Registry).Validate(document),
            e => e.Field == "expression_contracts");
        Assert.Equal("main", finding.WorkflowName);
        Assert.Equal("generated", finding.StepId);
        Assert.Equal(ErrorCodes.InputValidation, finding.Code);
        Assert.Contains("runtime limit is 65536", finding.Message);
    }
}
