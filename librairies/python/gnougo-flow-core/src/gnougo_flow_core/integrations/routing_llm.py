from __future__ import annotations

import inspect
from typing import Any

from ..models import LLMRequest, LLMResponse, LLMToolCall


class RoutingLLMClientAdapter:
    """Adapter from a routing LLM client to the Flow `ILLMClient` protocol."""

    def __init__(self, inner: Any) -> None:
        self._inner = inner

    async def call_async(self, request: LLMRequest) -> LLMResponse:
        inner_request = {
            "provider": request.provider,
            "model": request.model,
            "prompt": request.prompt,
            "temperature": request.temperature,
            "structured_output_schema": request.structured_output_schema,
            "structured_output_strict": request.structured_output_strict,
            "reasoning": request.reasoning,
        }
        if request.tools:
            inner_request["tools"] = [
                {
                    "name": tool.name,
                    "description": tool.description,
                    "input_schema": tool.input_schema,
                }
                for tool in request.tools
            ]

        call = getattr(self._inner, "call_async", None) or getattr(self._inner, "call", None)
        if call is None:
            raise TypeError("RoutingLLMClientAdapter inner client must expose call_async() or call()")

        try:
            value = call(inner_request)
        except TypeError:
            value = call(request)
        if inspect.isawaitable(value):
            value = await value

        if isinstance(value, LLMResponse):
            return value

        tool_calls_raw = _get(value, "tool_calls", None)
        tool_calls = None
        if tool_calls_raw:
            tool_calls = [
                tc if isinstance(tc, LLMToolCall) else LLMToolCall(
                    id=str(_get(tc, "id", "")),
                    name=str(_get(tc, "name", "")),
                    arguments=_get(tc, "arguments", None),
                )
                for tc in tool_calls_raw
            ]

        return LLMResponse(
            text=str(_get(value, "text", "") or ""),
            json_payload=_get(value, "json_payload", _get(value, "json", None)),
            usage=_get(value, "usage", None),
            raw=_get(value, "raw", None),
            tool_calls=tool_calls,
        )


def _get(value: Any, key: str, default: Any = None) -> Any:
    if isinstance(value, dict):
        return value.get(key, default)
    return getattr(value, key, default)

