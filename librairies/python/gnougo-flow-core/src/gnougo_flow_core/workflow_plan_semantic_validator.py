from __future__ import annotations

import copy
import json
import re
from dataclasses import dataclass
from typing import Any

from .expressions import _scan_expressions
from .models import OutputDef, StepDef, WorkflowDocument


@dataclass(slots=True)
class McpToolOutputContract:
    server_name: str
    tool_name: str
    input_schema: Any = None
    output_schema: Any = None
    example_response: Any = None


@dataclass(slots=True)
class WorkflowSemanticValidationError:
    code: str
    workflow_name: str | None
    step_id: str | None
    field: str
    invalid_path: str
    allowed_paths: list[str]
    suggestion: str
    message: str


class WorkflowSemanticValidationException(Exception):
    def __init__(self, errors: list[WorkflowSemanticValidationError]) -> None:
        self.errors = errors
        super().__init__(format_semantic_errors(errors))


_DATA_STEPS_PATH_RE = re.compile(r"\bdata\.steps\.([A-Za-z_][A-Za-z0-9_-]*)(?P<path>(?:\.[A-Za-z_][A-Za-z0-9_]*)*)")


def validate_workflow_semantics(
    document: WorkflowDocument,
    mcp_tool_contracts: list[McpToolOutputContract] | None = None,
) -> None:
    errors: list[WorkflowSemanticValidationError] = []
    mcp_contracts = {(c.server_name, c.tool_name): c for c in mcp_tool_contracts or []}

    for workflow_name, workflow in document.workflows.items():
        all_step_ids = set(_collect_step_ids(workflow.steps))
        known_contracts: dict[str, Any] = {}
        _validate_step_list(workflow.steps, workflow_name, known_contracts, all_step_ids, mcp_contracts, errors)
        if workflow.outputs:
            for output_name, output_def in workflow.outputs.items():
                _validate_output_def(output_def, workflow_name, f"outputs.{output_name}", known_contracts, all_step_ids, errors)

    if errors:
        raise WorkflowSemanticValidationException(errors)


def normalize_mcp_call_input_requests(
    document: WorkflowDocument,
    mcp_tool_contracts: list[McpToolOutputContract] | None = None,
) -> int:
    mcp_contracts = {(c.server_name, c.tool_name): c for c in mcp_tool_contracts or []}
    changes = 0
    for workflow in document.workflows.values():
        changes += _normalize_mcp_call_input_requests(workflow.steps, mcp_contracts)
    return changes


def format_semantic_errors(errors: list[WorkflowSemanticValidationError]) -> str:
    return json.dumps(
        {
            "error": "Generated workflow semantic validation failed",
            "errors": [
                {
                    "code": err.code,
                    "workflow": err.workflow_name,
                    "step": err.step_id,
                    "field": err.field,
                    "invalid_path": err.invalid_path,
                    "allowed_paths": err.allowed_paths,
                    "suggestion": err.suggestion,
                    "message": err.message,
                }
                for err in errors
            ],
        },
        ensure_ascii=False,
    )


def _collect_step_ids(steps: list[StepDef]) -> list[str]:
    ids: list[str] = []
    for step in steps:
        if step.id:
            ids.append(step.id)
        if step.steps:
            ids.extend(_collect_step_ids(step.steps))
        if step.branches:
            for branch in step.branches:
                ids.extend(_collect_step_ids(branch.steps))
        if step.cases:
            for case in step.cases:
                ids.extend(_collect_step_ids(case.steps))
        if step.default:
            ids.extend(_collect_step_ids(step.default))
    return ids


def _validate_step_list(
    steps: list[StepDef],
    workflow_name: str,
    known_contracts: dict[str, Any],
    all_step_ids: set[str],
    mcp_contracts: dict[tuple[str, str], McpToolOutputContract],
    errors: list[WorkflowSemanticValidationError],
) -> None:
    for step in steps:
        _validate_step(step, workflow_name, known_contracts, all_step_ids, mcp_contracts, errors)


