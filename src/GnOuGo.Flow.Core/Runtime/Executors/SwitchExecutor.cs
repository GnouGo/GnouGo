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
    public StepRecovery Recovery => StepRecovery.Composite;
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

        var selected = await ctx.RecordControlAsync("branch", () =>
        {
            var expression = ctx.Step.Source.Expr is null ? null : ctx.Interpolator.Interpolate(ctx.Step.Source.Expr, ctx.Data);
            for (var index = 0; index < cases.Count; index++)
            {
                var item = cases[index];
                var match = expression is not null && item.Source.Value is not null
                    ? item.Source.Value == (expression is JsonValue scalar && scalar.TryGetValue(out string? text) ? text : expression.ToJsonString())
                    : item.Source.When is not null && ExpressionEvaluator.GetBool(ctx.Interpolator.Interpolate(item.Source.When, ctx.Data));
                if (match) return JsonValue.Create(index);
            }
            return JsonValue.Create(-1);
        }, ct);
        var branch = selected!.GetValue<int>();
        if (branch >= 0)
        {
            var result = new RunResult { Success = true };
            await ctx.Engine.ExecuteStepsAsync(cases[branch].Steps, ctx.Data, result, ctx.Limits, ctx.CallDepth, ctx.CallStack,
                ctx.EffectiveExecutionScope.Child("case", branch.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct, ctx.TelemetrySpan);
            return ctx.Data["steps"]?.DeepClone();
        }

        // Default branch
        if (ctx.Step.Default != null && ctx.Step.Default.Count > 0)
        {
            var result = new RunResult { Success = true };
            await ctx.Engine.ExecuteStepsAsync(
                ctx.Step.Default,
                ctx.Data,
                result,
                ctx.Limits,
                ctx.CallDepth,
                ctx.CallStack,
                ctx.EffectiveExecutionScope.Child("case", "default"),
                ct,
                ctx.TelemetrySpan);
            return ctx.Data["steps"]?.DeepClone();
        }

        return null;
    }
}
