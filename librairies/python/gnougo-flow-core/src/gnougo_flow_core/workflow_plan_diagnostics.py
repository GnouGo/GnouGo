from __future__ import annotations

import copy
import json
from typing import Any

from .compilation import ValidationError, WorkflowCompilationException
from .errors import ErrorCodes
from .workflow_plan_semantic_validator import (
    WorkflowSemanticValidationError,
    WorkflowSemanticValidationException,
)


def build_validation_failure_details(
    validation_errors: list[ValidationError],
    semantic_exception: WorkflowSemanticValidationException | None,
    compilation_exception: Exception | None,
    phase: str = "validation",
) -> dict[str, Any]:
    diagnostics: list[dict[str, Any]] = []
    seen: set[tuple[Any, ...]] = set()

    for error in validation_errors:
        _add_diagnostic(diagnostics, seen, _build_validation_diagnostic(error, "workflow_validation"))

    if semantic_exception is not None:
        for error in semantic_exception.errors:
            _add_diagnostic(diagnostics, seen, _build_semantic_diagnostic(error))

    if isinstance(compilation_exception, WorkflowCompilationException):
        for error in compilation_exception.errors:
            _add_diagnostic(diagnostics, seen, _build_validation_diagnostic(error, "compilation"))
    elif compilation_exception is not None:
        _add_diagnostic(
            diagnostics,
            seen,
            _build_exception_diagnostic(
                infer_plan_error_code(str(compilation_exception)),
                "compilation",
                str(compilation_exception),
                compilation_exception,
            ),
        )

    return _build_plan_details(
        phase,
        diagnostics,
        "Generated workflow validation failed.",
        [
            "Fix each diagnostic in order, starting with YAML shape and input contract errors before semantic references.",
            "Use the diagnostic location, expected value, and allowed paths to make the smallest valid YAML change.",
            "Do not invent step types, MCP servers, MCP methods, request fields, or step output fields that are not listed in the diagnostics.",
        ],
    )


def build_dry_run_failure_details(
    code: str,
    message: str,
    failure_kind: str,
    exception: Exception | None = None,
    runtime_details: Any = None,
) -> dict[str, Any]:
    diagnostics = [
        _build_dry_run_diagnostic(
            code,
            message,
            failure_kind,
            exception,
            runtime_details,
        )
    ]
    return _build_plan_details(
        "dry_run",
        diagnostics,
        "Generated workflow dry_run failed.",
        [
            "Dry-run executes the generated workflow with deterministic fake providers, so failures usually mean an expression, "
            "input contract, MCP request, or step dependency is invalid.",
            "Fix the exact failing step or field, then re-check downstream references that consume it.",
            "Keep sample-safe values and do not replace missing required data with secrets, environment variables, empty strings, or fake production data.",
        ],
    )


def build_exception_details(
    code: str,
    phase: str,
    message: str,
    exception: Exception | None = None,
) -> dict[str, Any]:
    diagnostics = [_build_exception_diagnostic(code, phase, message, exception)]
    return _build_plan_details(
        phase,
        diagnostics,
        message,
        [
            "Use the diagnostic code and message to repair the generated YAML before retrying.",
            "If this is a parser error, fix YAML syntax and root structure before changing workflow logic.",
        ],
    )


def build_mcp_discovery_coverage_details(code: str, message: str, hint: str) -> dict[str, Any]:
    return _build_plan_details(
        "validation",
        [
            {
                "code": code,
                "phase": "mcp_discovery_coverage",
                "message": message,
                "location": "mcp.discovery",
                "hint": hint,
                "llm_guidance": hint,
            }
        ],
        f"1 diagnostic(s): {code}",
        [
            "Use only MCP servers and tools that were discovered for this workflow.plan run.",
            "When discovery is unavailable, avoid generating mcp.call steps that require a catalog contract.",
        ],
    )


def format_validation_errors(errors: list[ValidationError]) -> str:
    formatted: list[str] = []
    for error in errors:
        location_parts: list[str] = []
        if error.workflow_name:
            location_parts.append(f"workflow '{error.workflow_name}'")
        if error.step_id:
            location_parts.append(f"step '{error.step_id}'")
        if error.field:
            location_parts.append(f"field '{error.field}'")
        prefix = f"[{', '.join(location_parts)}] " if location_parts else ""
        formatted.append(f"{prefix}{error.code}: {error.message}")
    return "; ".join(formatted)