def _normalize_mcp_call_input_requests(
    steps: list[StepDef],
    mcp_contracts: dict[tuple[str, str], McpToolOutputContract],
) -> int:
    changes = 0
    for step in steps:
        changes += _normalize_mcp_call_input_request(step, mcp_contracts)
        if step.steps:
            changes += _normalize_mcp_call_input_requests(step.steps, mcp_contracts)
        if step.branches:
            for branch in step.branches:
                changes += _normalize_mcp_call_input_requests(branch.steps, mcp_contracts)
        if step.cases:
            for case in step.cases:
                changes += _normalize_mcp_call_input_requests(case.steps, mcp_contracts)
        if step.default:
            changes += _normalize_mcp_call_input_requests(step.default, mcp_contracts)
    return changes


def _normalize_mcp_call_input_request(
    step: StepDef,
    mcp_contracts: dict[tuple[str, str], McpToolOutputContract],
) -> int:
    if step.type != "mcp.call" or not isinstance(step.input, dict):
        return 0

    kind = _try_get_input_string(step, "kind") or "tool"
    if kind.lower() != "tool":
        return 0

    server_name = _try_get_input_string(step, "server")
    method_name = _try_get_input_string(step, "method")
    if not server_name or not method_name:
        return 0

    contract = mcp_contracts.get((server_name, method_name))
    request = step.input.get("request")
    if contract is None or not isinstance(contract.input_schema, dict) or not isinstance(request, dict):
        return 0

    return _normalize_json_node_against_schema(request, contract.input_schema)


def _validate_step(
    step: StepDef,
    workflow_name: str,
    known_contracts: dict[str, Any],
    all_step_ids: set[str],
    mcp_contracts: dict[tuple[str, str], McpToolOutputContract],
    errors: list[WorkflowSemanticValidationError],
) -> None:
    _validate_string(step.if_, workflow_name, step.id, "if", known_contracts, all_step_ids, errors)
    _validate_string(step.expr, workflow_name, step.id, "expr", known_contracts, all_step_ids, errors)
    _validate_json(step.input, workflow_name, step.id, "input", known_contracts, all_step_ids, errors)
    _validate_mcp_call_input_request(step, workflow_name, mcp_contracts, errors)
    step_is_conditional = bool(step.if_ and str(step.if_).strip())

    if step.on_error:
        for index, case in enumerate(step.on_error.cases):
            _validate_string(case.if_, workflow_name, step.id, f"on_error.cases[{index}].if", known_contracts, all_step_ids, errors)
            _validate_json(case.set_output, workflow_name, step.id, f"on_error.cases[{index}].set_output", known_contracts, all_step_ids, errors)

    if step.type == "parallel" and step.branches:
        produced: dict[str, Any] = {}
        for branch in step.branches:
            branch_known = copy.deepcopy(known_contracts)
            _validate_step_list(branch.steps, workflow_name, branch_known, all_step_ids, mcp_contracts, errors)
            for key, value in branch_known.items():
                if key not in known_contracts:
                    produced[key] = copy.deepcopy(value)
        if not step_is_conditional:
            known_contracts.update(produced)
    elif step.type == "switch":
        if step.cases:
            for case in step.cases:
                _validate_string(case.when, workflow_name, step.id, "cases.when", known_contracts, all_step_ids, errors)

                # Only one switch branch runs, so case-local outputs are not guaranteed after the switch.
                case_known = copy.deepcopy(known_contracts)
                _validate_step_list(case.steps, workflow_name, case_known, all_step_ids, mcp_contracts, errors)

        if step.default:
            default_known = copy.deepcopy(known_contracts)
            _validate_step_list(step.default, workflow_name, default_known, all_step_ids, mcp_contracts, errors)
    elif step.type in {"loop.sequential", "loop.parallel"}:
        if step.steps:
            # Loop body outputs are not guaranteed after the loop because iterations may be zero.
            loop_known = copy.deepcopy(known_contracts)
            _validate_step_list(step.steps, workflow_name, loop_known, all_step_ids, mcp_contracts, errors)
    else:
        if step.steps:
            if step_is_conditional:
                conditional_known = copy.deepcopy(known_contracts)
                _validate_step_list(step.steps, workflow_name, conditional_known, all_step_ids, mcp_contracts, errors)
            else:
                _validate_step_list(step.steps, workflow_name, known_contracts, all_step_ids, mcp_contracts, errors)

    if step.id and not step_is_conditional:
        known_contracts[step.id] = _build_step_output_schema(step, mcp_contracts)


