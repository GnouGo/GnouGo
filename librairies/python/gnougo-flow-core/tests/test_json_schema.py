from gnougo_flow_core.json_schema import (
    input_def_to_schema,
    inputs_to_json_schema,
    output_def_to_schema,
    outputs_to_json_schema,
)
from gnougo_flow_core.models import InputDef, OutputDef


def test_inputs_to_json_schema_matches_dotnet_shape() -> None:
    schema = inputs_to_json_schema(
        {
            "name": InputDef(type="string", required=True, description="User name"),
            "config": InputDef(
                type="object",
                required=False,
                properties={
                    "host": InputDef(type="string", required=True),
                    "port": InputDef(type="number", required=False),
                },
                required_properties=["host"],
            ),
            "labels": InputDef(type="array", items=InputDef(type="string")),
        }
    )

    assert schema == {
        "type": "object",
        "properties": {
            "name": {"type": "string", "description": "User name"},
            "config": {
                "type": "object",
                "properties": {
                    "host": {"type": "string"},
                    "port": {"type": "number"},
                },
                "required": ["host"],
            },
            "labels": {
                "type": "array",
                "items": {"type": "string"},
            },
        },
        "required": ["name", "labels"],
    }


def test_input_def_to_schema_uses_default_and_additional_properties() -> None:
    schema = input_def_to_schema(
        InputDef(
            type="dictionary",
            default=42,
            additional_properties=InputDef(type="boolean"),
        )
    )

    assert schema == {
        "type": "object",
        "default": "42",
        "additionalProperties": {"type": "boolean"},
    }


def test_outputs_to_json_schema_matches_dotnet_shape() -> None:
    schema = outputs_to_json_schema(
        {
            "summary": OutputDef(expr="${data.steps.x.text}", type="string"),
            "report": OutputDef(
                type="object",
                properties={
                    "score": OutputDef(type="number"),
                    "tags": OutputDef(type="array", items=OutputDef(type="string")),
                },
                required_properties=["score"],
            ),
        }
    )

    assert schema == {
        "type": "object",
        "properties": {
            "summary": {"type": "string"},
            "report": {
                "type": "object",
                "properties": {
                    "score": {"type": "number"},
                    "tags": {"type": "array", "items": {"type": "string"}},
                },
                "required": ["score"],
            },
        },
    }


def test_output_def_to_schema_supports_additional_properties() -> None:
    schema = output_def_to_schema(
        OutputDef(type="dictionary", additional_properties=OutputDef(type="string"))
    )

    assert schema == {
        "type": "object",
        "additionalProperties": {"type": "string"},
    }

