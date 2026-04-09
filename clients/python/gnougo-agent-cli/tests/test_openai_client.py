from gnougo_agent_cli.openai_client import _normalize_openai_json_schema


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