def _validate_output_def(
    output_def: OutputDef,
    workflow_name: str,
    field: str,
    known_contracts: dict[str, Any],
    all_step_ids: set[str],
    errors: list[WorkflowSemanticValidationError],
) -> None:
    _validate_string(output_def.expr, workflow_name, None, field, known_contracts, all_step_ids, errors)
    if output_def.properties:
        for property_name, property_def in output_def.properties.items():
            _validate_output_def(property_def, workflow_name, f"{field}.properties.{property_name}", known_contracts, all_step_ids, errors)
    if output_def.items:
        _validate_output_def(output_def.items, workflow_name, f"{field}.items", known_contracts, all_step_ids, errors)
    if output_def.additional_properties:
        _validate_output_def(output_def.additional_properties, workflow_name, f"{field}.additional_properties", known_contracts, all_step_ids, errors)


def _validate_json(
    node: Any,
    workflow_name: str,
    step_id: str | None,
    field: str,
    known_contracts: dict[str, Any],
    all_step_ids: set[str],
    errors: list[WorkflowSemanticValidationError],
) -> None:
    if isinstance(node, str):
        _validate_string(node, workflow_name, step_id, field, known_contracts, all_step_ids, errors)
    elif isinstance(node, dict):
        for key, value in node.items():
            _validate_json(value, workflow_name, step_id, f"{field}.{key}", known_contracts, all_step_ids, errors)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            _validate_json(value, workflow_name, step_id, f"{field}[{index}]", known_contracts, all_step_ids, errors)


def _validate_string(
    text: str | None,
    workflow_name: str,
    step_id: str | None,
    field: str,
    known_contracts: dict[str, Any],
    all_step_ids: set[str],
    errors: list[WorkflowSemanticValidationError],
) -> None:
    if not text or "data.steps." not in text:
        return
    for _start, _end, expression in _scan_expressions(text):
        for match in _DATA_STEPS_PATH_RE.finditer(expression):
            referenced_step_id = match.group(1)
            property_path = [part for part in match.group("path").split(".") if part]
            invalid_path = "data.steps." + referenced_step_id + ("." + ".".join(property_path) if property_path else "")

            if referenced_step_id not in known_contracts:
                exists_later = referenced_step_id in all_step_ids
                errors.append(
                    WorkflowSemanticValidationError(
                        code="STEP_REFERENCE_NOT_AVAILABLE" if exists_later else "STEP_REFERENCE_UNKNOWN",
                        workflow_name=workflow_name,
                        step_id=step_id,
                        field=field,
                        invalid_path=invalid_path,
                        allowed_paths=[f"data.steps.{key}" for key in sorted(known_contracts)],
                        suggestion=(
                            f"Move this reference after step '{referenced_step_id}' has executed, or move the producing step earlier."
                            if exists_later
                            else "Reference an existing previous step id, or add the missing producing step before this expression."
                        ),
                        message=(
                            f"Step '{referenced_step_id}' exists but is not available at this point in execution."
                            if exists_later
                            else f"Step '{referenced_step_id}' does not exist in this workflow."
                        ),
                    )
                )
                continue

            result = _validate_schema_path(known_contracts[referenced_step_id], property_path)
            if result[0]:
                continue
            is_opaque_response = result[2]
            prefix = f"data.steps.{referenced_step_id}"
            errors.append(
                WorkflowSemanticValidationError(
                    code="OPAQUE_RESPONSE_DEEP_ACCESS" if is_opaque_response else "STEP_OUTPUT_PROPERTY_UNKNOWN",
                    workflow_name=workflow_name,
                    step_id=step_id,
                    field=field,
                    invalid_path=invalid_path,
                    allowed_paths=list(_enumerate_allowed_paths(prefix, known_contracts[referenced_step_id])),
                    suggestion=_build_property_suggestion(prefix, referenced_step_id, is_opaque_response),
                    message=result[1],
                )
            )