def to_prompt_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2)


def build_structured_plan_error(exc: Exception, attempt: int | None = None) -> str:
    message = str(exc).strip()
    code = infer_plan_error_code(message, getattr(exc, "code", None))
    root: dict[str, Any] = {
        "code": code,
        "message": message,
        "legacy_summary": f"code={code}; message={message}",
    }
    if attempt is not None:
        root["attempt"] = attempt

    details = _clone_prompt_safe_details(getattr(exc, "details", None))
    if details is not None:
        root["details"] = details

    return to_prompt_json(root)


def infer_plan_error_code(message: str, exception_code: str | None = None) -> str:
    lower = message.lower()
    if "mcp_server_unknown" in lower:
        return "MCP_SERVER_UNKNOWN"
    if "mcp_method_unknown" in lower:
        return "MCP_METHOD_UNKNOWN"
    if "mcp tool" in lower and "does not exist" in lower:
        return "MCP_METHOD_UNKNOWN"
    if "available tools" in lower and "not found" in lower:
        return "MCP_METHOD_UNKNOWN"
    if "mcp_request_schema_invalid" in lower or ("mcp.call request" in lower and "invalid" in lower):
        return "MCP_REQUEST_SCHEMA_INVALID"
    if "expr_type_mismatch" in lower or ("resolves to" in lower and "contract requires" in lower):
        return ErrorCodes.EXPR_TYPE_MISMATCH
    if "mcp_server_not_found" in lower or ("mcp server" in lower and "not found" in lower):
        return ErrorCodes.MCP_SERVER_NOT_FOUND
    if "missing required field 'workflows'" in lower:
        return "MISSING_ROOT_KEY_WORKFLOWS"
    if "missing required field 'version'" in lower:
        return "MISSING_ROOT_KEY_VERSION"
    if "missing required field 'name'" in lower:
        return "MISSING_ROOT_KEY_NAME"
    if "skill_required" in lower or "top-level 'skill'" in lower:
        return "MISSING_ROOT_KEY_SKILL"
    if "step_type_unknown" in lower:
        return "UNKNOWN_STEP_TYPE"
    if "missing_steps" in lower or "missing_branches" in lower or "missing_cases" in lower:
        return "INVALID_CONTAINER_SHAPE"
    if "step_reference_not_available" in lower or "step_reference_unknown" in lower or "semantic_mapping_error" in lower:
        return "SEMANTIC_MAPPING_ERROR"
    if "opaque_response_deep_access" in lower:
        return "OPAQUE_RESPONSE_DEEP_ACCESS"
    if "step_output_property_unknown" in lower:
        return "STEP_OUTPUT_PROPERTY_UNKNOWN"
    if "yaml" in lower:
        return "YAML_PARSE_ERROR"
    if "not allowed by policy" in lower or "denied by policy" in lower:
        return "POLICY_ERROR"
    if "exceeds limit" in lower:
        return "LIMIT_ERROR"
    return exception_code or "VALIDATION_ERROR"


def _build_plan_details(
    phase: str,
    diagnostics: list[dict[str, Any]],
    summary: str,
    llm_guidance: list[str],
) -> dict[str, Any]:
    return {
        "ok": False,
        "phase": phase,
        "summary": _diagnostic_summary(diagnostics, summary),
        "diagnostics": diagnostics,
        "llm_guidance": llm_guidance,
    }


def _diagnostic_summary(diagnostics: list[dict[str, Any]], fallback: str) -> str:
    if not diagnostics:
        return fallback
    codes: list[str] = []
    for diagnostic in diagnostics:
        code = str(diagnostic.get("code") or "")
        if code and code not in codes:
            codes.append(code)
        if len(codes) >= 8:
            break
    return f"{len(diagnostics)} diagnostic(s): {', '.join(codes)}"


