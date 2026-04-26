from __future__ import annotations

import uuid

from gnougo_flow_core.models import HumanInputFieldDef
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
            raise WorkflowRuntimeException("NO_HITL_PROVIDER", "human.input step requires an IHumanInputProvider configured on the engine.")
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "human.input input must be an object")
        prompt = input_obj.get("prompt")
        if not isinstance(prompt, str):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "human.input requires a 'prompt' field")

        timeout_ms = int(input_obj.get("timeout_ms", 300_000))
        choices = ["" if c is None else str(c) for c in input_obj.get("choices", [])] if isinstance(input_obj.get("choices"), list) else None
        fields = None
        if isinstance(input_obj.get("fields"), list):
            fields = []
            for node in input_obj["fields"]:
                if not isinstance(node, dict):
                    continue
                fields.append(
                    HumanInputFieldDef(
                        name=str(node.get("name", "")),
                        type=str(node.get("type", "string")),
                        required=bool(node.get("required", True)),
                        description=node.get("description"),
                        options=["" if o is None else str(o) for o in node.get("options", [])] if isinstance(node.get("options"), list) else None,
                        default=None if node.get("default") is None else str(node.get("default")),
                    )
                )

        run_id = ctx.limits.run_id or uuid.uuid4().hex
        request_payload: dict[str, Any] = {
            "prompt": prompt,
            "run_id": run_id,
            "step_id": ctx.step.id,
            "timeout_ms": timeout_ms,
        }
        if input_obj.get("context") is not None:
            request_payload["context"] = input_obj.get("context")
        if choices is not None:
            request_payload["choices"] = choices
        if fields is not None:
            request_payload["fields"] = [f.model_dump() for f in fields]

        ctx.add_telemetry_event(
            "gnougo-flow.step.waiting_for_human",
            [
                ("gnougo-flow.human.prompt", prompt),
                ("gnougo-flow.human.request", json.dumps(request_payload, ensure_ascii=False)),
            ],
        )

        request = HumanInputRequest(
            run_id=run_id,
            step_id=ctx.step.id,
            prompt=prompt,
            context=input_obj.get("context"),
            choices=choices,
            fields=fields,
            timeout_ms=timeout_ms,
        )

        try:
            coro = provider.request_input_async(request)
            response = await (asyncio.wait_for(coro, timeout=timeout_ms / 1000) if timeout_ms > 0 else coro)
            ctx.add_telemetry_event(
                "gnougo-flow.step.thinking",
                [
                    ("gnougo-flow.thinking.message", "Human input received."),
                    ("gnougo-flow.thinking.level", "info"),
                ],
            )
            return response if response is not None else {"response": None}
        except asyncio.TimeoutError as exc:
            raise WorkflowRuntimeException(
                "HUMAN_INPUT_TIMEOUT",
                (
                    f"human.input step '{ctx.step.id}' timed out after "
                    f"{timeout_ms}ms waiting for user response."
                ),
            ) from exc