@dataclass(slots=True)
class _SchemaValidationError:
    path: str
    message: str


def _validate_mcp_call_input_request(
    step: StepDef,
    workflow_name: str,
    mcp_contracts: dict[tuple[str, str], McpToolOutputContract],
    errors: list[WorkflowSemanticValidationError],
) -> None:
    if step.type != "mcp.call" or not isinstance(step.input, dict):
        return

    kind = _try_get_input_string(step, "kind") or "tool"
    if kind.lower() != "tool":
        return

    server_name = _try_get_input_string(step, "server")
    method_name = _try_get_input_string(step, "method")
    if not server_name or not method_name:
        return

    if not _validate_mcp_call_target_exists(step, workflow_name, server_name, method_name, mcp_contracts, errors):
        return

    contract = mcp_contracts.get((server_name, method_name))
    if contract is None or not isinstance(contract.input_schema, dict):
        return

    request_node = step.input.get("request")
    request_object = request_node if isinstance(request_node, dict) else {}
    schema_errors: list[_SchemaValidationError] = []
    _validate_json_node_against_schema(request_object, contract.input_schema, "", schema_errors)
    if not schema_errors:
        return

    allowed_paths = list(_enumerate_allowed_paths("input.request", contract.input_schema))[:64]
    for schema_error in schema_errors:
        invalid_path = "input.request" if not schema_error.path else f"input.request.{schema_error.path}"
        errors.append(
            WorkflowSemanticValidationError(
                code="MCP_REQUEST_SCHEMA_INVALID",
                workflow_name=workflow_name,
                step_id=step.id,
                field="input.request",
                invalid_path=invalid_path,
                allowed_paths=allowed_paths,
                suggestion=f"Align `mcp.call` request with MCP tool schema for server '{server_name}' method '{method_name}'.",
                message=f"mcp.call request for '{server_name}/{method_name}' is invalid: {schema_error.message}",
            )
        )


def _validate_mcp_call_target_exists(
    step: StepDef,
    workflow_name: str,
    server_name: str,
    method_name: str,
    mcp_contracts: dict[tuple[str, str], McpToolOutputContract],
    errors: list[WorkflowSemanticValidationError],
) -> bool:
    if not mcp_contracts:
        return True

    known_servers = sorted({server for server, _method in mcp_contracts})
    if server_name not in known_servers:
        errors.append(
            WorkflowSemanticValidationError(
                code="MCP_SERVER_UNKNOWN",
                workflow_name=workflow_name,
                step_id=step.id,
                field="input.server",
                invalid_path=f"input.server:{server_name}",
                allowed_paths=[f"mcp.server:{server}" for server in known_servers],
                suggestion="Use one of the MCP servers discovered for this plan, or add an `mcp.list` discovery step before calling it.",
                message=f"mcp.call references unknown MCP server '{server_name}'.",
            )
        )
        return False

    if (server_name, method_name) in mcp_contracts:
        return True

    known_methods = sorted({method for server, method in mcp_contracts if server == server_name})
    errors.append(
        WorkflowSemanticValidationError(
            code="MCP_METHOD_UNKNOWN",
            workflow_name=workflow_name,
            step_id=step.id,
            field="input.method",
            invalid_path=f"input.method:{method_name}",
            allowed_paths=[f"mcp.server:{server_name}.method:{method}" for method in known_methods],
            suggestion=f"Use one of the tools discovered for MCP server '{server_name}', or inspect the server with `mcp.list` before calling it.",
            message=f"mcp.call references unknown MCP method '{method_name}' on server '{server_name}'.",
        )
    )
    return False


