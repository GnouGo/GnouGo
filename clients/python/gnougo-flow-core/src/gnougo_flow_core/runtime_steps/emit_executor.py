from __future__ import annotations

from gnougo_flow_core.runtime import *  # noqa: F401,F403


class EmitExecutor:
    step_type = "emit"
    step_description = "Emit progress/thinking messages to telemetry/UI."
    dsl_snippet = """
### emit - Send a progress/thinking message
```yaml
- id: thinking
  type: emit
  input:
    message: "Planning next actions"
    level: thinking
```
Output: `{ message, level }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "The input object is malformed or the 'message' field is missing."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "emit input must be object")
        message = input_obj.get("message")
        if not isinstance(message, str):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "emit requires 'message'")
        level = str(input_obj.get("level", "thinking")).lower()
        if level not in {"thinking", "info", "progress", "response"}:
            level = "thinking"
        if ctx.telemetry_span:
            ctx.telemetry_span.add_event(
                "gnougo-flow.step.thinking",
                [("gnougo-flow.thinking.message", message), ("gnougo-flow.thinking.level", level)],
            )
        return {"message": message, "level": level}
