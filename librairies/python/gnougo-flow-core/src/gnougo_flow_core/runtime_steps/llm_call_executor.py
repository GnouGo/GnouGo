from __future__ import annotations

import gnougo_flow_core.runtime as _runtime
from gnougo_flow_core.runtime import *  # noqa: F401,F403


def _matches_json_schema(value: Any, schema: Any) -> bool:
    if not isinstance(schema, dict):
        return True

    schema_type = schema.get("type")
    if isinstance(schema_type, list):
        return any(_matches_json_schema(value, {**schema, "type": item}) for item in schema_type)

    if schema_type == "object":
        if not isinstance(value, dict):
            return False
        required = schema.get("required") if isinstance(schema.get("required"), list) else []
        if any(key not in value for key in required):
            return False
        properties = schema.get("properties") if isinstance(schema.get("properties"), dict) else {}
        return all(key not in value or _matches_json_schema(value[key], prop_schema) for key, prop_schema in properties.items())
    if schema_type == "array":
        if not isinstance(value, list):
            return False
        item_schema = schema.get("items")
        return all(_matches_json_schema(item, item_schema) for item in value) if item_schema else True
    if schema_type == "string":
        return isinstance(value, str)
    if schema_type in {"number", "integer"}:
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if schema_type == "boolean":
        return isinstance(value, bool)
    if schema_type == "null":
        return value is None
    return True


class LlmCallExecutor:
    step_type = "llm.call"
    step_description = "Call an LLM with prompt/model and optional structured output."
    dsl_snippet = """
### llm.call - Call a language model
IMPORTANT: use `prompt` (NOT `messages`). `prompt` is REQUIRED. `model` is required unless the runtime injects a default model.
IMPORTANT: `temperature` and `reasoning` are optional overrides.
Omit them unless the task explicitly needs them; the runtime removes unsupported parameters based on model capabilities.
Basic call:
```yaml
- id: summarize
  type: llm.call
  input:
    model: gpt-4                        # optional when runtime defaults are configured
    prompt: "Summarize: ${data.inputs.task}"  # required - plain string
    system: "You are a helpful assistant."    # optional
    temperature: 0.7                     # optional override; omit by default
    reasoning: high                      # optional override; omit by default
    max_tokens: 2048                     # optional
```
Structured output:
IMPORTANT for `strict: true` (OpenAI/GitHub Models response_format json_schema):
- Every schema object with `properties` MUST have `required` listing EVERY key from `properties`.
- Do NOT list only the fields that feel mandatory; strict mode rejects omitted property names.
- Optional fields must still be listed in `required`; represent them as nullable with `anyOf: [{ type: <type> }, { type: "null" }]`.
- Add `additionalProperties: false` on every object schema for portability. The OpenAI provider also patches/adapts it automatically in strict mode.
```yaml
- id: classify
  type: llm.call
  input:
    model: gpt-4
    prompt: "Classify this ticket and return JSON"
    structured_output:
      schema_inline:
        type: object
        properties:
          category: { type: string }
          priority: { type: string }
          notes:
            anyOf:
              - type: string
              - type: "null"
        required: [category, priority, notes]   # every property above is listed, including nullable optional fields
        additionalProperties: false
      strict: true
```
You can also use `structured_output.schema_ref` instead of `schema_inline`.
Output: `{ text, json?, usage?, raw? }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "llm.call input/model/prompt is invalid."),
        (ErrorCodes.LLM_TIMEOUT, True, "transient timeout while calling LLM provider."),
        (ErrorCodes.LLM_NETWORK, True, "transient network failure while calling LLM provider."),
        (ErrorCodes.LLM_SCHEMA, False, "structured_output response was not valid JSON for the requested schema."),
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
        request = ctx.engine.sanitize_llm_request(request)

        ctx.set_telemetry_attribute("gen_ai.operation.name", "chat")
        ctx.set_telemetry_attribute("gen_ai.system", provider or "default")
        ctx.set_telemetry_attribute("gen_ai.request.model", model)
        if request.temperature is not None:
            ctx.set_telemetry_attribute("gen_ai.request.temperature", request.temperature)
        if request.reasoning is not None:
            ctx.set_telemetry_attribute("gen_ai.request.reasoning_effort", request.reasoning)

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
            response = await ctx.engine.call_llm_async(request)
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

        structured_json = response.json_payload
        schema = request.structured_output_schema
        if schema is not None and structured_json is None and response.text:
            try:
                structured_json = json.loads(response.text)
            except Exception:
                structured_json = None
        if schema is not None and (structured_json is None or not _matches_json_schema(structured_json, schema)):
            raise WorkflowRuntimeException(
                ErrorCodes.LLM_SCHEMA,
                "llm.call structured_output expected valid JSON but the LLM returned an incompatible response",
            )

        out = {"text": response.text, "meta": {"model": model}}
        if structured_json is not None:
            out["json"] = structured_json
        if response.usage is not None:
            out["usage"] = response.usage
        if response.raw is not None:
            out["raw"] = response.raw
        return out
