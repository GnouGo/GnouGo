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
        raw_args = copy.deepcopy(args) if isinstance(args, dict) else dict(args or {})
        resolved_args = apply_workflow_input_defaults(sub.source, raw_args)
        input_errors = validate_input_types(sub.source, resolved_args)
        if input_errors:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                f"Input validation failed for called workflow '{resolution.workflow_name}': {'; '.join(input_errors)}",
                details={"workflow": resolution.workflow_name, "validation_errors": input_errors},
            )
        sub_data = {
            "inputs": resolved_args,
            "steps": {},
            "env": copy.deepcopy(ctx.data.get("env", {})),
        }
        rr = RunResult(success=True)
        previous_document = ctx.engine.compiled_document
        if sub.document is not None:
            ctx.engine.compiled_document = sub.document
        sub_span = ctx.engine.telemetry.workflow_start(
            {
                "workflow_name": resolution.workflow_name,
                "document_name": sub.document.source.name if sub.document and sub.document.source else None,
                "inputs": copy.deepcopy(sub_data["inputs"]),
                "source_text": sub.document.source.raw_yaml if sub.document and sub.document.source else None,
                "source_format": "yaml",
            }
        )
        started = time.perf_counter()
        try:
            try:
                await ctx.engine.execute_steps_async(
                    sub.steps,
                    sub_data,
                    rr,
                    ctx.limits,
                    ctx.call_depth + 1,
                    call_stack,
                    sub_span,
                    ct=ctx.ct,
                    is_finalization=ctx.is_finalization,
                )
            except WorkflowRuntimeException as exc:
                rr.success = False
                rr.error = exc.to_workflow_error()
            except asyncio.CancelledError:
                rr.success = False
                rr.error = WorkflowRuntimeException(
                    "CANCELLED", "Workflow execution cancelled", True
                ).to_workflow_error()
            except Exception as exc:
                rr.success = False
                rr.error = WorkflowRuntimeException(
                    "INTERNAL_ERROR", str(exc), False
                ).to_workflow_error()
            finally:
                await ctx.engine.execute_workflow_finalization_async(
                    sub,
                    sub_data,
                    rr,
                    ctx.limits,
                    ctx.call_depth + 1,
                    call_stack,
                    sub_span,
                    inherited_finalization_ct=ctx.ct if ctx.is_finalization else None,
                )
        finally:
            ctx.engine.compiled_document = previous_document
            ctx.engine.telemetry.workflow_end(
                sub_span,
                {
                    "success": rr.success,
                    "steps_executed": len(rr.step_results),
                    "duration": time.perf_counter() - started,
                    "error_code": rr.error.code if rr.error else None,
                    "error_message": rr.error.message if rr.error else None,
                },
            )

        if not rr.success:
            error = rr.error or WorkflowRuntimeException(
                "INTERNAL_ERROR", "Called workflow failed."
            ).to_workflow_error()
            details = copy.deepcopy(error.details) if isinstance(error.details, dict) else {}
            failed_step = next((item for item in reversed(rr.step_results) if item.error is not None), None)
            details.setdefault("workflow", resolution.workflow_name)
            if failed_step is not None:
                details.setdefault("step_id", failed_step.step_id)
                details.setdefault("step_type", failed_step.step_type)
                details.setdefault("step_status", failed_step.status.value)
            raise WorkflowRuntimeException(error.code, error.message, error.retryable, details)

        outputs = (
            {k: ctx.engine.evaluate_output_def(v, sub_data) for k, v in sub.outputs.items()}
            if sub.outputs
            else copy.deepcopy(sub_data.get("steps", {}))
        )
        return {"outputs": outputs, "workflow": resolution.workflow_name}