def _validate_json_node_against_schema(value: Any, schema: Any, path: str, errors: list[_SchemaValidationError]) -> None:
    if not isinstance(schema, dict):
        return

    any_of = schema.get("anyOf")
    if isinstance(any_of, list):
        if _matches_any_schema_variant(value, any_of):
            return
        errors.append(_SchemaValidationError(path=path, message="value does not match any allowed schema variant"))
        return

    one_of = schema.get("oneOf")
    if isinstance(one_of, list):
        if _matches_any_schema_variant(value, one_of):
            return
        errors.append(_SchemaValidationError(path=path, message="value does not match any allowed schema variant"))
        return

    schema_type = _read_schema_type(schema)
    if schema_type == "object":
        _validate_object_against_schema(value, schema, path, errors)
    elif schema_type == "array":
        _validate_array_against_schema(value, schema, path, errors)
    elif schema_type in {"string", "number", "integer", "boolean", "null"}:
        _validate_primitive_type(value, schema_type, path, errors)


def _matches_any_schema_variant(value: Any, variants: list[Any]) -> bool:
    for variant in variants:
        variant_errors: list[_SchemaValidationError] = []
        _validate_json_node_against_schema(value, variant, "", variant_errors)
        if not variant_errors:
            return True
    return False


def _validate_object_against_schema(value: Any, schema: dict[str, Any], path: str, errors: list[_SchemaValidationError]) -> None:
    if _is_dynamic_expression_string(value):
        return
    if not isinstance(value, dict):
        errors.append(_SchemaValidationError(path=path, message="expected object"))
        return

    required = schema.get("required")
    if isinstance(required, list):
        for required_name in required:
            if not isinstance(required_name, str) or not required_name.strip():
                continue
            if required_name not in value:
                required_path = required_name if not path else f"{path}.{required_name}"
                errors.append(_SchemaValidationError(path=required_path, message="missing required property"))

    properties = schema.get("properties")
    for property_name, property_value in value.items():
        property_schema = properties.get(property_name) if isinstance(properties, dict) else None
        property_path = property_name if not path else f"{path}.{property_name}"

        if property_schema is not None:
            _validate_json_node_against_schema(property_value, property_schema, property_path, errors)
            continue

        if not _allows_additional_properties(schema):
            errors.append(_SchemaValidationError(path=property_path, message="property is not allowed by schema"))
            continue

        additional_schema = schema.get("additionalProperties")
        if isinstance(additional_schema, dict):
            _validate_json_node_against_schema(property_value, additional_schema, property_path, errors)


def _validate_array_against_schema(value: Any, schema: dict[str, Any], path: str, errors: list[_SchemaValidationError]) -> None:
    if _is_dynamic_expression_string(value):
        return
    if not isinstance(value, list):
        errors.append(_SchemaValidationError(path=path, message="expected array"))
        return

    if "items" not in schema:
        return
    for index, item in enumerate(value):
        item_path = f"[{index}]" if not path else f"{path}[{index}]"
        _validate_json_node_against_schema(item, schema.get("items"), item_path, errors)


def _validate_primitive_type(value: Any, expected_type: str, path: str, errors: list[_SchemaValidationError]) -> None:
    if _is_dynamic_expression_string(value):
        return

    if value is None:
        if expected_type != "null":
            errors.append(_SchemaValidationError(path=path, message=f"expected {expected_type} but got null"))
        return

    is_valid = False
    if expected_type == "string":
        is_valid = isinstance(value, str)
    elif expected_type == "number":
        is_valid = isinstance(value, (int, float)) and not isinstance(value, bool)
    elif expected_type == "integer":
        is_valid = isinstance(value, int) and not isinstance(value, bool)
    elif expected_type == "boolean":
        is_valid = isinstance(value, bool)
    elif expected_type == "null":
        is_valid = value is None

    if not is_valid:
        errors.append(_SchemaValidationError(path=path, message=f"expected {expected_type}"))


def _normalize_json_node_against_schema(value: Any, schema: Any) -> int:
    if not isinstance(schema, dict):
        return 0

    any_of = schema.get("anyOf")
    if isinstance(any_of, list):
        return _normalize_against_single_matching_variant(value, any_of)

    one_of = schema.get("oneOf")
    if isinstance(one_of, list):
        return _normalize_against_single_matching_variant(value, one_of)

    schema_type = _read_schema_type(schema)
    if schema_type == "object":
        return _normalize_object_against_schema(value, schema)
    if schema_type == "array":
        return _normalize_array_against_schema(value, schema)
    return 0


