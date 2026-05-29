from __future__ import annotations

import copy

from gnougo_flow_core.runtime import *  # noqa: F401,F403


class LoopSequentialExecutor:
    step_type = "loop.sequential"
    step_description = "Sequential loop: iterates over an items/over array, or loops times/while times."
    dsl_snippet = """
### loop.sequential — Sequential loop over items or with while/times
```yaml
# Iterate over a list sequentially:
- id: process
  type: loop.sequential
  input:
    items: "${data.inputs.my_list}"    # required — array expression (alias: 'over')
  item_var: item                        # variable name for current item (default: "item")
  index_var: idx                        # variable name for current index (default: "i")
  steps:
    - id: transform
      type: set
      input: { value: "${data.item}" }

# Fixed iteration count:
- id: loop
  type: loop.sequential
  input:
    times: 5
    # or: while: "${data._loop.index < 10}"
  steps:
    - id: iter
      type: template.render
      input: { engine: mustache, template: "Iteration {{idx}}", data: { idx: "${data._loop.index}" }, mode: text }
```
Context: `data.<item_var>` (current item), `data.<index_var>` (current index), `data._loop.index`, `data._loop.item` (when iterating items).
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
        # Support both 'items' (primary) and 'over' (alias) for array iteration
        items = input_obj.get("items") or input_obj.get("over")
        while_expr = input_obj.get("while")
        max_times = int(input_obj.get("max_times", ctx.limits.max_loop_iterations))

        # Validate: items and times are mutually exclusive
        if items is not None and times is not None:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "loop.sequential: 'items'/'over' and 'times' are mutually exclusive",
            )

        if items is not None and not isinstance(items, list):
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "loop.sequential 'items'/'over' must be an array",
            )

        item_var = ctx.step.source.item_var or "item"
        index_var = ctx.step.source.index_var or "i"
        iterations: list[Any] = []

        # ── items-based iteration ──────────────────────────────────────
        if items is not None:
            if len(items) > max_times:
                raise WorkflowRuntimeException(
                    ErrorCodes.LOOP_LIMIT,
                    f"Loop items ({len(items)}) exceeds limit ({max_times})",
                )

            for i, item in enumerate(items):
                ctx.data[item_var] = copy.deepcopy(item)
                ctx.data[index_var] = i
                ctx.data["_loop"] = {"index": i, "item": copy.deepcopy(item)}
                ctx.data["loop"] = {"index": i, "item": copy.deepcopy(item)}

                # Evaluate while condition if present (combined items + while)
                if while_expr is not None:
                    cond = ctx.engine.interpolator.interpolate(str(while_expr), ctx.data)
                    if not ExpressionEvaluator.get_bool(cond):
                        break

                run = RunResult(success=True)
                await ctx.engine.execute_steps_async(
                    ctx.step.steps, ctx.data, run, ctx.limits, ctx.call_depth, ctx.call_stack, ctx.telemetry_span, ct=ctx.ct,
                )
                iterations.append(copy.deepcopy(ctx.data.get("steps", {})))

            # Clean up loop-scoped variables
            ctx.data.pop(item_var, None)
            ctx.data.pop(index_var, None)
            ctx.data.pop("_loop", None)
            ctx.data.pop("loop", None)

            return {"iterations": iterations, "count": len(iterations)}

        # ── times / while iteration ────────────────────────────────────
        i = 0
        while True:
            if times is not None and i >= int(ExpressionEvaluator.get_number(times)):
                break
            if i >= max_times:
                raise WorkflowRuntimeException(
                    ErrorCodes.LOOP_LIMIT,
                    f"Loop iteration limit reached ({max_times})",
                )

            ctx.data["_loop"] = {"index": i}
            ctx.data["loop"] = {"index": i}

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
