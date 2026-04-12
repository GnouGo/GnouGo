from __future__ import annotations

import json
from typing import Any

from openai import AsyncOpenAI

from gnougo_flow_core.models import LLMRequest, LLMResponse, LLMToolCall

from .settings import OpenAiSettings


def _normalize_openai_json_schema(schema: Any, *, strict: bool) -> Any:
    def walk(node: Any) -> Any:
        if isinstance(node, list):
            return [walk(item) for item in node]
        if not isinstance(node, dict):
            return node

        normalized = {key: walk(value) for key, value in node.items()}
        if normalized.get("type") == "object" and strict:
            # OpenAI strict json_schema requires explicit additionalProperties=false.
            normalized["additionalProperties"] = False
        return normalized

    return walk(schema)


class OpenAiLlmClient:
    def __init__(self, settings: OpenAiSettings) -> None:
        if not settings.api_key:
            raise ValueError("OpenAI API key is missing")
        self._default_model = settings.model
        self._client = AsyncOpenAI(
            api_key=settings.api_key,
            base_url=settings.base_url,
            organization=settings.organization,
            timeout=settings.timeout_seconds,
        )

    async def call_async(self, request: LLMRequest) -> LLMResponse:
        model = request.model or self._default_model
        kwargs: dict[str, Any] = {
            "model": model,
            "messages": [{"role": "user", "content": request.prompt}],
        }
        if request.temperature is not None:
            kwargs["temperature"] = request.temperature

        if request.tools:
            kwargs["tools"] = [
                {
                    "type": "function",
                    "function": {
                        "name": tool.name,
                        "description": tool.description or "",
                        "parameters": tool.input_schema or {"type": "object"},
                    },
                }
                for tool in request.tools
            ]
            kwargs["tool_choice"] = "auto"

        if request.structured_output_schema is not None:
            strict_schema = bool(request.structured_output_strict)
            schema = _normalize_openai_json_schema(request.structured_output_schema, strict=strict_schema)
            kwargs["response_format"] = {
                "type": "json_schema",
                "json_schema": {
                    "name": "structured_output",
                    "schema": schema,
                    "strict": strict_schema,
                },
            }

        resp = await self._client.chat.completions.create(**kwargs)
        choice = resp.choices[0] if resp.choices else None
        message = choice.message if choice is not None else None

        text = ""
        if message is not None and message.content:
            if isinstance(message.content, str):
                text = message.content
            else:
                parts = []
                for item in message.content:
                    chunk = getattr(item, "text", None)
                    if chunk:
                        parts.append(chunk)
                text = "\n".join(parts)

        parsed_json = None
        if request.structured_output_schema is not None and text:
            try:
                parsed_json = json.loads(text)
            except Exception:
                parsed_json = None

        tool_calls: list[LLMToolCall] = []
        if message is not None and message.tool_calls:
            for tc in message.tool_calls:
                args = tc.function.arguments if tc.function else None
                parsed_args: Any = None
                if args:
                    try:
                        parsed_args = json.loads(args)
                    except Exception:
                        parsed_args = {"raw": args}
                tool_calls.append(
                    LLMToolCall(
                        id=tc.id or "",
                        name=tc.function.name if tc.function else "",
                        arguments=parsed_args,
                    )
                )

        usage = None
        if resp.usage is not None:
            usage = {
                "prompt_tokens": resp.usage.prompt_tokens,
                "completion_tokens": resp.usage.completion_tokens,
                "total_tokens": resp.usage.total_tokens,
            }

        return LLMResponse(
            text=text,
            json_payload=parsed_json,
            usage=usage,
            raw=resp.model_dump(),
            tool_calls=tool_calls or None,
        )

