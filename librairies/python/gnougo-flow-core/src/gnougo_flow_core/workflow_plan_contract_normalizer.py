from __future__ import annotations

import copy
from typing import Any

from .models import OutputDef

WEAK_OUTPUT_SCHEMA_CODE = "WEAK_OUTPUT_SCHEMA"


def build_workflow_output_from_schema(schema: Any, expr: str) -> dict[str, Any] | None:
    output = build_canonical_schema_yaml(schema)
    if not isinstance(output, dict) or is_weak_yaml_output_schema(output):
        return None
    result = {"expr": expr}
    for key, value in output.items():
        if key != "expr":
            result[key] = copy.deepcopy(value)
    return result


def build_workflow_output_from_descriptor(descriptor: Any, expr: str) -> dict[str, Any] | None:
    return build_workflow_output_from_schema(descriptor, expr)


def build_skill_output_from_workflow_output_yaml(workflow_output_yaml: Any) -> dict[str, Any] | None:
    if not isinstance(workflow_output_yaml, dict):
        return None
    clone = copy.deepcopy(workflow_output_yaml)
    clone.pop("expr", None)
    return clone if "type" in clone and not is_weak_yaml_output_schema(clone) else None


def build_canonical_schema_yaml(schema: Any) -> Any:
    normalized = _normalize_for_workflow_contract(_schema_to_contract(copy.deepcopy(schema)))
    return _canonicalize_workflow_contract_node(normalized)


def is_weak_yaml_output_schema(node: Any) -> bool:
    return _is_weak_schema(_normalize_for_workflow_contract(_schema_to_contract(copy.deepcopy(node))))


def is_weak_output_def(output: OutputDef, allow_skill_scalar_type_shorthand: bool = False) -> bool:
    return _is_weak_schema(_output_def_to_schema(output, allow_skill_scalar_type_shorthand))


def collect_weak_output_schema_diagnostics(
    output: OutputDef,
    path: str,
    diagnostics: list[dict[str, Any]],
    allow_skill_scalar_type_shorthand: bool,
) -> None:
    _collect_weak_schema_diagnostics(
        _output_def_to_schema(output, allow_skill_scalar_type_shorthand),
        path,
        diagnostics,
    )


def build_weak_output_schema_diagnostic(path: str, message: str, expected: str) -> dict[str, Any]:
    return {
        "code": WEAK_OUTPUT_SCHEMA_CODE,
        "phase": "output_schema_validation",
        "location": path,
        "message": message,
        "expected": expected,
        "hint": "Generated workflow outputs are public contracts and must be concrete.",
        "llm_guidance": (
            "Use the exact output path and add a concrete schema. Arrays need items; object outputs and object array items "
            "need properties; do not use any."
        ),
    }


def _schema_to_contract(schema: Any) -> Any:
    if schema is None:
        return {"type": "any"}
    if isinstance(schema, str):
        return {"type": _normalize_type(schema)}
    if not isinstance(schema, dict):
        return {"type": "any"}

    if "type" not in schema:
        for union_key in ("anyOf", "any_of", "oneOf", "one_of"):
            variants = schema.get(union_key)
            if isinstance(variants, list):
                return {union_key: [_schema_to_contract(item) for item in variants]}
        if isinstance(schema.get("properties"), dict):
            schema["type"] = "object"
        elif "items" in schema:
            schema["type"] = "array"
        elif "additionalProperties" in schema or "additional_properties" in schema:
            schema["type"] = "dictionary"
        else:
            schema["type"] = "any"

    schema_type = schema.get("type")
    if isinstance(schema_type, list):
        return {"anyOf": [{"type": _normalize_type(item)} for item in schema_type]}

    schema["type"] = _normalize_type(str(schema_type))

    if isinstance(schema.get("properties"), dict):
        schema["properties"] = {str(name): _schema_to_contract(child) for name, child in schema["properties"].items()}
    if "items" in schema:
        schema["items"] = _schema_to_contract(schema.get("items"))
    for additional_key in ("additionalProperties", "additional_properties"):
        if isinstance(schema.get(additional_key), dict):
            schema[additional_key] = _schema_to_contract(schema[additional_key])
    for union_key in ("anyOf", "any_of", "oneOf", "one_of"):
        if isinstance(schema.get(union_key), list):
            schema[union_key] = [_schema_to_contract(item) for item in schema[union_key]]
    return schema


