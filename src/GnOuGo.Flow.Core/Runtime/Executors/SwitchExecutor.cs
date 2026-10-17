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

        var checkOrder = 0;
        void Observe(string pointer, string name, string origin, string? error)
        {
            static string Escape(string part) => part.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
            ctx.TelemetrySpan?.AddEvent("gnougo-flow.control.check", new KeyValuePair<string, object?>[]
            {
                new("gnougo-flow.check.name", name), new("gnougo-flow.check.origin", origin),
                new("gnougo-flow.check.reference", ctx.Step.Source.ControlContracts?[pointer]?[name]?["source"]?.GetValue<string>() ?? name),
                new("gnougo-flow.check.control_pointer", pointer),
                new("gnougo-flow.check.contract_pointer", "/control_contracts/" + Escape(pointer) + "/" + Escape(name) + "/schema"),
                new("gnougo-flow.check.order", checkOrder++), new("gnougo-flow.check.outcome", error is null ? "passed" : "failed"),
                new("error.type", error)
            });
        }
        if (ctx.HasRecordedControl("branch") && ctx.Step.Source.ControlContracts is not null)
            ctx.TelemetrySpan?.AddEvent("gnougo-flow.control.reused", new KeyValuePair<string, object?>[] { new("gnougo-flow.invocation.id", ctx.InvocationId) });
        var selected = await ctx.RecordControlAsync("branch", () =>
        {
            var expression = ctx.Step.Source.Expr is null ? null : ctx.Interpolator.ResolveControl(ctx.Step.Source.Expr, "/expr", ctx.Data, ctx.Step.Source.ControlContracts, Observe);
            for (var index = 0; index < cases.Count; index++)
            {
                var item = cases[index];
                var match = expression is not null && item.Source.Value is not null
                    ? item.Source.Value == (expression is JsonValue scalar && scalar.TryGetValue(out string? text) ? text : expression.ToJsonString())
                    : item.Source.When is not null && ExpressionEvaluator.GetBool(ctx.Interpolator.ResolveControl(item.Source.When, "/cases/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/when", ctx.Data, ctx.Step.Source.ControlContracts, Observe));
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
