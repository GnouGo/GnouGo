from __future__ import annotations

import copy
from dataclasses import dataclass
from typing import Any

JSON_TYPES = {"object", "array", "string", "number", "integer", "boolean", "null"}
STRUCTURED_OUTPUT_FIELDS = {"schema_inline", "schema_ref", "strict"}
UNSUPPORTED_STRICT_KEYWORDS = {"allOf", "oneOf", "uniqueItems", "minProperties", "maxProperties"}
UNSUPPORTED_RUNTIME_KEYWORDS = {
    "not",
    "dependentRequired",
    "dependentSchemas",
    "if",
    "then",
    "else",
    "patternProperties",
    "contains",
    "minContains",
    "maxContains",
    "prefixItems",
    "propertyNames",
    "unevaluatedProperties",
    "unevaluatedItems",
}
SUPPORTED_STRICT_FORMATS = {"date-time", "time", "date", "duration", "email", "hostname", "ipv4", "ipv6", "uuid"}


@dataclass(slots=True)
class StructuredOutputContract:
    schema: Any = None
    strict: bool | None = None
    errors: list[str] | None = None
    is_dynamic: bool = False


def validate_structured_output(structured_output: Any, allow_dynamic_schema_reference: bool) -> StructuredOutputContract:
    errors: list[str] = []
    if not isinstance(structured_output, dict):
        return StructuredOutputContract(errors=["structured_output must be an object"])

    for field in structured_output:
        if field not in STRUCTURED_OUTPUT_FIELDS:
            errors.append(f"structured_output.{field}: unknown field; allowed fields are schema_inline, schema_ref, strict")

    has_inline = structured_output.get("schema_inline") is not None
    has_reference = structured_output.get("schema_ref") is not None
    if has_inline == has_reference:
        errors.append(
            "structured_output: schema_inline and schema_ref are mutually exclusive"
            if has_inline
            else "structured_output: exactly one of schema_inline or schema_ref is required"
        )

    strict: bool | None = None
    if "strict" in structured_output and structured_output.get("strict") is not None:
        strict_value = structured_output.get("strict")
        if isinstance(strict_value, bool):
            strict = strict_value
        else:
            errors.append("structured_output.strict: expected boolean")

    schema = structured_output.get("schema_inline") if has_inline else structured_output.get("schema_ref") if has_reference else None
    if isinstance(schema, str) and "${" in schema:
        if allow_dynamic_schema_reference and has_reference:
            return StructuredOutputContract(schema=None, strict=strict, errors=errors, is_dynamic=True)
        errors.append("structured_output.schema_ref: expression did not resolve to a JSON Schema object")
        return StructuredOutputContract(schema=None, strict=strict, errors=errors)

    if schema is not None:
        schema = normalize_schema(copy.deepcopy(schema))
        errors.extend(validate_schema(schema, strict_profile=strict is True))

    return StructuredOutputContract(schema=schema, strict=strict, errors=errors)


def normalize_schema(schema: Any) -> Any:
    if not isinstance(schema, dict):
        return schema

    if "type" in schema:
        if schema["type"] is None:
            schema["type"] = "null"
        elif isinstance(schema["type"], list):
            schema["type"] = ["null" if item is None else item for item in schema["type"]]

    _normalize_bool(schema, "additionalProperties")
    _normalize_bool(schema, "uniqueItems")
    for keyword in ("minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties"):
        _normalize_int(schema, keyword)
    for keyword in ("minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf"):
        _normalize_number(schema, keyword)

    properties = schema.get("properties")
    if isinstance(properties, dict):
        for child in properties.values():
            normalize_schema(child)
    for keyword in ("items", "additionalProperties"):
        if isinstance(schema.get(keyword), dict):
            normalize_schema(schema[keyword])
    for keyword in ("anyOf", "oneOf", "allOf"):
        variants = schema.get(keyword)
        if isinstance(variants, list):
            for child in variants:
                normalize_schema(child)
    for keyword in ("$defs", "definitions"):
        definitions = schema.get(keyword)
        if isinstance(definitions, dict):
            for child in definitions.values():
                normalize_schema(child)
    return schema


def validate_schema(schema: Any, strict_profile: bool = False) -> list[str]:
    errors: list[str] = []
    if not isinstance(schema, dict):
        return ["structured_output schema must be a JSON Schema object"]
    stats = {"properties": 0, "depth": 1, "enum": 0, "text": 0}
    _validate_schema_node(schema, schema, "$", True, strict_profile, errors, stats, 1)
    if strict_profile:
        if stats["properties"] > 5000:
            errors.append(f"$: strict structured output supports at most 5000 object properties, found {stats['properties']}")
        if stats["depth"] > 10:
            errors.append(f"$: strict structured output supports at most 10 levels of nesting, found {stats['depth']}")
        if stats["enum"] > 1000:
            errors.append(f"$: strict structured output supports at most 1000 enum values, found {stats['enum']}")
        if stats["text"] > 120000:
            errors.append("$: strict structured output property/definition/enum/const text exceeds 120000 characters")
    return errors


