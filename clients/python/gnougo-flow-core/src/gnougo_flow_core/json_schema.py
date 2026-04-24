from __future__ import annotations

from typing import Any

from .models import InputDef, OutputDef


def _map_base_type(type_name: str) -> dict[str, Any]:
    type_lc = type_name.lower()
    if type_lc == "string":
        return {"type": "string"}
    if type_lc == "number":
        return {"type": "number"}
    if type_lc == "boolean":
        return {"type": "boolean"}
    if type_lc == "array":
        return {"type": "array"}
    if type_lc in {"object", "dictionary"}:
        return {"type": "object"}
    return {}


def input_def_to_schema(definition: InputDef) -> dict[str, Any]:
    schema: dict[str, Any] = _map_base_type(definition.type)

    if definition.description is not None:
        schema["description"] = definition.description
    if definition.default is not None:
        schema["default"] = str(definition.default)
    if definition.items is not None:
        schema["items"] = input_def_to_schema(definition.items)

    if definition.properties is not None:
        props: dict[str, Any] = {}
        inferred_required: list[str] = []
        for key, prop_def in definition.properties.items():
            props[key] = input_def_to_schema(prop_def)
            if prop_def.required:
                inferred_required.append(key)
        schema["properties"] = props
        if definition.required_properties:
            schema["required"] = list(definition.required_properties)
        elif inferred_required:
            schema["required"] = inferred_required

    if definition.additional_properties is not None:
        schema["additionalProperties"] = input_def_to_schema(definition.additional_properties)

    return schema


def inputs_to_json_schema(inputs: dict[str, InputDef]) -> dict[str, Any]:
    properties: dict[str, Any] = {}
    required: list[str] = []

    for name, definition in inputs.items():
        properties[name] = input_def_to_schema(definition)
        if definition.required:
            required.append(name)

    schema: dict[str, Any] = {
        "type": "object",
        "properties": properties,
    }
    if required:
        schema["required"] = required
    return schema


def output_def_to_schema(definition: OutputDef) -> dict[str, Any]:
    schema: dict[str, Any] = _map_base_type(definition.type)

    if definition.description is not None:
        schema["description"] = definition.description
    if definition.items is not None:
        schema["items"] = output_def_to_schema(definition.items)

    if definition.properties is not None:
        schema["properties"] = {
            key: output_def_to_schema(prop_def) for key, prop_def in definition.properties.items()
        }
        if definition.required_properties:
            schema["required"] = list(definition.required_properties)

    if definition.additional_properties is not None:
        schema["additionalProperties"] = output_def_to_schema(definition.additional_properties)

    return schema


def outputs_to_json_schema(outputs: dict[str, OutputDef]) -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {name: output_def_to_schema(definition) for name, definition in outputs.items()},
    }

