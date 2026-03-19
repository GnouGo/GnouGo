using System.Text.Json.Nodes;
using GnOuGo.Flow.Core.Expressions;
using GnOuGo.Flow.Core.Models;

namespace GnOuGo.Flow.Core.Runtime.Executors;

/// <summary>
/// Switch step: evaluates cases and executes the first match.
/// Form A: expr + cases with value matches
/// Form B: cases with when conditions
/// </summary>
public sealed class SwitchExecutor : IStepExecutor
{
    public string StepType => "switch";

    public IReadOnlyList<StepExceptionDoc>? DocumentedExceptions => new StepExceptionDoc[]
    {
        new(ErrorCodes.InputValidation, false, "The step is missing `cases` or exceeds the runtime maximum number of switch cases.")
    };

    public string DslSnippet => """
        ### switch — Conditional branching
        Form A (value match):
        ```yaml
        - id: route
          type: switch
          expr: "${data.inputs.mode}"
          cases:
            - value: "fast"
              steps:
                - id: fast_step
                  type: template.render
                  input: { engine: mustache, template: "Fast mode", mode: text }
            - value: "slow"
              steps: [...]
          default:
            - id: default_step
              type: template.render
              input: { engine: mustache, template: "Default", mode: text }
        ```
        Form B (when expressions):
        ```yaml
        - id: route
          type: switch
          cases:
            - when: "${data.inputs.score > 80}"
              steps: [...]
            - when: "${data.inputs.score > 50}"
              steps: [...]
          default: [...]
        ```
        Output: output of the matched branch's steps.
        """;

    public async Task<JsonNode?> ExecuteAsync(StepExecutionContext ctx, CancellationToken ct)
    {
        var cases = ctx.Step.Cases;
        if (cases == null || cases.Count == 0)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation, "switch step requires 'cases'");

        if (cases.Count > ctx.Limits.MaxSwitchCases)
            throw new WorkflowRuntimeException(ErrorCodes.InputValidation,
                $"Switch cases ({cases.Count}) exceeds limit ({ctx.Limits.MaxSwitchCases})");

        // Form A: expr matching
        JsonNode? exprValue = null;
        if (ctx.Step.Source.Expr != null)
        {
            exprValue = ctx.Engine.Interpolator.Interpolate(ctx.Step.Source.Expr, ctx.Data);
        }

        foreach (var c in cases)
        {
            bool matched = false;

            if (exprValue != null && c.Source.Value != null)
            {
                // Form A: value match
                var caseVal = c.Source.Value;
                var exprStr = exprValue is JsonValue jv && jv.TryGetValue(out string? s) ? s : exprValue?.ToJsonString();
                matched = caseVal == exprStr;
            }
            else if (c.Source.When != null)
            {
                // Form B: boolean condition
                var condResult = ctx.Engine.Interpolator.Interpolate(c.Source.When, ctx.Data);
                matched = ExpressionEvaluator.GetBool(condResult);
            }

            if (matched)
            {
                var result = new RunResult { Success = true };
                await ctx.Engine.ExecuteStepsAsync(c.Steps, ctx.Data, result, ctx.Limits, ctx.CallDepth, ctx.CallStack, ct);
                return ctx.Data["steps"]?.DeepClone();
            }
        }

        // Default branch
        if (ctx.Step.Default != null && ctx.Step.Default.Count > 0)
        {
            var result = new RunResult { Success = true };
            await ctx.Engine.ExecuteStepsAsync(ctx.Step.Default, ctx.Data, result, ctx.Limits, ctx.CallDepth, ctx.CallStack, ct);
            return ctx.Data["steps"]?.DeepClone();
        }

        return null;
    }
}
