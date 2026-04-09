from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403

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
```
Output: `{ outputs: <workflow outputs>, workflow: <name> }`.
"""
    documented_exceptions = [
        (ErrorCodes.WORKFLOW_CYCLE_DETECTED, False, "recursive cycle or max depth exceeded."),
        (ErrorCodes.WORKFLOW_FETCH_POLICY, False, "remote workflow reference violates fetch policy."),
        (ErrorCodes.WORKFLOW_FETCH_NETWORK, False, "failed to fetch remote workflow."),
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

        if ctx.call_depth >= ctx.limits.max_call_depth:
            raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_CYCLE_DETECTED, "Max call depth exceeded")

        if kind == "local":
            name = ref.get("name")
            if not isinstance(name, str):
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Local workflow.call requires 'name'")
            if name in ctx.call_stack:
                raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_CYCLE_DETECTED, f"Cycle detected: workflow '{name}' already in call stack")

            compiled_doc = ctx.engine.compiled_document
            if not compiled_doc or name not in compiled_doc.workflows:
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"Local workflow '{name}' not found")
            sub = compiled_doc.workflows[name]
            sub_data = {"inputs": dict(args), "steps": {}, "env": dict(ctx.data.get("env", {}))}
            rr = RunResult(success=True)
            await ctx.engine.execute_steps_async(sub.steps, sub_data, rr, ctx.limits, ctx.call_depth + 1, set(ctx.call_stack) | {name}, ctx.telemetry_span)
            outputs = {k: ctx.engine.evaluate_output_def(v, sub_data) for k, v in (sub.outputs or {}).items()} if sub.outputs else sub_data.get("steps", {})
            return {"outputs": outputs, "workflow": name}

        if kind == "url":
            fetcher = ctx.engine.workflow_fetcher
            if fetcher is None:
                raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_FETCH_NETWORK, "No workflow fetcher configured")
            url = ref.get("url")
            if not isinstance(url, str):
                raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Remote workflow.call requires 'url'")

            if ctx.engine.fetch_policy and ctx.engine.fetch_policy.require_https and not url.startswith("https://"):
                raise WorkflowRuntimeException(ErrorCodes.WORKFLOW_FETCH_POLICY, "HTTPS required by policy")

            yaml_text = await fetcher.fetch_async(url, ref.get("integrity"))
            doc = WorkflowParser.parse(yaml_text)
            compiled = WorkflowCompiler().compile(doc)
            wf = compiled.workflows.get(compiled.entrypoint or "") or next(iter(compiled.workflows.values()))
            sub_data = {"inputs": dict(args), "steps": {}, "env": dict(ctx.data.get("env", {}))}
            rr = RunResult(success=True)
            await ctx.engine.execute_steps_async(wf.steps, sub_data, rr, ctx.limits, ctx.call_depth + 1, set(ctx.call_stack), ctx.telemetry_span)
            return {"outputs": sub_data.get("steps", {}), "workflow": wf.name}

        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"Unknown workflow.call kind: {kind}")