def _normalize_against_single_matching_variant(value: Any, variants: list[Any]) -> int:
    selected_variant: dict[str, Any] | None = None
    for variant in variants:
        if not isinstance(variant, dict):
            continue

        variant_errors: list[_SchemaValidationError] = []
        _validate_json_node_against_schema(value, variant, "", variant_errors)
        if not variant_errors:
            return 0

        clone = copy.deepcopy(value)
        changes = _normalize_json_node_against_schema(clone, variant)
        if changes == 0:
            continue

        variant_errors = []
        _validate_json_node_against_schema(clone, variant, "", variant_errors)
        if not variant_errors:
            if selected_variant is not None:
                return 0
            selected_variant = variant

    if selected_variant is None:
        return 0
    return _normalize_json_node_against_schema(value, selected_variant)


def _normalize_object_against_schema(value: Any, schema: dict[str, Any]) -> int:
    if _is_dynamic_expression_string(value) or not isinstance(value, dict):
        return 0

    changes = 0
    properties = schema.get("properties")
    additional_schema = schema.get("additionalProperties")
    for property_name, property_value in list(value.items()):
        property_schema = properties.get(property_name) if isinstance(properties, dict) else None
        if property_schema is None and isinstance(additional_schema, dict):
            property_schema = additional_schema
        if property_schema is None:
            continue

        changes += _normalize_json_node_against_schema(property_value, property_schema)
        coerced, did_coerce = _try_coerce_json_value(property_value, property_schema)
        if did_coerce:
            value[property_name] = coerced
            changes += 1

    return changes


def _normalize_array_against_schema(value: Any, schema: dict[str, Any]) -> int:
    if _is_dynamic_expression_string(value) or not isinstance(value, list) or "items" not in schema:
        return 0

    item_schema = schema.get("items")
    changes = 0
    for index, item in enumerate(list(value)):
        changes += _normalize_json_node_against_schema(item, item_schema)
        coerced, did_coerce = _try_coerce_json_value(item, item_schema)
        if did_coerce:
            value[index] = coerced
            changes += 1
    return changes


def _try_coerce_json_value(value: Any, schema: Any) -> tuple[Any, bool]:
    if _is_dynamic_expression_string(value) or not isinstance(schema, dict) or not isinstance(value, str):
        return value, False

    schema_type = _read_schema_type(schema)
    text = value.strip()
    if schema_type == "number":
        try:
            return float(text), True
        except ValueError:
            return value, False
    if schema_type == "integer":
        try:
            return int(text, 10), True
        except ValueError:
            return value, False
    if schema_type == "boolean":
        if text.lower() == "true":
            return True, True
        if text.lower() == "false":
            return False, True
    return value, False


def _is_dynamic_expression_string(value: Any) -> bool:
    return isinstance(value, str) and "${" in value


def _read_schema_type(schema: dict[str, Any]) -> str | None:
    type_value = schema.get("type")
    if isinstance(type_value, str):
        return type_value
    if isinstance(type_value, list):
        for item in type_value:
            if isinstance(item, str) and item.strip() and item != "null":
                return item
    return None


def _allows_additional_properties(schema: dict[str, Any]) -> bool:
    if "additionalProperties" not in schema:
        return False
    additional = schema.get("additionalProperties")
    return additional is True or isinstance(additional, dict)


def _build_property_suggestion(prefix: str, referenced_step_id: str, is_opaque_response: bool) -> str:
    if is_opaque_response:
        return (
            f"Do not invent fields under '{prefix}.response'. Use json(data.steps.{referenced_step_id}.response), "
            "or add an llm.call normalization step with structured_output before accessing named fields."
        )
    return (
        f"Use one of the allowed paths for step '{referenced_step_id}', or add a normalization step "
        "that produces the desired property with structured_output."
    )


