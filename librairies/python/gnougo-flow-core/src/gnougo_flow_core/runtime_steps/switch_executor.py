from __future__ import annotations

import json
from typing import Any

from gnougo_flow_core.runtime import *  # noqa: F401,F403


def _expr_to_string(value: Any) -> str:
    """Mirror .NET: JsonValue.s if string, else ToJsonString()."""
    if isinstance(value, str):
        return value
    if value is True:
        return "true"
    if value is False:
        return "false"
    if value is None:
        return "null"
    try:
        return json.dumps(value, ensure_ascii=False, default=str)
    except Exception:
        return str(value)


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
        (
            ErrorCodes.INPUT_VALIDATION,
            False,
            "The step is missing `cases` or exceeds the runtime maximum number of switch cases.",
        ),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        if not ctx.step.cases:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "switch step requires 'cases'")
        if len(ctx.step.cases) > ctx.limits.max_switch_cases:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                f"Switch cases ({len(ctx.step.cases)}) exceeds limit ({ctx.limits.max_switch_cases})",
            )

        # Form A: evaluate expr once
        expr_value: Any = None
        if ctx.step.source.expr is not None:
            expr_value = ctx.engine.interpolator.interpolate(ctx.step.source.expr, ctx.data)

        for case in ctx.step.cases:
            matched = False

            # Form A: value match — both expr and case value must be non-null (mirrors .NET)
            if expr_value is not None and case.source.value is not None:
                expr_str = _expr_to_string(expr_value)
                matched = case.source.value == expr_str
            # Form B: when condition (also acts as Form A fallthrough when value is null)
            elif case.source.when is not None:
                cond = ctx.engine.interpolator.interpolate(case.source.when, ctx.data)
                matched = ExpressionEvaluator.get_bool(cond)

            if matched:
                run = RunResult(success=True)
                await ctx.engine.execute_steps_async(
                    case.steps, ctx.data, run, ctx.limits, ctx.call_depth, ctx.call_stack, ctx.telemetry_span, ct=ctx.ct,
                )
                return dict(ctx.data.get("steps", {}))

        if ctx.step.default:
            run = RunResult(success=True)
            await ctx.engine.execute_steps_async(
                ctx.step.default, ctx.data, run, ctx.limits, ctx.call_depth, ctx.call_stack, ctx.telemetry_span, ct=ctx.ct,
            )
            return dict(ctx.data.get("steps", {}))
        return None
