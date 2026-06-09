from __future__ import annotations

import uuid
from typing import Any

from gnougo_flow_core.models import (
    HUMAN_INPUT_FIELD_TYPES,
    HUMAN_INPUT_FIELD_TYPES_FOR_DSL,
    HUMAN_INPUT_MODE_CHOICE,
    HUMAN_INPUT_MODE_CONFIRM,
    HUMAN_INPUT_MODE_FORM,
    HUMAN_INPUT_MODE_TEXT,
    HUMAN_INPUT_MODES,
    HUMAN_INPUT_MODES_FOR_DSL,
    HumanInputFieldDef,
    human_input_field_type_requires_options,
)
from gnougo_flow_core.runtime import *  # noqa: F401,F403


def _human_input_dsl_snippet() -> str:
    return f"""
### human.input - Request human validation/input
Pauses the workflow and asks the user for text, a choice, confirmation, or a structured form.
Always set `input.mode` explicitly when generating workflows.

Valid modes: {", ".join(HUMAN_INPUT_MODES_FOR_DSL)}.
Valid field types: {", ".join(HUMAN_INPUT_FIELD_TYPES_FOR_DSL)}.

Common input fields:
  - prompt (string, required): question/instruction shown to the user.
  - mode (string, required for generated DSL): text, choice, form, or confirm.
  - context (any, optional): structured data shown next to the prompt.
  - timeout_ms (number, optional): milliseconds before HUMAN_INPUT_TIMEOUT. Default: 300000. Use 0 for no timeout.

Mode patterns:
```yaml
- id: ask_feedback
  type: human.input
  input:
    mode: text
    prompt: "What should be changed?"
    context: "${{json(data.steps.draft)}}"
```

```yaml
- id: review
  type: human.input
  input:
    mode: choice
    prompt: "Choose the next action."
    choices: [approve, modify, reject]
    timeout_ms: 300000
```

```yaml
- id: confirm_publish
  type: human.input
  input:
    mode: confirm
    prompt: "Publish the generated report?"
    choices: [approve, reject]
```

```yaml
- id: user_config
  type: human.input
  input:
    mode: form
    prompt: "Please configure the request."
    fields:
      - name: email
        type: email
        required: true
        description: Contact email
      - name: due_date
        type: date
        required: false
        default: "2026-06-09"
      - name: priority
        type: select
        options: [low, medium, high]
        default: medium
      - name: notes
        type: textarea
        required: false
```

Rules:
  - `choice` and `confirm` require a non-empty `choices` array of strings.
  - `form` requires a non-empty `fields` array.
  - `select`, `radio`, `multiselect`, and `checkbox` fields require non-empty `options`.
  - Field names must be unique and non-empty.
  - Use `date` for ISO date input (`YYYY-MM-DD`); it is returned as a string.

Output access patterns:
  - text/choice/confirm: `data.steps.<id>.response`
  - form: `data.steps.<id>.<field_name>` (for example `data.steps.user_config.due_date`)
  - Providers may also include `source`; use `data.steps.<id>.source` only when the provider supplies it.
"""


class HumanInputExecutor:
    step_type = "human.input"
    step_description = "Pause workflow and collect human input."
    dsl_snippet = _human_input_dsl_snippet()
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
                        type=str(node.get("type", "string")).strip(),
                        required=_read_bool(node.get("required"), True),
                        description=None if node.get("description") is None else str(node.get("description")),
                        options=["" if o is None else str(o) for o in node.get("options", [])] if isinstance(node.get("options"), list) else None,
                        default=None if node.get("default") is None else str(node.get("default")),
                    )
                )

        mode = _resolve_mode(input_obj, choices, fields)
        _validate_request(mode, choices, fields)

        run_id = ctx.limits.run_id or uuid.uuid4().hex
        request_payload: dict[str, Any] = {
            "prompt": prompt,
            "mode": mode,
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
            mode=mode,
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


def _resolve_mode(input_obj: dict[str, Any], choices: list[str] | None, fields: list[HumanInputFieldDef] | None) -> str:
    raw_mode = input_obj.get("mode")
    if isinstance(raw_mode, str) and raw_mode.strip():
        return raw_mode.strip()
    if fields:
        return HUMAN_INPUT_MODE_FORM
    if choices:
        if len(choices) == 2 and any(_is_confirm_choice(c) for c in choices) and any(_is_reject_choice(c) for c in choices):
            return HUMAN_INPUT_MODE_CONFIRM
        return HUMAN_INPUT_MODE_CHOICE
    return HUMAN_INPUT_MODE_TEXT


def _read_bool(value: Any, default: bool) -> bool:
    if value is None:
        return default
    if isinstance(value, bool):
        return value
    if isinstance(value, int | float):
        return value != 0
    if isinstance(value, str):
        normalized = value.strip().lower()
        if normalized in {"true", "1", "yes", "y"}:
            return True
        if normalized in {"false", "0", "no", "n"}:
            return False
    return default


def _validate_request(mode: str, choices: list[str] | None, fields: list[HumanInputFieldDef] | None) -> None:
    if mode.lower() not in HUMAN_INPUT_MODES:
        raise WorkflowRuntimeException(
            ErrorCodes.INPUT_VALIDATION,
            f"human.input mode '{mode}' is not supported. Known modes: {', '.join(sorted(HUMAN_INPUT_MODES))}.",
        )

    mode_lc = mode.lower()
    if mode_lc in {HUMAN_INPUT_MODE_CHOICE, HUMAN_INPUT_MODE_CONFIRM} and not choices:
        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"human.input mode '{mode}' requires a non-empty 'choices' array.")
    if mode_lc == HUMAN_INPUT_MODE_FORM and not fields:
        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "human.input mode 'form' requires a non-empty 'fields' array.")
    if mode_lc == HUMAN_INPUT_MODE_TEXT and choices:
        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "human.input mode 'text' cannot define 'choices'. Use mode 'choice' or 'confirm'.")
    if mode_lc == HUMAN_INPUT_MODE_TEXT and fields:
        raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "human.input mode 'text' cannot define 'fields'. Use mode 'form'.")

    if not fields:
        return
    names: set[str] = set()
    for field in fields:
        if not field.name.strip():
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "human.input field requires a non-empty 'name'.")
        name_key = field.name.lower()
        if name_key in names:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"human.input field '{field.name}' is defined more than once.")
        names.add(name_key)
        if field.type.lower() not in HUMAN_INPUT_FIELD_TYPES:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                f"human.input field '{field.name}' uses unsupported type '{field.type}'. Known types: {', '.join(sorted(HUMAN_INPUT_FIELD_TYPES))}.",
            )
        if human_input_field_type_requires_options(field.type) and not field.options:
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                f"human.input field '{field.name}' of type '{field.type}' requires non-empty 'options'.",
            )


def _is_confirm_choice(value: str) -> bool:
    return value.lower() in {"approve", "yes", "true", "confirm", "ok"}


def _is_reject_choice(value: str) -> bool:
    return value.lower() in {"reject", "no", "false", "cancel"}