def _add_diagnostic(
    diagnostics: list[dict[str, Any]],
    seen: set[tuple[Any, ...]],
    diagnostic: dict[str, Any],
) -> None:
    key = (
        diagnostic.get("code"),
        diagnostic.get("workflow"),
        diagnostic.get("step"),
        diagnostic.get("field"),
        diagnostic.get("message"),
    )
    if key not in seen:
        seen.add(key)
        diagnostics.append(diagnostic)


def _build_validation_diagnostic(error: ValidationError, phase: str) -> dict[str, Any]:
    hint = _validation_hint(error)
    diagnostic: dict[str, Any] = {
        "code": error.code,
        "phase": phase,
        "message": error.message,
        "location": _location(error.workflow_name, error.step_id, error.field),
        "hint": hint,
        "llm_guidance": _validation_llm_guidance(error, hint),
    }
    if error.workflow_name:
        diagnostic["workflow"] = error.workflow_name
    if error.step_id:
        diagnostic["step"] = error.step_id
    if error.field:
        diagnostic["field"] = error.field
    expected = _validation_expected(error)
    if expected:
        diagnostic["expected"] = expected
    return diagnostic


def _build_semantic_diagnostic(error: WorkflowSemanticValidationError) -> dict[str, Any]:
    hint = error.suggestion or _semantic_hint(error)
    diagnostic: dict[str, Any] = {
        "code": error.code,
        "phase": "semantic_validation",
        "message": error.message,
        "location": _location(error.workflow_name, error.step_id, error.field),
        "field": error.field,
        "invalid_path": error.invalid_path,
        "allowed_paths": list(error.allowed_paths or []),
        "hint": hint,
        "llm_guidance": _semantic_llm_guidance(error, hint),
    }
    if error.workflow_name:
        diagnostic["workflow"] = error.workflow_name
    if error.step_id:
        diagnostic["step"] = error.step_id
    expected = _semantic_expected(error)
    if expected:
        diagnostic["expected"] = expected
    return diagnostic


def _build_dry_run_diagnostic(
    code: str,
    message: str,
    failure_kind: str,
    exception: Exception | None,
    runtime_details: Any,
) -> dict[str, Any]:
    diagnostic_code = infer_plan_error_code(message, code)
    hint = _dry_run_hint(diagnostic_code, message)
    diagnostic: dict[str, Any] = {
        "code": diagnostic_code,
        "runtime_error_code": code,
        "phase": "dry_run",
        "failure_kind": failure_kind,
        "message": message,
        "location": _extract_location_from_runtime_details(runtime_details) or "dry_run",
        "hint": hint,
        "llm_guidance": _dry_run_llm_guidance(diagnostic_code, hint),
    }
    if runtime_details is not None:
        diagnostic["runtime_details"] = copy.deepcopy(runtime_details)
    if exception is not None:
        diagnostic["exception_type"] = type(exception).__name__
    return diagnostic


def _build_exception_diagnostic(
    code: str,
    phase: str,
    message: str,
    exception: Exception | None,
) -> dict[str, Any]:
    diagnostic_code = infer_plan_error_code(message, code)
    hint = (
        "Fix YAML syntax and root structure before changing workflow logic."
        if exception is not None and "parse" in type(exception).__name__.lower()
        else "Inspect the message and repair the generated workflow shape or contract that triggered this exception."
    )
    diagnostic: dict[str, Any] = {
        "code": diagnostic_code,
        "phase": phase,
        "message": message,
        "location": phase,
        "hint": hint,
        "llm_guidance": hint,
    }
    if exception is not None:
        diagnostic["exception_type"] = type(exception).__name__
    return diagnostic


def _location(workflow_name: str | None, step_id: str | None, field: str | None) -> str:
    parts: list[str] = []
    if workflow_name:
        parts.append(f"workflow:{workflow_name}")
    if step_id:
        parts.append(f"step:{step_id}")
    if field:
        parts.append(f"field:{field}")
    return "/".join(parts) if parts else "$"


