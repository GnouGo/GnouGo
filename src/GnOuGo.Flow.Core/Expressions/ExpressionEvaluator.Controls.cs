using System.Globalization;
using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Models;
using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Expressions;

public sealed partial class ExpressionEvaluator
{
    internal static void ValidateControlContracts(StepDef step)
    {
        if (step.ControlContracts is null) return;
        foreach (var (pointer, checks) in step.ControlContracts)
        {
            string? expression = pointer == "/if" ? step.If :
                pointer == "/expr" && step.Type == "switch" ? step.Expr : null;
            var parts = pointer.Split('/');
            if (step.Type == "switch" && parts is ["", "cases", var position, "when"] &&
                int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                position == index.ToString(CultureInfo.InvariantCulture) && step.Cases is { } cases && index < cases.Count &&
                cases[index].Value is null)
                expression = cases[index].When;
            if (expression is null)
                throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "Control contract must identify an unambiguous if, switch expr or case when expression: " + pointer);
            ValidateExpressionContracts(JsonValue.Create(expression), new JsonObject { [""] = checks?.DeepClone() });
        }
    }
}
