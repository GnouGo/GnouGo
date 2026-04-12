from __future__ import annotations

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
        (ErrorCodes.LOOP_LIMIT, False, "loop.parallel exceeded items limit."),
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
            raise WorkflowRuntimeException(ErrorCodes.LOOP_LIMIT, "Loop items exceed limit")

        item_var = ctx.step.source.item_var or "item"
        index_var = ctx.step.source.index_var or "i"
        max_concurrency = int(input_obj.get("max_concurrency", 0))
        sem = asyncio.Semaphore(max_concurrency) if max_concurrency > 0 else None

        async def run_iter(index: int, item: Any) -> tuple[int, dict[str, Any]]:
            if sem:
                await sem.acquire()
            try:
                iter_data = {
                    "inputs": dict(ctx.data.get("inputs", {})),
                    "steps": dict(ctx.data.get("steps", {})),
                    "env": dict(ctx.data.get("env", {})),
                    item_var: item,
                    index_var: index,
                    "_loop": {"index": index, "item": item},
                    "loop": {"index": index, "item": item},
                }
                run = RunResult(success=True)
                await ctx.engine.execute_steps_async(ctx.step.steps, iter_data, run, ctx.limits, ctx.call_depth, set(ctx.call_stack), ctx.telemetry_span)
                return index, iter_data
            finally:
                if sem:
                    sem.release()

        results = await asyncio.gather(*(run_iter(i, it) for i, it in enumerate(items)))
        ordered = [r for _, r in sorted(results, key=lambda x: x[0])]
        cleaned = []
        for item in ordered:
            steps = {k: v for k, v in item.get("steps", {}).items() if not (k.startswith("__") and k.endswith("__"))}
            cleaned.append(steps)
        return {"results": cleaned, "count": len(items)}
