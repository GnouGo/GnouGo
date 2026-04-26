from __future__ import annotations

import copy

from gnougo_flow_core.runtime import *  # noqa: F401,F403


class LoopSequentialExecutor:
    step_type = "loop.sequential"
    step_description = "Iterate nested steps with times/over/while conditions."
    dsl_snippet = """
### loop.sequential - Execute loop iterations sequentially
```yaml
- id: review_loop
  type: loop.sequential
  input:
    while: "${!exists(data.steps.review) || data.steps.review.response != 'approve'}"
    max_times: 5
  steps:
    - id: review
      type: human.input
      input:
        prompt: "Approve?"
        choices: [approve, modify]

# Or iterate over a list (Python-only extension):
- id: process_each
  type: loop.sequential
  item_var: row
  index_var: i
  input:
    over: "${data.inputs.rows}"
  steps:
    - id: handle
      type: set
      input: { value: "${row}", idx: "${i}" }
```
Output: `{ iterations: [...], count: <number> }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "loop.sequential requires nested 'steps' or has incompatible inputs."),
        (ErrorCodes.LOOP_LIMIT, False, "loop.sequential exceeded max_times limit."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        if not ctx.step.steps:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "loop.sequential requires 'steps'")
        input_obj = ctx.engine.get_resolved_input(ctx) or {}
        if not isinstance(input_obj, dict):
            input_obj = {}

        times = input_obj.get("times")
        over = input_obj.get("over")
        while_expr = input_obj.get("while")
        max_times = int(input_obj.get("max_times", ctx.limits.max_loop_iterations))

        # Mode validation: at most one of times / over may be combined with while.
        modes = sum(1 for v in (times is not None, over is not None) if v)
        if modes > 1:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "loop.sequential: 'times' and 'over' are mutually exclusive",
            )

        if over is not None and not isinstance(over, list):
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "loop.sequential 'over' must be an array",
            )

        item_var = ctx.step.source.item_var
        index_var = ctx.step.source.index_var
        iterations: list[Any] = []
        i = 0
        while True:
            # Termination by `times` count
            if times is not None and i >= int(ExpressionEvaluator.get_number(times)):
                break
            # Termination by `over` length
            if over is not None and i >= len(over):
                break
            # Hard cap to avoid runaway loops
            if i >= max_times:
                raise WorkflowRuntimeException(
                    ErrorCodes.LOOP_LIMIT,
                    f"Loop iteration limit reached ({max_times})",
                )

            # Bind iteration variables
            loop_state: dict[str, Any] = {"index": i}
            if over is not None:
                item = over[i]
                loop_state["item"] = item
                if item_var:
                    ctx.data[item_var] = item
            if index_var:
                ctx.data[index_var] = i

            ctx.data["_loop"] = loop_state
            ctx.data["loop"] = dict(loop_state)

            # while: evaluated every iteration BEFORE running steps
            if while_expr is not None:
                cond = ctx.engine.interpolator.interpolate(str(while_expr), ctx.data)
                if not ExpressionEvaluator.get_bool(cond):
                    break

            run = RunResult(success=True)
            await ctx.engine.execute_steps_async(
                ctx.step.steps, ctx.data, run, ctx.limits, ctx.call_depth, ctx.call_stack, ctx.telemetry_span, ct=ctx.ct,
            )
            iterations.append(copy.deepcopy(ctx.data.get("steps", {})))
            i += 1
        return {"iterations": iterations, "count": i}
