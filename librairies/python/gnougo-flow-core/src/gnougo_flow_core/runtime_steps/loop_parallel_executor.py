from __future__ import annotations

import copy

from gnougo_flow_core.runtime import *  # noqa: F401,F403


class LoopParallelExecutor:
    step_type = "loop.parallel"
    step_description = "Iterate items in parallel with optional max_concurrency."
    dsl_snippet = """
### loop.parallel - Execute iterations in parallel over items
```yaml
- id: fanout
  type: loop.parallel
  item_var: item
  index_var: i
  input:
    items: "${data.inputs.urls}"
    max_concurrency: 4
  steps:
    - id: process
      type: set
      input:
        index: "${i}"
        value: "${item}"
```
Output: `{ results: [<iteration_steps>...], count: <number> }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "loop.parallel requires object input and 'items' array."),
        (ErrorCodes.LOOP_LIMIT, False, "Number of items exceeds max_loop_iterations."),
        (ErrorCodes.PARALLEL_LIMIT, False, "Number of parallel iterations exceeds max_parallel_branches."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        if not ctx.step.steps:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "loop.parallel requires 'steps'")
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "loop.parallel input must be object")

        items = input_obj.get("items")
        if not isinstance(items, list):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "loop.parallel requires 'items' array")
        if len(items) > ctx.limits.max_loop_iterations:
            raise WorkflowRuntimeException(
                ErrorCodes.LOOP_LIMIT,
                f"Loop items ({len(items)}) exceeds max_loop_iterations ({ctx.limits.max_loop_iterations})",
            )
        if len(items) > ctx.limits.max_parallel_branches:
            raise WorkflowRuntimeException(
                ErrorCodes.PARALLEL_LIMIT,
                f"Loop items ({len(items)}) exceeds max_parallel_branches ({ctx.limits.max_parallel_branches})",
            )

        item_var = ctx.step.source.item_var or "item"
        index_var = ctx.step.source.index_var or "i"
        max_concurrency = int(input_obj.get("max_concurrency", 0))
        sem = asyncio.Semaphore(max_concurrency) if max_concurrency > 0 else None

        async def run_iter(index: int, item: Any) -> tuple[int, dict[str, Any]]:
            if sem:
                await sem.acquire()
            try:
                # Deep-clone parent data so each iteration is fully isolated yet
                # inherits everything (parent loop vars, env, etc.).
                iter_data = copy.deepcopy(ctx.data)
                iter_data["steps"] = {}
                iter_data[item_var] = item
                iter_data[index_var] = index
                iter_data["_loop"] = {"index": index, "item": item}
                iter_data["loop"] = {"index": index, "item": item}
                run = RunResult(success=True)
                await ctx.engine.execute_steps_async(
                    ctx.step.steps,
                    iter_data,
                    run,
                    ctx.limits,
                    ctx.call_depth,
                    set(ctx.call_stack),
                    ctx.telemetry_span,
                    ct=ctx.ct,
                )
                return index, iter_data
            finally:
                if sem:
                    sem.release()

        results = await asyncio.gather(*(run_iter(i, it) for i, it in enumerate(items)))
        ordered = [r for _, r in sorted(results, key=lambda x: x[0])]
        cleaned = []
        for item in ordered:
            steps = {
                k: copy.deepcopy(v)
                for k, v in item.get("steps", {}).items()
                if not (k.startswith("__") and k.endswith("__"))
            }
            cleaned.append(steps)
        return {"results": cleaned, "count": len(items)}