def _validate_schema_path(schema: Any, path: list[str]) -> tuple[bool, str, bool]:
    if not path:
        return True, "", False
    if not isinstance(schema, dict):
        return False, f"Output path '{'.'.join(path)}' is opaque and has no known object schema.", False
    if schema.get("x-gnougo-opaque") is True:
        return False, f"Output path '{'.'.join(path)}' crosses an opaque value with no known schema.", True
    if schema.get("type") == "array":
        return False, f"Output path '{'.'.join(path)}' tries to read object properties from an array output.", False

    properties = schema.get("properties")
    segment = path[0]
    if not isinstance(properties, dict) or segment not in properties:
        additional = schema.get("additionalProperties")
        if additional is True or isinstance(additional, dict):
            return True, "", False
        return False, f"Property '{segment}' is not defined by the output schema.", False
    return _validate_schema_path(properties[segment], path[1:])


def _enumerate_allowed_paths(prefix: str, schema: Any, depth: int = 0):
    yield prefix
    if depth >= 4 or not isinstance(schema, dict) or schema.get("x-gnougo-opaque") is True:
        return
    properties = schema.get("properties")
    if not isinstance(properties, dict):
        return
    for property_name in sorted(properties):
        child_prefix = f"{prefix}.{property_name}"
        yield child_prefix
        child_schema = properties[property_name]
        if isinstance(child_schema, dict) and child_schema.get("x-gnougo-opaque") is not True:
            for nested in list(_enumerate_allowed_paths(child_prefix, child_schema, depth + 1))[1:]:
                yield nested


def _build_step_output_schema(step: StepDef, mcp_contracts: dict[tuple[str, str], McpToolOutputContract]) -> Any:
    if step.type == "set":
        return _build_set_output_schema(step)
    if step.type == "template.render":
        mode = _try_get_input_string(step, "mode") or "text"
        meta_schema = _object_schema(("engine", _string_schema()))
        if mode == "json":
            return _object_schema(("json", _opaque_schema()), ("meta", meta_schema))
        return _object_schema(("text", _string_schema()), ("meta", meta_schema))
    if step.type == "llm.call":
        return _object_schema(
            ("text", _string_schema()),
            ("json", _get_structured_output_schema(step) or _opaque_schema()),
            ("usage", _object_schema()),
            ("meta", _object_schema(("model", _string_schema()))),
            ("raw", _opaque_schema()),
        )
    if step.type == "mcp.call":
        return _build_mcp_call_output_schema(step, mcp_contracts)
    if step.type == "mcp.list":
        return _object_schema(
            ("status", _string_schema()),
            ("text", _string_schema()),
            ("servers", _array_schema()),
            ("tools", _array_schema()),
            ("resources", _array_schema()),
            ("prompts", _array_schema()),
        )
    if step.type == "workflow.plan":
        return _object_schema(("workflow", _object_schema()), ("yaml", _string_schema()), ("meta", _object_schema()), ("diagnostics", _array_schema()))
    if step.type == "workflow.execute":
        return _object_schema(
            ("outputs", _opaque_schema()),
            ("workflow", _string_schema()),
            ("run", _object_schema(("steps_executed", _number_schema()), ("success", _boolean_schema()))),
        )
    if step.type == "sequence":
        return _object_schema(("steps", _object_schema()), ("count", _number_schema()))
    if step.type == "parallel":
        return _object_schema(("branches", _array_schema()), ("count", _number_schema()))
    if step.type in {"loop.sequential", "loop.parallel"}:
        return _object_schema(("results", _array_schema()), ("count", _number_schema()))
    if step.type == "switch":
        return _object_schema(("matched", _string_schema()), ("steps", _object_schema()))
    if step.type == "emit":
        return _object_schema(("event", _opaque_schema()), ("status", _string_schema()))
    if step.type == "human.input":
        return _build_human_input_output_schema(step)
    return _opaque_schema()


def _build_human_input_output_schema(step: StepDef) -> Any:
    properties: list[tuple[str, Any]] = [("response", _opaque_schema()), ("source", _string_schema())]
    input_obj = step.input if isinstance(step.input, dict) else {}
    fields = input_obj.get("fields")
    if isinstance(fields, list):
        for field in fields:
            if not isinstance(field, dict):
                continue
            name = field.get("name")
            if not isinstance(name, str) or not name.strip():
                continue
            field_type = field.get("type", "string")
            properties.append((name, _human_input_field_schema(str(field_type))))
    return _object_schema(*properties)


