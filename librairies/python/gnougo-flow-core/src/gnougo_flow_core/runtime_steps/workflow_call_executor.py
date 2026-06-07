from __future__ import annotations

import copy

from gnougo_flow_core.runtime import *  # noqa: F401,F403
from gnougo_flow_core.workflow_call_resolver import DefaultWorkflowCallResolver, WorkflowCallResolution, WorkflowCallResolutionContext


class WorkflowCallExecutor:
    step_type = "workflow.call"
    step_description = "Call a local or remote workflow by reference."
    dsl_snippet = """
### workflow.call - Execute another workflow
```yaml
- id: run_sub
  type: workflow.call
  input:
    ref:
      kind: local
      name: generated
    args:
      task: "${data.inputs.task}"

# Or remote:
- id: call_remote
  type: workflow.call
  input:
    ref:
      kind: url
      url: https://example.com/wf.yaml
      integrity: sha256-...
      export: my_entry      # optional - must be in remote `exports`
    args: { x: 1 }

# Or from the configured workspace root:
- id: call_workspace
  type: workflow.call
  input:
    ref:
      kind: workspace
      path: workflows/helper.yaml
```
Output: `{ outputs: <workflow outputs>, workflow: <name> }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "Missing/invalid 'ref' or unknown local workflow."),
        (ErrorCodes.WORKFLOW_CYCLE_DETECTED, False, "Recursive cycle or max call depth exceeded."),
        (ErrorCodes.WORKFLOW_FETCH_POLICY, False, "Remote workflow reference violates fetch policy."),
        (ErrorCodes.WORKFLOW_FETCH_NETWORK, False, "Failed to fetch remote workflow."),
        (ErrorCodes.WORKFLOW_FETCH_INTEGRITY, False, "Remote workflow integrity verification failed."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.call input must be object")

        ref = input_obj.get("ref")
        if not isinstance(ref, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.call requires 'ref'")

        kind = str(ref.get("kind", "local"))
        args = input_obj.get("args") or {}

        # Depth check FIRST, mirroring .NET ordering.
        if ctx.call_depth >= ctx.limits.max_call_depth:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_CYCLE_DETECTED,
                f"Max call depth ({ctx.limits.max_call_depth}) exceeded",
            )

        resolver = ctx.engine.workflow_call_resolver or DefaultWorkflowCallResolver()
        resolution = await resolver.resolve_async(
            WorkflowCallResolutionContext(
                engine=ctx.engine,
                ref=ref,
                kind=kind,
                call_depth=ctx.call_depth,
                call_stack=set(ctx.call_stack),
            )
        )

        if resolution.call_stack_key and resolution.call_stack_key in ctx.call_stack:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_CYCLE_DETECTED,
                f"Cycle detected: workflow '{resolution.workflow_name}' already in call stack",
            )

        return await self._execute_resolved(ctx, resolution, args)

    async def _execute_resolved(self, ctx: StepExecutionContext, resolution: WorkflowCallResolution, args: Any) -> Any:
        sub = resolution.workflow
        call_stack = set(ctx.call_stack)
        if resolution.call_stack_key:
            call_stack.add(resolution.call_stack_key)
        sub_data = {
            "inputs": copy.deepcopy(args) if isinstance(args, (dict, list)) else dict(args or {}),
            "steps": {},
            "env": copy.deepcopy(ctx.data.get("env", {})),
        }
        rr = RunResult(success=True)
        previous_document = ctx.engine.compiled_document
        if sub.document is not None:
            ctx.engine.compiled_document = sub.document
        try:
            await ctx.engine.execute_steps_async(
                sub.steps,
                sub_data,
                rr,
                ctx.limits,
                ctx.call_depth + 1,
                call_stack,
                ctx.telemetry_span,
                ct=ctx.ct,
            )
        finally:
            ctx.engine.compiled_document = previous_document
        if sub.outputs:
            outputs = {k: ctx.engine.evaluate_output_def(v, sub_data) for k, v in sub.outputs.items()}
        else:
            outputs = copy.deepcopy(sub_data.get("steps", {}))
        return {"outputs": outputs, "workflow": resolution.workflow_name}