def _validation_hint(error: ValidationError) -> str:
    if error.message.lower().startswith("unknown yaml field"):
        return (
            "Remove the unknown YAML key, or move it to the documented location. Common fields stay at step level; "
            "executor-specific arguments go under step.input."
        )

    hints = {
        ErrorCodes.SKILL_REQUIRED: (
            "Add a top-level `skill` block with description, tags, inputs, and outputs so routers and LLM repair can "
            "understand the workflow contract."
        ),
        ErrorCodes.STEP_TYPE_UNKNOWN: "Replace the step type with one exact registered step type from the available DSL snippets.",
        ErrorCodes.EXPR_PARSE: "Fix the expression syntax inside `${...}`; validate function names, parentheses, and quoted string literals.",
        ErrorCodes.INPUT_VALIDATION: "Align this field with the step input contract and preserve JSON/YAML scalar types exactly.",
        ErrorCodes.LLM_SCHEMA: "Use a valid structured_output schema. Prefer `schema_inline` with standard JSON Schema object fields.",
        ErrorCodes.WORKFLOW_CYCLE_DETECTED: "Break local workflow.call cycles so the call graph is acyclic.",
        "DUPLICATE_STEP_ID": "Rename one step id. Step ids must be unique within the workflow, including nested branches and cases.",
        "DSL_VERSION": "Use workflow DSL version 1.",
        "NO_WORKFLOWS": "Add a non-empty top-level `workflows` object.",
        "EMPTY_STEPS": "Add at least one executable step to this workflow, or remove the empty workflow.",
        "MISSING_STEPS": "For sequence and loop steps, put child steps in a step-level `steps:` array.",
        "MISSING_BRANCHES": "For parallel steps, add step-level `branches:` entries, each with its own `steps:` array.",
        "MISSING_CASES": "For switch steps, add step-level `cases:` entries with `when:` and `steps:`.",
        "INVALID_ENTRYPOINT": "Set `entrypoint` to an existing workflow name, or remove it so `main` can be used.",
        "INVALID_EXPORT": "Export only workflow names that exist in the document.",
        "INVALID_WORKFLOW_REF": "Point workflow.call ref.name to a local workflow that exists, or add the missing workflow.",
    }
    return hints.get(error.code, "Fix the indicated workflow field and rerun validation.")


def _validation_expected(error: ValidationError) -> str | None:
    expected = {
        ErrorCodes.SKILL_REQUIRED: "top-level skill object",
        ErrorCodes.STEP_TYPE_UNKNOWN: "registered step type",
        ErrorCodes.INPUT_VALIDATION: "value matching the step input contract",
        ErrorCodes.LLM_SCHEMA: "valid JSON Schema structured_output",
        "DUPLICATE_STEP_ID": "unique step id",
        "DSL_VERSION": "version: 1",
        "NO_WORKFLOWS": "non-empty workflows mapping",
        "EMPTY_STEPS": "non-empty steps array",
        "MISSING_STEPS": "non-empty steps array",
        "MISSING_BRANCHES": "non-empty branches array",
        "MISSING_CASES": "non-empty cases array",
        "INVALID_ENTRYPOINT": "existing workflow name",
        "INVALID_EXPORT": "existing workflow name",
        "INVALID_WORKFLOW_REF": "existing workflow name",
    }
    return expected.get(error.code)


def _validation_llm_guidance(error: ValidationError, hint: str) -> str:
    if error.step_id:
        return f"Repair step '{error.step_id}' first. {hint}"
    if error.workflow_name:
        return f"Repair workflow '{error.workflow_name}' first. {hint}"
    return hint