def _human_input_field_schema(field_type: str) -> Any:
    field_type = field_type.lower()
    if field_type in {"number", "integer"}:
        return _number_schema()
    if field_type == "boolean":
        return _boolean_schema()
    if field_type == "json":
        return _opaque_schema()
    if field_type in {"multiselect", "checkbox", "file", "directory"}:
        return _array_schema()
    return _string_schema()


def _build_set_output_schema(step: StepDef) -> Any:
    if not isinstance(step.input, dict):
        return _object_schema()
    return _object_schema(*[(str(key), _infer_schema_from_example(value) or _opaque_schema()) for key, value in step.input.items()])


def _build_mcp_call_output_schema(step: StepDef, mcp_contracts: dict[tuple[str, str], McpToolOutputContract]) -> Any:
    input_obj = step.input if isinstance(step.input, dict) else {}
    kind = str(input_obj.get("kind", "tool")).lower()
    structured_json_schema = _get_structured_output_schema(step)
    if input_obj.get("prompt") is not None:
        return _object_schema(
            ("status", _string_schema()),
            ("selection_mode", _string_schema()),
            ("text", _string_schema()),
            ("selection_text", _string_schema()),
            ("tool_calls", _array_schema()),
            ("results", _array_schema()),
            ("json", structured_json_schema or _opaque_schema()),
        )
    if kind == "prompt":
        return _object_schema(("status", _string_schema()), ("description", _string_schema()), ("messages", _array_schema()), ("text", _string_schema()))

    response_schema: Any = _opaque_schema()
    server_name = _try_get_input_string(step, "server")
    method_name = _try_get_input_string(step, "method")
    if server_name and method_name and (server_name, method_name) in mcp_contracts:
        contract = mcp_contracts[(server_name, method_name)]
        response_schema = copy.deepcopy(contract.output_schema) or _infer_schema_from_example(contract.example_response) or _opaque_schema()

    return _object_schema(
        ("status", _string_schema()),
        ("response", response_schema),
        ("error", _object_schema()),
        ("correlation_id", _string_schema()),
        ("trace_id", _string_schema()),
        ("results", _array_schema()),
    )


def _get_structured_output_schema(step: StepDef) -> Any:
    if not isinstance(step.input, dict):
        return None
    structured = step.input.get("structured_output")
    if not isinstance(structured, dict):
        return None
    return copy.deepcopy(structured.get("schema_inline") or structured.get("schema_ref"))


def _try_get_input_string(step: StepDef, property_name: str) -> str | None:
    if not isinstance(step.input, dict):
        return None
    value = step.input.get(property_name)
    if isinstance(value, str) and value.strip() and "${" not in value:
        return value
    return None


def _infer_schema_from_example(example: Any) -> Any:
    if example is None:
        return None
    if isinstance(example, dict):
        return {
            "type": "object",
            "properties": {str(key): _infer_schema_from_example(value) or _opaque_schema() for key, value in example.items()},
            "additionalProperties": False,
        }
    if isinstance(example, list):
        return {"type": "array", "items": _infer_schema_from_example(example[0]) if example else _opaque_schema()}
    if isinstance(example, bool):
        return _boolean_schema()
    if isinstance(example, (int, float)):
        return _number_schema()
    if isinstance(example, str):
        return _string_schema()
    return _opaque_schema()


def _object_schema(*properties: tuple[str, Any]) -> dict[str, Any]:
    return {
        "type": "object",
        "properties": {name: copy.deepcopy(schema) for name, schema in properties},
        "additionalProperties": False,
    }


def _string_schema() -> dict[str, str]:
    return {"type": "string"}


def _number_schema() -> dict[str, str]:
    return {"type": "number"}


def _boolean_schema() -> dict[str, str]:
    return {"type": "boolean"}


def _array_schema() -> dict[str, Any]:
    return {"type": "array", "items": _opaque_schema()}


def _opaque_schema() -> dict[str, bool]:
    return {"x-gnougo-opaque": True}
