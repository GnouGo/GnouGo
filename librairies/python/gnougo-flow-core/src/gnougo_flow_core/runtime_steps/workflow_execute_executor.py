from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403


class WorkflowExecuteExecutor:
    step_type = "workflow.execute"
    step_description = "Execute a YAML workflow produced by workflow.plan."
    dsl_snippet = """
### workflow.execute - Execute generated YAML from workflow.plan
```yaml
- id: run_generated
  type: workflow.execute
  input:
    from_step: generate
    args:
      task: "${data.inputs.task}"
```
Output: `{ outputs, workflow, run: { steps_executed, success } }`.
"""
    documented_exceptions = [
        (
            ErrorCodes.INPUT_VALIDATION,
            False,
            "workflow.execute requires from_step with valid planned YAML and executable inputs.",
        ),
        (ErrorCodes.WORKFLOW_CYCLE_DETECTED, False, "max call depth exceeded during planned workflow execution."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.execute input must be object")
        from_step = input_obj.get("from_step")
        if not isinstance(from_step, str):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.execute requires 'from_step'")

        plan_result = ctx.data.get("steps", {}).get(from_step)
        if not isinstance(plan_result, dict) or "yaml" not in plan_result:
            if isinstance(plan_result, dict):
                raise WorkflowRuntimeException(
                    ErrorCodes.INPUT_VALIDATION,
                    f"No YAML found in plan result of step '{from_step}'",
                )
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"No plan result found in step '{from_step}'")

        yaml_text = plan_result["yaml"]
        args = input_obj.get("args") or {}
        if not isinstance(args, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.execute 'args' must be object")

        try:
            doc = WorkflowParser.parse(str(yaml_text))
            compiled = WorkflowCompiler().compile(doc)
        except WorkflowRuntimeException:
            raise
        except Exception as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                f"Invalid planned workflow YAML from step '{from_step}': {exc}",
            ) from exc
        entrypoint = compiled.entrypoint or next(iter(compiled.workflows), None)
        if not entrypoint:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "No workflow found in planned document")
        workflow = compiled.workflows[entrypoint]

        if ctx.call_depth >= ctx.limits.max_call_depth:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_CYCLE_DETECTED,
                f"Max call depth ({ctx.limits.max_call_depth}) exceeded",
            )

        merged_args = apply_workflow_input_defaults(workflow.source, args)
        type_errors = validate_input_types(workflow.source, merged_args)
        if type_errors:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"Input validation failed: {'; '.join(type_errors)}")

        sub_data = {"inputs": dict(merged_args), "steps": {}, "env": dict(ctx.data.get("env", {}))}
        rr = RunResult(success=True)
        sub_span = ctx.engine.telemetry.workflow_start(
            {
                "workflow_name": workflow.name,
                "document_name": compiled.source.name if compiled.source else None,
                "inputs": dict(merged_args),
                "source_text": str(yaml_text),
                "source_format": "yaml",
            }
        )
        started = time.perf_counter()
        sub_error: Exception | None = None
        try:
            await ctx.engine.execute_steps_async(
                workflow.steps,
                sub_data,
                rr,
                ctx.limits,
                ctx.call_depth + 1,
                ctx.call_stack,
                parent_span=sub_span,
                ct=ctx.ct,
            )
        except Exception as exc:
            sub_error = exc
            rr.success = False
            raise
        finally:
            ctx.engine.telemetry.workflow_end(
                sub_span,
                {
                    "success": rr.success and sub_error is None,
                    "steps_executed": len(rr.step_results),
                    "duration": time.perf_counter() - started,
                    "error_code": sub_error.code if isinstance(sub_error, WorkflowRuntimeException) else ("INTERNAL_ERROR" if sub_error else None),
                    "error_message": str(sub_error) if sub_error else None,
                },
            )

        outputs = (
            {k: ctx.engine.evaluate_output_def(v, sub_data) for k, v in (workflow.outputs or {}).items()}
            if workflow.outputs
            else sub_data.get("steps", {})
        )
        return {
            "outputs": outputs,
            "workflow": workflow.name,
            "run": {"steps_executed": len(rr.step_results), "success": rr.success},
        }