def _semantic_hint(error: WorkflowSemanticValidationError) -> str:
    hints = {
        "STEP_REFERENCE_NOT_AVAILABLE": (
            "Move the producing step earlier, move the consuming reference later, or create a guaranteed normalization step "
            "before reading it."
        ),
        "STEP_REFERENCE_UNKNOWN": "Reference an existing previous step id, or add the missing producing step before this expression.",
        "OPAQUE_RESPONSE_DEEP_ACCESS": (
            "Do not invent fields under an opaque response. Pass the whole response or normalize it with llm.call "
            "structured_output."
        ),
        "STEP_OUTPUT_PROPERTY_UNKNOWN": "Use one of the allowed output paths or add a normalizer step that produces the desired property.",
        "MCP_REQUEST_SCHEMA_INVALID": "Align input.request with the discovered MCP tool input schema.",
        "MCP_CALL_INPUT_FIELD_UNKNOWN": "Move MCP tool arguments under input.request; keep only mcp.call envelope fields at input top level.",
        "MCP_METHOD_UNKNOWN": "Use one exact MCP tool name from the discovered server catalog.",
        "MCP_SERVER_UNKNOWN": "Use one exact MCP server name from discovery.",
        "FUNCTION_JSDOC_MISSING": "Add a JSDoc block immediately before the custom function declaration.",
        "FUNCTION_JSDOC_PARAM_MISSING": "Add one typed `@param {type} name` tag for every custom function parameter.",
        "FUNCTION_JSDOC_RETURNS_MISSING": "Add a typed `@returns {type}` tag for the custom function return value.",
        ErrorCodes.EXPR_TYPE_MISMATCH: "Use an expression whose resolved type matches the destination contract.",
    }
    return hints.get(error.code, "Repair the generated workflow so the semantic contract can be proven statically.")


def _semantic_expected(error: WorkflowSemanticValidationError) -> str | None:
    expected = {
        "STEP_REFERENCE_NOT_AVAILABLE": "previously executed step output",
        "STEP_REFERENCE_UNKNOWN": "previously executed step output",
        "OPAQUE_RESPONSE_DEEP_ACCESS": "documented output path",
        "STEP_OUTPUT_PROPERTY_UNKNOWN": "documented output path",
        "MCP_REQUEST_SCHEMA_INVALID": "request matching MCP input_schema",
        "MCP_CALL_INPUT_FIELD_UNKNOWN": "supported mcp.call input envelope",
        "MCP_METHOD_UNKNOWN": "discovered MCP method",
        "MCP_SERVER_UNKNOWN": "discovered MCP server",
        "FUNCTION_JSDOC_MISSING": "JSDoc immediately preceding the function declaration",
        "FUNCTION_JSDOC_PARAM_MISSING": "typed @param tag for each function parameter",
        "FUNCTION_JSDOC_RETURNS_MISSING": "typed @returns tag for the function return value",
        ErrorCodes.EXPR_TYPE_MISMATCH: "expression result matching expected type",
    }
    return expected.get(error.code)


def _semantic_llm_guidance(error: WorkflowSemanticValidationError, hint: str) -> str:
    if error.allowed_paths:
        return f"{hint} Prefer one of allowed_paths when it satisfies the task."
    return hint


def _dry_run_hint(code: str, message: str) -> str:
    if code == ErrorCodes.EXPR_TYPE_MISMATCH or "requires" in message.lower():
        return "Change the expression or declared contract so the runtime value type matches the expected type."
    if code in {"MCP_REQUEST_SCHEMA_INVALID", "MCP_METHOD_UNKNOWN", "MCP_SERVER_UNKNOWN"}:
        return "Fix the mcp.call server, method, and input.request against the discovered MCP catalog."
    if "data.steps." in message:
        return "Fix the step reference so it points to an earlier guaranteed step output with the documented shape."
    return "Replay the generated workflow mentally with dry-run sample inputs and fix the first expression, input, or step dependency that cannot execute."


def _dry_run_llm_guidance(code: str, hint: str) -> str:
    if code == ErrorCodes.EXPR_TYPE_MISMATCH:
        return hint + " For numeric fields, use numeric workflow inputs or structured JSON fields, not free-form LLM text."
    if code == "MCP_REQUEST_SCHEMA_INVALID":
        return hint + " Preserve exact scalar types: integers/numbers/booleans must not be quoted strings."
    return hint


def _extract_location_from_runtime_details(runtime_details: Any) -> str | None:
    if not isinstance(runtime_details, dict):
        return None
    for key in ("field", "invalid_path", "method", "server"):
        value = runtime_details.get(key)
        if isinstance(value, str) and value.strip():
            return value
    return None


def _clone_prompt_safe_details(details: Any) -> Any:
    if details is None:
        return None
    if not isinstance(details, dict):
        return copy.deepcopy(details)
    clone = {
        key: copy.deepcopy(value)
        for key, value in details.items()
        if key not in {"generated_yaml", "invalid_yaml", "yaml"}
    }
    return clone or None
