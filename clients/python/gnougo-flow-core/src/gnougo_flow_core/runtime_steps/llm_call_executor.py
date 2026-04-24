from __future__ import annotations

import gnougo_flow_core.runtime as _runtime
from gnougo_flow_core.runtime import *  # noqa: F401,F403

class LlmCallExecutor:
    step_type = "llm.call"
    step_description = "Call an LLM with prompt/model and optional structured output."
    dsl_snippet = """
### llm.call - Call a language model
```yaml
- id: analyze
  type: llm.call
  input:
    model: gpt-4o-mini
    temperature: 0.2
    prompt: "Summarize: ${data.inputs.task}"
    structured_output:
      schema_inline:
        type: object
        properties:
          summary: { type: string }
        required: [summary]
      strict: true
```
Output: `{ text, json?, usage?, raw? }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "llm.call input/model/prompt is invalid."),
        (ErrorCodes.LLM_TIMEOUT, True, "transient timeout while calling LLM provider."),
        (ErrorCodes.LLM_NETWORK, True, "transient network failure while calling LLM provider."),
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        client = ctx.engine.llm_client
        if client is None:
            raise WorkflowRuntimeException(ErrorCodes.LLM_NETWORK, "No LLM client configured")
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "llm.call input must be object")

        provider, model = ctx.engine.resolve_llm_target(input_obj.get("provider"), input_obj.get("model"))
        if not model:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "llm.call requires 'model' unless runtime default is configured")
        prompt = input_obj.get("prompt")
        if not isinstance(prompt, str):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "llm.call requires 'prompt'")

        structured = input_obj.get("structured_output") if isinstance(input_obj.get("structured_output"), dict) else None
        # Reasoning / thinking effort: "minimal"|"low"|"medium"|"high"|"max"|"auto".
        # When omitted, providers fall back to their own defaults ("auto").
        reasoning_raw = input_obj.get("reasoning")
        reasoning = reasoning_raw.strip() if isinstance(reasoning_raw, str) and reasoning_raw.strip() else None
        request = LLMRequest(
            provider=provider,
            model=model,
            prompt=prompt,
            temperature=float(input_obj["temperature"]) if "temperature" in input_obj and input_obj["temperature"] is not None else None,
            structured_output_schema=(structured.get("schema_inline") or structured.get("schema_ref")) if structured else None,
            structured_output_strict=bool(structured.get("strict")) if structured and "strict" in structured else None,
            reasoning=reasoning,
        )

        ctx.set_telemetry_attribute("gen_ai.operation.name", "chat")
        ctx.set_telemetry_attribute("gen_ai.system", provider or "default")
        ctx.set_telemetry_attribute("gen_ai.request.model", model)
        if request.temperature is not None:
            ctx.set_telemetry_attribute("gen_ai.request.temperature", request.temperature)
        if reasoning is not None:
            ctx.set_telemetry_attribute("gen_ai.request.reasoning_effort", reasoning)

        if ctx.limits.log_step_content:
            ctx.add_telemetry_event(
                "gen_ai.content.prompt",
                [("gen_ai.prompt", prompt), ("prompt.role", "user")],
            )

        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            [
                ("gnougo-flow.thinking.message", f"Calling LLM ({model})..."),
                ("gnougo-flow.thinking.level", "thinking"),
            ],
        )

        try:
            response = await client.call_async(request)
        except TimeoutError as exc:
            raise WorkflowRuntimeException(ErrorCodes.LLM_TIMEOUT, str(exc), True) from exc
        except Exception as exc:
            raise WorkflowRuntimeException(ErrorCodes.LLM_NETWORK, f"LLM call failed: {exc}") from exc

        finish_reason = "tool_calls" if response.tool_calls else "stop"
        ctx.set_telemetry_attribute("gen_ai.response.model", model)
        ctx.set_telemetry_attribute("gen_ai.response.finish_reason", finish_reason)
        _runtime._extract_usage_telemetry(ctx, response.usage, model)

        if response.text:
            preview = response.text[:120] + ("..." if len(response.text) > 120 else "")
            ctx.add_telemetry_event(
                "gnougo-flow.step.thinking",
                [
                    ("gnougo-flow.thinking.message", preview),
                    ("gnougo-flow.thinking.level", "response"),
                ],
            )

        if ctx.limits.log_step_content and (response.text or response.json_payload is not None):
            completion_payload = response.text if response.text else json.dumps(response.json_payload, ensure_ascii=False)
            ctx.add_telemetry_event(
                "gen_ai.content.completion",
                [
                    ("gen_ai.completion", completion_payload),
                    ("completion.role", "assistant"),
                    ("completion.finish_reason", finish_reason),
                ],
            )

        out = {"text": response.text, "meta": {"model": model}}
        if response.json_payload is not None:
            out["json"] = response.json_payload
        if response.usage is not None:
            out["usage"] = response.usage
        if response.raw is not None:
            out["raw"] = response.raw
        return out
