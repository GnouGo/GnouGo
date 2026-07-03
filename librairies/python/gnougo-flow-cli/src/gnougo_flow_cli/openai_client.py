from __future__ import annotations

import asyncio
import json
import time
from typing import Any

from gnougo_flow_core.models import LLMRequest, LLMResponse, LLMToolCall
from gnougo_flow_core.reasoning import normalize_openai_reasoning
from openai import AsyncOpenAI

from .settings import OpenAiSettings

_INTEGER_SCHEMA_KEYWORDS = {
    "maxItems",
    "minItems",
    "maxLength",
    "minLength",
    "maxProperties",
    "minProperties",
}

_NUMBER_SCHEMA_KEYWORDS = {
    "multipleOf",
    "minimum",
    "maximum",
    "exclusiveMinimum",
    "exclusiveMaximum",
}

_BOOLEAN_SCHEMA_KEYWORDS = {
    "additionalProperties",
    "uniqueItems",
}

_BACKGROUND_UNSUPPORTED_TTL_SECONDS = 65 * 60


def _parse_bool_text(value: str) -> bool | None:
    lowered = value.strip().lower()
    if lowered == "true":
        return True
    if lowered == "false":
        return False
    return None


def _normalize_schema_keyword_value(key: str, value: Any) -> Any:
    if key == "type":
        if value is None:
            return "null"
        if isinstance(value, list):
            return ["null" if item is None else item for item in value]
        return value

    if isinstance(value, str):
        text = value.strip()
        if key in _BOOLEAN_SCHEMA_KEYWORDS:
            parsed = _parse_bool_text(text)
            if parsed is not None:
                return parsed
        if key in _INTEGER_SCHEMA_KEYWORDS:
            try:
                return int(text)
            except ValueError:
                return value
        if key in _NUMBER_SCHEMA_KEYWORDS:
            try:
                return float(text)
            except ValueError:
                return value

    return value


def _normalize_openai_json_schema(schema: Any, *, strict: bool) -> Any:
    def walk(node: Any) -> Any:
        if isinstance(node, list):
            return [walk(item) for item in node]
        if not isinstance(node, dict):
            return node

        normalized = {
            key: _normalize_schema_keyword_value(key, walk(value))
            for key, value in node.items()
        }
        if normalized.get("type") == "object" and strict:
            # OpenAI strict json_schema requires explicit additionalProperties=false.
            normalized["additionalProperties"] = False
        return normalized

    return walk(schema)


class BackgroundModeAvailabilityCache:
    def __init__(self, *, clock=time.monotonic) -> None:
        self._clock = clock
        self._unsupported_until: dict[str, float] = {}

    def is_unsupported(self, key: str) -> bool:
        expires_at = self._unsupported_until.get(key)
        if expires_at is None:
            return False
        if expires_at <= self._clock():
            self._unsupported_until.pop(key, None)
            return False
        return True

    def mark_unsupported(
        self,
        key: str,
        ttl_seconds: int = _BACKGROUND_UNSUPPORTED_TTL_SECONDS,
    ) -> None:
        self._unsupported_until[key] = self._clock() + ttl_seconds


