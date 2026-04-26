from __future__ import annotations

import copy

from gnougo_flow_core.runtime import *  # noqa: F401,F403


class ParallelExecutor:
    step_type = "parallel"
    step_description = "Execute branches in parallel."
    dsl_snippet = """
### parallel - Execute branches in parallel
```yaml
- id: run_parallel
  type: parallel
  input:
    max_concurrency: 2
  branches:
    - steps:
        - id: a
          type: set
          input: { value: "A" }
    - steps:
        - id: b
          type: set
          input: { value: "B" }
```
Output: `{ branches: [<branch_steps_0>, <branch_steps_1>, ...] }` in branch order.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "parallel step requires 'branches'."),
        (ErrorCodes.PARALLEL_LIMIT, False, "Number of parallel branches exceeds the runtime maximum."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        branches = ctx.step.branches
        if not branches:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "parallel step requires 'branches'")
        if len(branches) > ctx.limits.max_parallel_branches:
            raise WorkflowRuntimeException(
                ErrorCodes.PARALLEL_LIMIT,
                f"Parallel branches ({len(branches)}) exceeds limit ({ctx.limits.max_parallel_branches})",
            )

        input_obj = ctx.engine.get_resolved_input(ctx) or {}
        max_concurrency = int(input_obj.get("max_concurrency", 0)) if isinstance(input_obj, dict) else 0
        sem = asyncio.Semaphore(max_concurrency) if max_concurrency > 0 else None

        async def run_branch(branch_steps: list[CompiledStep], branch_index: int) -> tuple[int, dict[str, Any]]:
            if sem:
                await sem.acquire()
            try:
                # Deep-clone parent data so each branch is fully isolated and inherits
                # any loop-bound variables (`item`, `_loop`, etc.) from the parent.
                branch_data = copy.deepcopy(ctx.data)
                branch_data["steps"] = {}
                branch_result = RunResult(success=True)
                await ctx.engine.execute_steps_async(
                    branch_steps,
                    branch_data,
                    branch_result,
                    ctx.limits,
                    ctx.call_depth,
                    set(ctx.call_stack),
                    ctx.telemetry_span,
                    ct=ctx.ct,
                )
                return branch_index, branch_data
            finally:
                if sem:
                    sem.release()

        tasks = [run_branch(branch, i) for i, branch in enumerate(branches)]
        results = await asyncio.gather(*tasks)
        ordered = [item for _, item in sorted(results, key=lambda x: x[0])]
        return {"branches": [copy.deepcopy(item.get("steps", {})) for item in ordered]}
