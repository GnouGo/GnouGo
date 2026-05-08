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


def test_normalize_openai_json_schema_converts_yaml_derived_null_type() -> None:
    schema = {
        "type": "object",
        "properties": {
            "issue_number": {
                "anyOf": [
                    {"type": "number"},
                    {"type": None},
                ]
            },
            "body": {
                "type": ["string", None],
                "enum": ["", None],
            },
        },
    }

    normalized = _normalize_openai_json_schema(schema, strict=False)

    assert normalized["properties"]["issue_number"]["anyOf"][1]["type"] == "null"
    assert normalized["properties"]["body"]["type"] == ["string", "null"]
    assert normalized["properties"]["body"]["enum"] == ["", None]


def test_normalize_openai_json_schema_coerces_quoted_schema_keyword_scalars() -> None:
    schema = {
        "type": "object",
        "additionalProperties": "false",
        "properties": {
            "issues": {
                "type": "array",
                "maxItems": "10",
                "minItems": "1",
                "uniqueItems": "true",
                "items": {
                    "type": "object",
                    "additionalProperties": "false",
                    "properties": {
                        "score": {
                            "type": "number",
                            "minimum": "0.5",
                            "maximum": "1",
                        }
                    },
                },
            }
        },
    }

    normalized = _normalize_openai_json_schema(schema, strict=False)
    issues = normalized["properties"]["issues"]
    score = issues["items"]["properties"]["score"]

    assert normalized["additionalProperties"] is False
    assert issues["maxItems"] == 10
    assert issues["minItems"] == 1
    assert issues["uniqueItems"] is True
    assert issues["items"]["additionalProperties"] is False
    assert score["minimum"] == 0.5
    assert score["maximum"] == 1.0


def test_normalize_openai_json_schema_strict_overrides_invalid_additional_properties() -> None:
    schema = {
        "type": "object",
        "additionalProperties": "true",
        "properties": {
            "nested": {
                "type": "object",
                "additionalProperties": True,
            }
        },
    }

    normalized = _normalize_openai_json_schema(schema, strict=True)

    assert normalized["additionalProperties"] is False
    assert normalized["properties"]["nested"]["additionalProperties"] is False


def test_normalize_openai_json_schema_does_not_mutate_original_schema() -> None:
    schema = {
        "type": "object",
        "properties": {
            "value": {
                "anyOf": [
                    {"type": "string"},
                    {"type": None},
                ]
            }
        },
    }

    _normalize_openai_json_schema(schema, strict=True)

    assert schema["properties"]["value"]["anyOf"][1]["type"] is None
    assert "additionalProperties" not in schema


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


