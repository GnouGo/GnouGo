from __future__ import annotations

from typing import Any

from gnougo_flow_core.runtime import *  # noqa: F401,F403


def _parse_bool_string(value: str) -> bool | None:
    normalized = value.strip().lower()
    if normalized == "true":
        return True
    if normalized == "false":
        return False
    return None


def _switch_values_equal(expected: Any, actual: Any) -> bool:
    if expected is None:
        return actual is None

    if isinstance(expected, bool):
        if isinstance(actual, bool):
            return expected == actual
        if isinstance(actual, str):
            parsed = _parse_bool_string(actual)
            return parsed is not None and expected == parsed
        return False

    if isinstance(expected, (int, float)) and not isinstance(expected, bool):
        if isinstance(actual, (int, float)) and not isinstance(actual, bool):
            return float(expected) == float(actual)
        if isinstance(actual, str):
            try:
                return float(expected) == float(actual.strip())
            except ValueError:
                return False
        return False

    return str(expected) == ExpressionEvaluator.get_string(actual)

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
            has_case_value = "value" in case.source.model_fields_set
            if has_case_value and ctx.step.source.expr is not None:
                matched = _switch_values_equal(case.source.value, expr_value)
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
