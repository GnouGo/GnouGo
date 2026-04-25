import asyncio
from types import SimpleNamespace

from gnougo_flow_core.models import LLMRequest

from gnougo_flow_cli.openai_client import OpenAiLlmClient, _normalize_openai_json_schema


def test_normalize_openai_json_schema_adds_additional_properties_false_in_strict_mode() -> None:
    schema = {
        "type": "object",
        "properties": {
            "answer": {"type": "string"},
            "meta": {
                "type": "object",
                "properties": {"score": {"type": "number"}},
            },
            "items": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {"name": {"type": "string"}},
                },
            },
        },
        "required": ["answer"],
    }

    normalized = _normalize_openai_json_schema(schema, strict=True)

    assert normalized["additionalProperties"] is False
    assert normalized["properties"]["meta"]["additionalProperties"] is False
    assert normalized["properties"]["items"]["items"]["additionalProperties"] is False
    assert normalized["required"] == ["answer"]


def test_normalize_openai_json_schema_keeps_non_strict_schema_unchanged() -> None:
    schema = {
        "type": "object",
        "properties": {"answer": {"type": "string"}},
        "required": ["answer"],
    }

    normalized = _normalize_openai_json_schema(schema, strict=False)

    assert "additionalProperties" not in normalized


def test_openai_client_forwards_reasoning_effort() -> None:
    captured = {}

    class FakeCompletions:
        async def create(self, **kwargs):
            captured.update(kwargs)
            message = SimpleNamespace(content="ok", tool_calls=None)
            choice = SimpleNamespace(message=message)
            return SimpleNamespace(
                choices=[choice],
                usage=None,
                model_dump=lambda: {"id": "fake"},
            )

    client = object.__new__(OpenAiLlmClient)
    client._default_model = "gpt-5.4"
    client._client = SimpleNamespace(chat=SimpleNamespace(completions=FakeCompletions()))

    request = LLMRequest(model="", prompt="hello", reasoning="max")

    response = asyncio.run(client.call_async(request))

    assert captured["reasoning_effort"] == "high"
    assert response.text == "ok"


