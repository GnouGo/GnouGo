from gnougo_flow_core.models import OutputDef
from gnougo_flow_core.workflow_plan_contract_normalizer import (
    build_canonical_schema_yaml,
    build_skill_output_from_workflow_output_yaml,
    build_workflow_output_from_schema,
    collect_weak_output_schema_diagnostics,
    is_weak_yaml_output_schema,
)


def test_contract_normalizer_builds_workflow_output_from_json_schema_union() -> None:
    output = build_workflow_output_from_schema(
        {
            "anyOf": [
                {"type": "null"},
                {
                    "type": "object",
                    "properties": {
                        "title": {"type": "string"},
                        "count": {"type": "integer"},
                    },
                    "required": ["title"],
                },
            ]
        },
        "${data.steps.extract.json}",
    )

    assert output == {
        "expr": "${data.steps.extract.json}",
        "type": "object",
        "properties": {
            "title": {"type": "string"},
            "count": {"type": "integer"},
        },
        "required_properties": ["title"],
    }


def test_contract_normalizer_rejects_weak_public_contracts() -> None:
    assert is_weak_yaml_output_schema({"type": "any"})
    assert is_weak_yaml_output_schema({"type": "object"})
    assert is_weak_yaml_output_schema({"type": "array"})
    assert is_weak_yaml_output_schema({"type": "dictionary"})
    assert build_workflow_output_from_schema({"type": "array"}, "${data.steps.x}") is None


def test_contract_normalizer_collects_nested_weak_schema_diagnostics() -> None:
    diagnostics: list[dict] = []
    collect_weak_output_schema_diagnostics(
        OutputDef(
            expr="${data.steps.x}",
            type="array",
            items=OutputDef(type="object"),
        ),
        "workflows.main.outputs.items",
        diagnostics,
        allow_skill_scalar_type_shorthand=False,
    )

    assert diagnostics
    assert diagnostics[0]["code"] == "WEAK_OUTPUT_SCHEMA"
    assert diagnostics[0]["location"] == "workflows.main.outputs.items"
    assert "Array output schema" in diagnostics[0]["message"]


def test_contract_normalizer_builds_skill_output_without_expression() -> None:
    workflow_output = {
        "expr": "${data.steps.records.rows}",
        "type": "array",
        "items": {"type": "string"},
    }

    assert build_skill_output_from_workflow_output_yaml(workflow_output) == {
        "type": "array",
        "items": {"type": "string"},
    }


def test_contract_normalizer_canonicalizes_dictionary_schema() -> None:
    assert build_canonical_schema_yaml(
        {"type": "object", "additionalProperties": {"type": "number"}}
    ) == {
        "type": "object",
        "additionalProperties": {"type": "number"},
    }