def _normalize_for_workflow_contract(schema: Any) -> Any:
    if not isinstance(schema, dict):
        return _schema_to_contract(schema)

    variants = _read_union_variants(schema)
    if variants is not None:
        normalized_variants = [
            _normalize_for_workflow_contract(variant)
            for variant in variants
            if _read_type(variant) != "null"
        ]
        if not normalized_variants:
            return {"type": "any"}
        if len(normalized_variants) == 1:
            return normalized_variants[0]
        variant_types = {_read_type(variant) for variant in normalized_variants}
        if variant_types == {"array"}:
            return {"type": "array", "items": {"anyOf": [variant.get("items", {"type": "any"}) for variant in normalized_variants]}}
        if variant_types <= {"object", "dictionary"}:
            return _merge_object_like_variants(normalized_variants)
        if len(variant_types) == 1:
            return normalized_variants[0]
        return {"type": "any"}

    schema_type = _read_type(schema)
    clone = copy.deepcopy(schema)
    if schema_type == "array" and isinstance(clone.get("items"), dict):
        clone["items"] = _normalize_for_workflow_contract(clone["items"])
    if schema_type in {"object", "dictionary"}:
        if isinstance(clone.get("properties"), dict):
            clone["properties"] = {name: _normalize_for_workflow_contract(child) for name, child in clone["properties"].items()}
        for additional_key in ("additionalProperties", "additional_properties"):
            if isinstance(clone.get(additional_key), dict):
                clone[additional_key] = _normalize_for_workflow_contract(clone[additional_key])
    return clone


def _merge_object_like_variants(variants: list[dict[str, Any]]) -> dict[str, Any]:
    names = sorted({name for variant in variants for name in (variant.get("properties") or {})})
    properties: dict[str, Any] = {}
    required: list[str] = []
    for name in names:
        property_variants = [
            (variant.get("properties") or {}).get(name)
            for variant in variants
            if name in (variant.get("properties") or {})
        ]
        property_variants = [variant for variant in property_variants if variant is not None]
        if not property_variants:
            continue
        properties[name] = _normalize_for_workflow_contract({"anyOf": property_variants})
        if len(property_variants) == len(variants) and all(name in _read_required_names(variant) for variant in variants):
            required.append(name)
    result: dict[str, Any] = {"type": "object", "properties": properties}
    if required:
        result["required_properties"] = required
    return result


def _canonicalize_workflow_contract_node(node: Any) -> Any:
    if isinstance(node, str):
        return {"type": _normalize_type(node)}
    if isinstance(node, list):
        return [_canonicalize_workflow_contract_node(item) for item in node]
    if not isinstance(node, dict):
        return copy.deepcopy(node)

    clone: dict[str, Any] = {}
    for key, value in node.items():
        if key == "required":
            clone["required_properties"] = copy.deepcopy(value)
            continue
        if key in {"properties"} and isinstance(value, dict):
            clone[key] = {name: _canonicalize_workflow_contract_node(child) for name, child in value.items()}
            continue
        if key in {"items", "additionalProperties", "additional_properties", "anyOf", "any_of", "oneOf", "one_of"}:
            clone[key] = _canonicalize_workflow_contract_node(value)
            continue
        clone[key] = copy.deepcopy(value)
    return clone


def _is_weak_schema(schema: Any) -> bool:
    schema = _normalize_for_workflow_contract(_schema_to_contract(schema))
    schema_type = _read_type(schema)
    if schema_type in {None, "any"} or schema.get("x-gnougo-opaque") is True:
        return True
    if schema_type == "array":
        return "items" not in schema or _is_weak_schema(schema.get("items"))
    if schema_type == "object":
        properties = schema.get("properties")
        return not isinstance(properties, dict) or not properties
    if schema_type == "dictionary":
        additional = schema.get("additionalProperties", schema.get("additional_properties"))
        return additional is None or additional is True or _is_weak_schema(additional)
    variants = _read_union_variants(schema)
    return variants is not None and (not variants or any(_is_weak_schema(variant) for variant in variants))