def validate_instance(instance: Any, schema: Any) -> list[str]:
    if not isinstance(schema, dict):
        return ["$: schema must be an object"]
    errors: list[str] = []
    _validate_instance_node(instance, schema, schema, "$", errors, set())
    return errors


def _validate_schema_node(
    schema: Any,
    root: dict[str, Any],
    path: str,
    is_root: bool,
    strict_profile: bool,
    errors: list[str],
    stats: dict[str, int],
    depth: int,
) -> None:
    if not isinstance(schema, dict):
        errors.append(f"{path}: schema node must be an object")
        return

    stats["depth"] = max(stats["depth"], depth)

    for keyword in UNSUPPORTED_RUNTIME_KEYWORDS:
        if keyword in schema:
            errors.append(f"{path}.{keyword}: unsupported JSON Schema keyword")
    if strict_profile:
        for keyword in UNSUPPORTED_STRICT_KEYWORDS:
            if keyword in schema:
                errors.append(f"{path}.{keyword}: unsupported in strict structured output")

    schema_type = schema.get("type")
    types = _read_schema_types(schema)
    if schema_type is not None and not types:
        errors.append(f"{path}.type: expected one of {', '.join(sorted(JSON_TYPES))}")

    enum_values = schema.get("enum")
    if enum_values is not None:
        if not isinstance(enum_values, list):
            errors.append(f"{path}.enum: expected array")
        else:
            stats["enum"] += len(enum_values)
            stats["text"] += sum(len(str(item)) for item in enum_values)

    if "const" in schema:
        stats["text"] += len(str(schema.get("const")))

    fmt = schema.get("format")
    if strict_profile and isinstance(fmt, str) and fmt not in SUPPORTED_STRICT_FORMATS:
        errors.append(f"{path}.format: unsupported strict structured output format '{fmt}'")

    properties = schema.get("properties")
    if properties is not None:
        if not isinstance(properties, dict):
            errors.append(f"{path}.properties: expected object")
        else:
            stats["properties"] += len(properties)
            stats["text"] += sum(len(str(name)) for name in properties)
            if strict_profile:
                required = schema.get("required")
                if not isinstance(required, list):
                    errors.append(f"{path}.required: strict object schemas must list every property")
                else:
                    missing = [name for name in properties if name not in required]
                    if missing:
                        errors.append(f"{path}.required: strict object schemas must include every property; missing {', '.join(missing)}")
                if schema.get("additionalProperties") is not False:
                    errors.append(f"{path}.additionalProperties: strict object schemas must set additionalProperties: false")
            for name, child in properties.items():
                _validate_schema_node(child, root, f"{path}.properties.{name}", False, strict_profile, errors, stats, depth + 1)

    required = schema.get("required")
    if required is not None:
        if not isinstance(required, list) or any(not isinstance(item, str) or not item for item in required):
            errors.append(f"{path}.required: expected array of property names")
        elif isinstance(properties, dict):
            for item in required:
                if item not in properties:
                    errors.append(f"{path}.required: property '{item}' is not declared in properties")

    items = schema.get("items")
    if items is not None:
        _validate_schema_node(items, root, f"{path}.items", False, strict_profile, errors, stats, depth + 1)

    additional = schema.get("additionalProperties")
    if isinstance(additional, dict):
        _validate_schema_node(additional, root, f"{path}.additionalProperties", False, strict_profile, errors, stats, depth + 1)
    elif additional is not None and not isinstance(additional, bool):
        errors.append(f"{path}.additionalProperties: expected boolean or schema object")

    for keyword in ("anyOf", "oneOf", "allOf"):
        variants = schema.get(keyword)
        if variants is None:
            continue
        if not isinstance(variants, list) or not variants:
            errors.append(f"{path}.{keyword}: expected non-empty array")
            continue
        for index, variant in enumerate(variants):
            _validate_schema_node(variant, root, f"{path}.{keyword}[{index}]", False, strict_profile, errors, stats, depth + 1)

    for keyword in ("$defs", "definitions"):
        definitions = schema.get(keyword)
        if definitions is None:
            continue
        if not isinstance(definitions, dict):
            errors.append(f"{path}.{keyword}: expected object")
            continue
        for name, definition in definitions.items():
            _validate_schema_node(definition, root, f"{path}.{keyword}.{name}", False, strict_profile, errors, stats, depth + 1)

    if is_root and strict_profile and "object" not in types:
        errors.append("$: strict structured output root schema must have type object")


