from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403

class SwitchExecutor:
    step_type = "switch"
    step_description = "Branch using expr/value or case conditions."
    dsl_snippet = """
### switch - Branch execution by value or condition
```yaml
- id: route
  type: switch
  expr: "${data.inputs.mode}"
  cases:
    - value: quick
      steps:
        - id: quick_path
          type: set
          input: { selected: quick }
    - when: "${data.inputs.priority > 5}"
      steps:
        - id: priority_path
          type: set
          input: { selected: priority }
  default:
    - id: fallback
      type: set
      input: { selected: default }
```
Output: merged `data.steps` after selected branch execution.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "switch requires 'cases' and valid case definitions."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        if not ctx.step.cases:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "switch step requires 'cases'")
        if len(ctx.step.cases) > ctx.limits.max_switch_cases:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Switch cases exceed limit")

        expr_value = None
        if ctx.step.source.expr is not None:
            expr_value = ctx.engine.interpolator.interpolate(ctx.step.source.expr, ctx.data)

        for case in ctx.step.cases:
            matched = False
            if expr_value is not None and case.source.value is not None:
                matched = str(case.source.value) == ExpressionEvaluator.get_string(expr_value)
            elif case.source.when is not None:
                matched = ExpressionEvaluator.get_bool(ctx.engine.interpolator.interpolate(case.source.when, ctx.data))

            if matched:
                run = RunResult(success=True)
                await ctx.engine.execute_steps_async(case.steps, ctx.data, run, ctx.limits, ctx.call_depth, ctx.call_stack, ctx.telemetry_span)
                return dict(ctx.data.get("steps", {}))

        if ctx.step.default:
            run = RunResult(success=True)
            await ctx.engine.execute_steps_async(ctx.step.default, ctx.data, run, ctx.limits, ctx.call_depth, ctx.call_stack, ctx.telemetry_span)
            return dict(ctx.data.get("steps", {}))
        return None
