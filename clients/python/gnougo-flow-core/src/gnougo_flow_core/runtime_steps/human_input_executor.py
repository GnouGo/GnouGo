from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403

class HumanInputExecutor:
    step_type = "human.input"
    step_description = "Pause workflow and collect human input."
    dsl_snippet = """
### human.input - Request human validation/input
```yaml
- id: review
  type: human.input
  input:
    prompt: "Approve this approach?"
    context: "${data.steps.analyze.text}"
    choices: [approve, modify]
    timeout_ms: 300000
```
Output: provider-defined response object (commonly includes `response`).
"""
    documented_exceptions = [
        ("NO_HITL_PROVIDER", False, "human.input requires an input provider."),
        (ErrorCodes.INPUT_VALIDATION, False, "human.input prompt/input is malformed."),
        ("HUMAN_INPUT_TIMEOUT", False, "human input timed out."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        provider = ctx.engine.human_input_provider
        if provider is None:
            raise WorkflowRuntimeException("NO_HITL_PROVIDER", "human.input requires an IHumanInputProvider")
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "human.input input must be an object")
        prompt = input_obj.get("prompt")
        if not isinstance(prompt, str):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "human.input requires a 'prompt' field")

        timeout_ms = int(input_obj.get("timeout_ms", 300_000))
        request = HumanInputRequest(
            run_id=ctx.limits.run_id or f"run-{int(time.time() * 1000)}",
            step_id=ctx.step.id,
            prompt=prompt,
            context=input_obj.get("context"),
            choices=[str(c) for c in input_obj.get("choices", [])] if isinstance(input_obj.get("choices"), list) else None,
            fields=None,
            timeout_ms=timeout_ms,
        )

        try:
            response = await asyncio.wait_for(provider.request_input_async(request), timeout=timeout_ms / 1000)
            return response if response is not None else {"response": None}
        except asyncio.TimeoutError as exc:
            raise WorkflowRuntimeException("HUMAN_INPUT_TIMEOUT", f"human.input step '{ctx.step.id}' timed out after {timeout_ms}ms") from exc