def _validate_instance_node(instance: Any, schema: Any, root: dict[str, Any], path: str, errors: list[str], refs_seen: set[str]) -> None:
    if not isinstance(schema, dict):
        return
    if isinstance(instance, str) and "${" in instance:
        return

    ref = schema.get("$ref")
    if isinstance(ref, str):
        resolved = _resolve_ref(ref, root)
        if resolved is None:
            errors.append(f"{path}: unresolved schema reference {ref}")
            return
        if ref in refs_seen:
            return
        _validate_instance_node(instance, resolved, root, path, errors, refs_seen | {ref})
        return

    for keyword in ("anyOf", "oneOf"):
        variants = schema.get(keyword)
        if isinstance(variants, list):
            if any(not validate_instance(instance, variant) for variant in variants):
                return
            errors.append(f"{path}: value does not match any allowed schema variant")
            return

    all_of = schema.get("allOf")
    if isinstance(all_of, list):
        for variant in all_of:
            _validate_instance_node(instance, variant, root, path, errors, refs_seen)
        return

    if "enum" in schema and isinstance(schema.get("enum"), list) and instance not in schema["enum"]:
        errors.append(f"{path}: value is not in enum")
        return
    if "const" in schema and instance != schema.get("const"):
        errors.append(f"{path}: value does not match const")
        return

    types = _read_schema_types(schema)
    if not types:
        if isinstance(schema.get("properties"), dict):
            types = ["object"]
        else:
            return

    if instance is None:
        if "null" not in types:
            errors.append(f"{path}: expected {'/'.join(types)} but got null")
        return

    if "object" in types:
        if isinstance(instance, dict):
            _validate_object_instance(instance, schema, root, path, errors, refs_seen)
            return
        if len(types) == 1:
            errors.append(f"{path}: expected object")
            return

    if "array" in types:
        if isinstance(instance, list):
            item_schema = schema.get("items")
            if item_schema is not None:
                for index, item in enumerate(instance):
                    _validate_instance_node(item, item_schema, root, f"{path}[{index}]", errors, refs_seen)
            return
        if len(types) == 1:
            errors.append(f"{path}: expected array")
            return

    if _matches_primitive_type(instance, types):
        return

    errors.append(f"{path}: expected {'/'.join(types)}")


def _validate_object_instance(
    instance: dict[str, Any],
    schema: dict[str, Any],
    root: dict[str, Any],
    path: str,
    errors: list[str],
    refs_seen: set[str],
) -> None:
    required = schema.get("required")
    if isinstance(required, list):
        for name in required:
            if isinstance(name, str) and name not in instance:
                errors.append(f"{path}.{name}: missing required property")

    properties = schema.get("properties")
    additional = schema.get("additionalProperties")
    for name, value in instance.items():
        property_path = f"{path}.{name}"
        property_schema = properties.get(name) if isinstance(properties, dict) else None
        if property_schema is not None:
            _validate_instance_node(value, property_schema, root, property_path, errors, refs_seen)
            continue

        if additional is False:
            errors.append(f"{property_path}: property is not allowed by schema")
            continue
        if isinstance(additional, dict):
            _validate_instance_node(value, additional, root, property_path, errors, refs_seen)


def _matches_primitive_type(value: Any, types: list[str]) -> bool:
    for expected in types:
        if expected == "string" and isinstance(value, str):
            return True
        if expected == "number" and isinstance(value, (int, float)) and not isinstance(value, bool):
            return True
        if expected == "integer" and isinstance(value, int) and not isinstance(value, bool):
            return True
        if expected == "boolean" and isinstance(value, bool):
            return True
        if expected == "null" and value is None:
            return True
    return False


def _read_schema_types(schema: dict[str, Any]) -> list[str]:
    type_value = schema.get("type")
    if isinstance(type_value, str):
        return [type_value] if type_value in JSON_TYPES else []
    if isinstance(type_value, list):
        result = []
        for item in type_value:
            if isinstance(item, str) and item in JSON_TYPES and item not in result:
                result.append(item)
        return result
    return []


def _resolve_ref(ref: str, root: dict[str, Any]) -> Any:
    if not ref.startswith("#/"):
        return None
    current: Any = root
    for raw_part in ref[2:].split("/"):
        part = raw_part.replace("~1", "/").replace("~0", "~")
        if not isinstance(current, dict) or part not in current:
            return None
        current = current[part]
    return current


def _normalize_bool(schema: dict[str, Any], keyword: str) -> None:
    value = schema.get(keyword)
    if isinstance(value, str) and value.strip().lower() in {"true", "false"}:
        schema[keyword] = value.strip().lower() == "true"


def _normalize_int(schema: dict[str, Any], keyword: str) -> None:
    value = schema.get(keyword)
    if isinstance(value, str):
        try:
            schema[keyword] = int(value.strip())
        except ValueError:
            return


def _normalize_number(schema: dict[str, Any], keyword: str) -> None:
    value = schema.get(keyword)
    if isinstance(value, str):
        try:
            schema[keyword] = float(value.strip())
        except ValueError:
            return