class OpenAiLlmClient:
    def __init__(
        self,
        settings: OpenAiSettings,
        *,
        background_mode_cache: BackgroundModeAvailabilityCache | None = None,
    ) -> None:
        if not settings.api_key:
            raise ValueError("OpenAI API key is missing")
        self._default_model = settings.model
        self._background_cache = background_mode_cache or BackgroundModeAvailabilityCache()
        self._background_cache_key = f"openai|{settings.base_url or ''}|responses"
        self._client = AsyncOpenAI(
            api_key=settings.api_key,
            base_url=settings.base_url,
            organization=settings.organization,
            timeout=settings.timeout_seconds,
        )

    async def call_async(self, request: LLMRequest) -> LLMResponse:
        model = request.model or self._default_model
        if request.use_background_mode:
            if not self._background_cache.is_unsupported(self._background_cache_key):
                try:
                    return await self._call_responses_background_async(request, model)
                except Exception as exc:
                    if not _is_not_found_error(exc):
                        raise
                    self._background_cache.mark_unsupported(self._background_cache_key)
            return await self._call_chat_completions_async(request, model)

        return await self._call_chat_completions_async(request, model)

    async def _call_chat_completions_async(self, request: LLMRequest, model: str) -> LLMResponse:
        kwargs: dict[str, Any] = {
            "model": model,
            "messages": [{"role": "user", "content": request.prompt}],
        }
        if request.temperature is not None:
            kwargs["temperature"] = request.temperature
        reasoning = normalize_openai_reasoning(request.reasoning)
        if reasoning is not None:
            kwargs["reasoning_effort"] = reasoning

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
            schema = _normalize_openai_json_schema(
                request.structured_output_schema,
                strict=strict_schema,
            )
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

    async def _call_responses_background_async(
        self,
        request: LLMRequest,
        model: str,
    ) -> LLMResponse:
        responses = getattr(self._client, "responses", None)
        if responses is None:
            self._background_cache.mark_unsupported(self._background_cache_key)
            return await self._call_chat_completions_async(request, model)

        kwargs: dict[str, Any] = {
            "model": model,
            "input": request.prompt,
            "background": True,
        }
        if request.temperature is not None:
            kwargs["temperature"] = request.temperature
        reasoning = normalize_openai_reasoning(request.reasoning)
        if reasoning is not None:
            kwargs["reasoning"] = {"effort": reasoning}

        response = await responses.create(**kwargs)
        delay_seconds = 1.0
        while True:
            status = getattr(response, "status", None)
            if status is None or str(status).lower() == "completed":
                return _responses_api_to_llm_response(response, request)
            if str(status).lower() in {"failed", "cancelled", "canceled", "incomplete"}:
                raise RuntimeError(f"OpenAI background response ended with status {status!r}")
            response_id = getattr(response, "id", None)
            if not response_id:
                raise RuntimeError("OpenAI background response did not include an id")
            await asyncio.sleep(delay_seconds)
            delay_seconds = min(delay_seconds * 1.5, 10.0)
            response = await responses.retrieve(response_id)


def _is_not_found_error(exc: Exception) -> bool:
    status_code = getattr(exc, "status_code", None)
    if status_code == 404:
        return True
    response = getattr(exc, "response", None)
    if getattr(response, "status_code", None) == 404:
        return True
    return "404" in str(exc) and "not found" in str(exc).lower()


def _responses_api_to_llm_response(resp: Any, request: LLMRequest) -> LLMResponse:
    raw = resp.model_dump() if hasattr(resp, "model_dump") else {}
    text = getattr(resp, "output_text", None) or _extract_responses_output_text(raw)
    parsed_json = None
    if request.structured_output_schema is not None and text:
        try:
            parsed_json = json.loads(text)
        except Exception:
            parsed_json = None

    usage = None
    raw_usage = raw.get("usage") if isinstance(raw, dict) else None
    if isinstance(raw_usage, dict):
        usage = {
            "prompt_tokens": raw_usage.get("input_tokens", raw_usage.get("prompt_tokens")),
            "completion_tokens": raw_usage.get("output_tokens", raw_usage.get("completion_tokens")),
            "total_tokens": raw_usage.get("total_tokens"),
        }

    return LLMResponse(text=text or "", json_payload=parsed_json, usage=usage, raw=raw or None)


def _extract_responses_output_text(raw: Any) -> str:
    if not isinstance(raw, dict):
        return ""
    parts: list[str] = []
    for output in raw.get("output") or []:
        if not isinstance(output, dict):
            continue
        for content in output.get("content") or []:
            if isinstance(content, dict) and isinstance(content.get("text"), str):
                parts.append(content["text"])
    return "\n".join(parts)
