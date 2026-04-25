from __future__ import annotations

import copy
from urllib.parse import urlparse

from gnougo_flow_core.runtime import *  # noqa: F401,F403


def _enforce_fetch_policy(url: str, integrity: str | None, policy) -> None:
    """Mirror .NET FetchPolicy validation (scheme, host, integrity)."""
    if policy is None:
        return
    parsed = urlparse(url)
    if policy.require_https and (parsed.scheme or "").lower() != "https":
        raise WorkflowRuntimeException(
            ErrorCodes.WORKFLOW_FETCH_POLICY,
            f"HTTPS required by fetch policy (got '{parsed.scheme or 'about'}://')",
        )
    allow = list(getattr(policy, "allowed_hostnames", None) or [])
    if allow:
        host = (parsed.hostname or "").lower()
        if host not in {h.lower() for h in allow}:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_FETCH_POLICY,
                f"Host '{host}' not in allow-list",
            )
    if getattr(policy, "require_integrity", False) and not integrity:
        raise WorkflowRuntimeException(
            ErrorCodes.WORKFLOW_FETCH_POLICY,
            "Integrity hash required by fetch policy but missing",
        )


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

        if kind == "local":
            return await self._call_local(ctx, ref, args)
        if kind == "url":
            return await self._call_remote(ctx, ref, args)

        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"Unknown workflow.call kind: {kind}")

    async def _call_local(self, ctx: StepExecutionContext, ref: dict, args: Any) -> Any:
        name = ref.get("name")
        if not isinstance(name, str):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "Local workflow.call requires 'name'")
        if name in ctx.call_stack:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_CYCLE_DETECTED,
                f"Cycle detected: workflow '{name}' already in call stack",
            )

        compiled_doc = ctx.engine.compiled_document
        if not compiled_doc or name not in compiled_doc.workflows:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                f"Local workflow '{name}' not found",
            )
        sub = compiled_doc.workflows[name]
        sub_data = {
            "inputs": copy.deepcopy(args) if isinstance(args, (dict, list)) else dict(args or {}),
            "steps": {},
            "env": copy.deepcopy(ctx.data.get("env", {})),
        }
        rr = RunResult(success=True)
        await ctx.engine.execute_steps_async(
            sub.steps,
            sub_data,
            rr,
            ctx.limits,
            ctx.call_depth + 1,
            set(ctx.call_stack) | {name},
            ctx.telemetry_span,
            ct=ctx.ct,
        )
        if sub.outputs:
            outputs = {k: ctx.engine.evaluate_output_def(v, sub_data) for k, v in sub.outputs.items()}
        else:
            outputs = copy.deepcopy(sub_data.get("steps", {}))
        return {"outputs": outputs, "workflow": name}

    async def _call_remote(self, ctx: StepExecutionContext, ref: dict, args: Any) -> Any:
        fetcher = ctx.engine.workflow_fetcher
        if fetcher is None:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_FETCH_NETWORK, "No workflow fetcher configured"
            )
        url = ref.get("url")
        if not isinstance(url, str):
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION, "Remote workflow.call requires 'url'"
            )
        integrity = ref.get("integrity")
        export = ref.get("export")

        policy = ctx.engine.fetch_policy
        _enforce_fetch_policy(url, integrity, policy)

        try:
            yaml_text = await fetcher.fetch_async(url, integrity)
        except WorkflowRuntimeException:
            raise
        except Exception as exc:
            raise WorkflowRuntimeException(
                ErrorCodes.WORKFLOW_FETCH_NETWORK,
                f"Failed to fetch remote workflow: {exc}",
            ) from exc

        if policy is not None and getattr(policy, "max_size_bytes", 0):
            size = len(yaml_text.encode("utf-8")) if isinstance(yaml_text, str) else len(yaml_text or b"")
            if size > policy.max_size_bytes:
                raise WorkflowRuntimeException(
                    ErrorCodes.WORKFLOW_FETCH_POLICY,
                    f"Remote workflow ({size} bytes) exceeds max_size_bytes ({policy.max_size_bytes})",
                )

        doc = WorkflowParser.parse(yaml_text)
        compiled = WorkflowCompiler().compile(doc)

        # Target selection: explicit export → entrypoint → single workflow → first.
        wf = None
        if isinstance(export, str) and export:
            exports = list(doc.exports or [])
            if exports and export not in exports:
                raise WorkflowRuntimeException(
                    ErrorCodes.WORKFLOW_FETCH_POLICY,
                    f"Requested export '{export}' is not in remote document exports",
                )
            wf = compiled.workflows.get(export)
            if wf is None:
                raise WorkflowRuntimeException(
                    ErrorCodes.WORKFLOW_FETCH_POLICY,
                    f"Requested export '{export}' is not defined in the remote document",
                )
        else:
            if compiled.entrypoint and compiled.entrypoint in compiled.workflows:
                wf = compiled.workflows[compiled.entrypoint]
            elif len(compiled.workflows) == 1:
                wf = next(iter(compiled.workflows.values()))
            else:
                wf = next(iter(compiled.workflows.values()))

        sub_data = {
            "inputs": copy.deepcopy(args) if isinstance(args, (dict, list)) else dict(args or {}),
            "steps": {},
            "env": copy.deepcopy(ctx.data.get("env", {})),
        }
        rr = RunResult(success=True)
        await ctx.engine.execute_steps_async(
            wf.steps,
            sub_data,
            rr,
            ctx.limits,
            ctx.call_depth + 1,
            set(ctx.call_stack),
            ctx.telemetry_span,
            ct=ctx.ct,
        )
        if wf.outputs:
            outputs = {k: ctx.engine.evaluate_output_def(v, sub_data) for k, v in wf.outputs.items()}
        else:
            outputs = copy.deepcopy(sub_data.get("steps", {}))
        return {"outputs": outputs, "workflow": wf.name}
