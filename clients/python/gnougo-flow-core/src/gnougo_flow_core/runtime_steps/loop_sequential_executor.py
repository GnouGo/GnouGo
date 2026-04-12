from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403

class LoopSequentialExecutor:
    step_type = "loop.sequential"
    step_description = "Iterate nested steps with times/while conditions."
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
```
Output: `{ iterations: [...], count: <number> }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "loop.sequential requires nested 'steps'."),
        (ErrorCodes.LOOP_LIMIT, False, "loop.sequential exceeded max_times limit."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        if not ctx.step.steps:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "loop.sequential requires 'steps'")
        input_obj = ctx.engine.get_resolved_input(ctx) or {}
        if not isinstance(input_obj, dict):
            input_obj = {}

        times = input_obj.get("times")
        max_times = int(input_obj.get("max_times", ctx.limits.max_loop_iterations))
        iterations: list[Any] = []
        i = 0
        while True:
            if times is not None and i >= int(ExpressionEvaluator.get_number(times)):
                break
            if i >= max_times:
                raise WorkflowRuntimeException(ErrorCodes.LOOP_LIMIT, f"Loop iteration limit reached ({max_times})")

            ctx.data["_loop"] = {"index": i}
            ctx.data["loop"] = {"index": i}

            while_expr = input_obj.get("while")
            if while_expr is not None:
                cond = ctx.engine.interpolator.interpolate(str(while_expr), ctx.data)
                if not ExpressionEvaluator.get_bool(cond):
                    break

            run = RunResult(success=True)
            await ctx.engine.execute_steps_async(ctx.step.steps, ctx.data, run, ctx.limits, ctx.call_depth, ctx.call_stack, ctx.telemetry_span)
            iterations.append(dict(ctx.data.get("steps", {})))
            i += 1
        return {"iterations": iterations, "count": i}