def _collect_weak_schema_diagnostics(schema: Any, path: str, diagnostics: list[dict[str, Any]]) -> None:
    schema = _normalize_for_workflow_contract(_schema_to_contract(schema))
    schema_type = _read_type(schema)
    if schema_type in {None, "any"} or schema.get("x-gnougo-opaque") is True:
        diagnostics.append(
            build_weak_output_schema_diagnostic(path, "Output schema uses type any.", "concrete scalar, object, array, or dictionary schema")
        )
        return
    if schema_type == "array":
        if "items" not in schema or _is_weak_schema(schema.get("items")):
            diagnostics.append(build_weak_output_schema_diagnostic(path, "Array output schema does not declare items.", "array with concrete items schema"))
        elif isinstance(schema.get("items"), dict):
            _collect_weak_schema_diagnostics(schema["items"], path + ".items", diagnostics)
        return
    if schema_type == "object":
        properties = schema.get("properties")
        if not isinstance(properties, dict) or not properties:
            diagnostics.append(
                build_weak_output_schema_diagnostic(
                    path,
                    "Object output schema does not declare properties.",
                    "object with non-empty properties",
                )
            )
            return
        for name, child in properties.items():
            _collect_weak_schema_diagnostics(child, f"{path}.properties.{name}", diagnostics)
        return
    if schema_type == "dictionary":
        additional = schema.get("additionalProperties", schema.get("additional_properties"))
        if additional is None or additional is True:
            diagnostics.append(
                build_weak_output_schema_diagnostic(
                    path,
                    "Dictionary output schema does not declare additional_properties.",
                    "dictionary with concrete additional_properties schema",
                )
            )
            return
        _collect_weak_schema_diagnostics(additional, path + ".additional_properties", diagnostics)
        return
    variants = _read_union_variants(schema)
    if variants is not None:
        if not variants:
            diagnostics.append(
                build_weak_output_schema_diagnostic(path, "Output schema uses type any.", "concrete scalar, object, array, or dictionary schema")
            )
        for variant in variants:
            _collect_weak_schema_diagnostics(variant, path, diagnostics)


def _output_def_to_schema(output: OutputDef, allow_skill_scalar_type_shorthand: bool) -> dict[str, Any]:
    schema_type = _normalize_type(output.type)
    if allow_skill_scalar_type_shorthand and schema_type == "any" and output.expr:
        maybe_type = _normalize_type(output.expr)
        if maybe_type != "any":
            schema_type = maybe_type
    schema: dict[str, Any] = {"type": schema_type}
    if output.description:
        schema["description"] = output.description
    if output.items is not None:
        schema["items"] = _output_def_to_schema(output.items, False)
    if output.properties:
        schema["properties"] = {name: _output_def_to_schema(child, False) for name, child in output.properties.items()}
    if output.additional_properties is not None:
        schema["additional_properties"] = _output_def_to_schema(output.additional_properties, False)
    if output.required_properties:
        schema["required_properties"] = list(output.required_properties)
    return schema


def _read_type(schema: Any) -> str | None:
    if not isinstance(schema, dict):
        return None
    type_value = schema.get("type")
    if isinstance(type_value, str):
        return _normalize_type(type_value)
    return None


def _read_union_variants(schema: dict[str, Any]) -> list[Any] | None:
    for key in ("anyOf", "any_of", "oneOf", "one_of"):
        value = schema.get(key)
        if isinstance(value, list):
            return value
    return None


def _read_required_names(schema: dict[str, Any]) -> set[str]:
    value = schema.get("required_properties", schema.get("required"))
    return {str(item) for item in value} if isinstance(value, list) else set()


def _normalize_type(type_name: Any) -> str:
    normalized = str(type_name or "any").strip().lower()
    if normalized in {"string", "number", "integer", "boolean", "array", "object", "dictionary", "null", "any"}:
        return normalized
    if normalized == "bool":
        return "boolean"
    return "any"
