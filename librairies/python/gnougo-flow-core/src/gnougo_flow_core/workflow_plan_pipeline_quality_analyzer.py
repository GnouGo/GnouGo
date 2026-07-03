from __future__ import annotations

import copy
import re
from dataclasses import dataclass
from typing import Any

from .errors import ErrorCodes, WorkflowRuntimeException
from .models import StepDef, WorkflowDocument
from .workflow_plan_diagnostics import to_prompt_json

MAIN_DATAFLOW_PHASE = "pipeline_main_dataflow_validation"
UNPROVEN_EXTERNAL_ARTIFACT_CODE = "PIPELINE_MAIN_UNPROVEN_EXTERNAL_ARTIFACT"
UNPROVEN_EXTERNAL_ARTIFACT_ROOT_CAUSE = "unproven_external_artifact"


def analyze_external_artifact_readiness(document: WorkflowDocument) -> list[dict[str, Any]]:
    diagnostics: list[dict[str, Any]] = []
    main = document.workflows.get("main")
    if main is None:
        return diagnostics

    steps = list(_enumerate_steps(main.steps))
    steps_by_id = {step.id: step for step in steps if step.id}
    for step in steps:
        if not _is_external_artifact_consumer_step_type(step.type):
            continue
        for field, text in _enumerate_string_values(step.input, "input"):
            if (
                not _is_artifact_locator_field(field)
                or not text.strip()
                or _is_proven_artifact_source(text, steps_by_id, set())[0]
            ):
                continue
            provenance = _build_artifact_provenance(text, steps_by_id)
            diagnostics.append(_build_unproven_external_artifact_diagnostic(step, field, text, provenance))
    return diagnostics


def validate_external_artifact_readiness(document: WorkflowDocument) -> None:
    diagnostics = analyze_external_artifact_readiness(document)
    if not diagnostics:
        return
    details = build_main_dataflow_quality_details(diagnostics)
    raise WorkflowRuntimeException(
        ErrorCodes.TEMPLATE_PLAN,
        "Pipeline main workflow dataflow quality validation failed. | repair diagnostics: " + to_prompt_json(details),
        details=details,
    )


def build_main_dataflow_quality_details(diagnostics: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "ok": False,
        "phase": MAIN_DATAFLOW_PHASE,
        "summary": f"{len(diagnostics)} pipeline main dataflow diagnostic(s)",
        "diagnostics": copy.deepcopy(diagnostics),
        "root_causes": build_root_causes(diagnostics),
        "llm_guidance": build_main_dataflow_guidance(),
    }


def build_root_causes(diagnostics: list[dict[str, Any]]) -> list[dict[str, Any]]:
    root_causes: list[dict[str, Any]] = []
    seen: set[tuple[str, str | None, str | None, str | None]] = set()
    for diagnostic in diagnostics:
        if diagnostic.get("code") != UNPROVEN_EXTERNAL_ARTIFACT_CODE:
            continue
        consumer_step = diagnostic.get("consumer_step") or diagnostic.get("step")
        field = diagnostic.get("field")
        key = (UNPROVEN_EXTERNAL_ARTIFACT_ROOT_CAUSE, consumer_step, field, diagnostic.get("code"))
        if key in seen:
            continue
        seen.add(key)
        root_causes.append(
            {
                "category": UNPROVEN_EXTERNAL_ARTIFACT_ROOT_CAUSE,
                "phase": MAIN_DATAFLOW_PHASE,
                "consumer_step": consumer_step,
                "consumer_field": field,
                "invalid_path": field,
                "code": diagnostic.get("code"),
                "message": diagnostic.get("message")
                or "Main synthesized an operational artifact locator before passing it to external work.",
                "primary": True,
            }
        )
    return root_causes


def build_main_dataflow_guidance() -> list[str]:
    return [
        "Reprompt only main assembly when the diagnostic is about main dataflow wiring.",
        "Do not synthesize operational artifact locators such as project/workspace/root/path/directory/file values in main before external work uses them.",
        "Use caller-provided workflow inputs for pre-existing artifacts, or pass a typed output from an upstream "
        "external-producing leaf/action that proves the artifact exists.",
    ]


@dataclass(slots=True)
class _ArtifactProvenance:
    source_kind: str
    producer_step_id: str | None = None
    producer_step_type: str | None = None
    producer_field: str | None = None


def _build_unproven_external_artifact_diagnostic(
    consumer: StepDef,
    field: str,
    expression: str,
    provenance: _ArtifactProvenance,
) -> dict[str, Any]:
    diagnostic = {
        "code": UNPROVEN_EXTERNAL_ARTIFACT_CODE,
        "phase": MAIN_DATAFLOW_PHASE,
        "workflow": "main",
        "step": consumer.id,
        "consumer_step": consumer.id,
        "consumer_type": consumer.type,
        "field": field,
        "request_field": field,
        "invalid_assignment": expression,
        "source_kind": provenance.source_kind,
        "message": f"External step '{consumer.id}' receives artifact-like field '{field}' from main-synthesized value '{expression}'.",
        "expected": (
            "Pass a caller-provided workflow input, or pass a typed output from an upstream external-producing leaf/action "
            "that proves the artifact exists."
        ),
        "hint": "Main may shape simple scalar values, but it should not invent operational artifact locators for external consumers.",
    }
    if provenance.producer_step_id:
        diagnostic["producer_step"] = provenance.producer_step_id
    if provenance.producer_step_type:
        diagnostic["producer_type"] = provenance.producer_step_type
    if provenance.producer_field:
        diagnostic["producer_field"] = provenance.producer_field
    return diagnostic


def _build_artifact_provenance(text: str, steps_by_id: dict[str, StepDef]) -> _ArtifactProvenance:
    parsed_step = _try_parse_exact_step_path_expression(text)
    if parsed_step is not None:
        step_id, path = parsed_step
        producer = steps_by_id.get(step_id)
        if producer is not None:
            return _ArtifactProvenance(
                "main_set" if producer.type == "set" else "main_support_step",
                step_id,
                producer.type,
                ".".join(path) if path else None,
            )
        return _ArtifactProvenance("unknown_step", step_id, None, ".".join(path) if path else None)
    return _ArtifactProvenance("main_template" if "${" in text else "main_literal")


def _is_proven_artifact_source(
    text: str,
    steps_by_id: dict[str, StepDef],
    visited_set_paths: set[str],
) -> tuple[bool, str | None]:
    input_name = _try_parse_exact_data_input_expression(text)
    if input_name:
        return True, f"workflow input `{input_name}`"

    parsed_step = _try_parse_exact_step_path_expression(text)
    if parsed_step is None:
        return False, None
    step_id, path = parsed_step
    producer = steps_by_id.get(step_id)
    if producer is None:
        return False, None
    if _is_external_artifact_producer_step_type(producer.type):
        return True, f"external/action step `{step_id}`"
    if producer.type != "set":
        return False, None

    set_path = step_id + "." + ".".join(path)
    if set_path in visited_set_paths:
        return False, None
    visited_set_paths.add(set_path)
    producer_value = _try_get_path(producer.input, path)
    if not isinstance(producer_value, str):
        return False, None
    return _is_proven_artifact_source(producer_value, steps_by_id, visited_set_paths)


def _is_external_artifact_consumer_step_type(step_type: str) -> bool:
    return step_type in {"mcp.call", "llm.call", "workflow.execute", "workflow.route"}


def _is_external_artifact_producer_step_type(step_type: str) -> bool:
    return step_type in {"mcp.call", "workflow.call", "human.input"}


def _is_artifact_locator_field(field: str) -> bool:
    target = _get_leaf_field_name(field)
    tokens = _tokenize_name(target)
    if not tokens or any(_is_url_like_token(token) for token in tokens):
        return False
    artifact_tokens = {
        "path",
        "paths",
        "root",
        "directory",
        "directories",
        "dir",
        "dirs",
        "folder",
        "folders",
        "workspace",
        "workdir",
        "cwd",
        "file",
        "files",
        "filename",
        "filenames",
    }
    return bool(artifact_tokens & set(tokens)) or ("project" in tokens and "root" in tokens)


def _is_url_like_token(token: str) -> bool:
    return token in {"url", "uri", "link", "href", "endpoint", "host", "domain"}


def _get_leaf_field_name(field: str) -> str:
    trimmed = field.strip()
    if "." in trimmed:
        trimmed = trimmed.rsplit(".", 1)[1]
    if "[" in trimmed:
        trimmed = trimmed.split("[", 1)[0]
    return trimmed


def _tokenize_name(name: str) -> list[str]:
    return [match.group(0).lower() for match in re.finditer(r"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+", name)]


def _enumerate_string_values(node: Any, field: str):
    if isinstance(node, str):
        yield field, node
    elif isinstance(node, dict):
        for name, child in node.items():
            yield from _enumerate_string_values(child, f"{field}.{name}")
    elif isinstance(node, list):
        for index, child in enumerate(node):
            yield from _enumerate_string_values(child, f"{field}[{index}]")


def _enumerate_steps(steps: list[StepDef]):
    for step in steps or []:
        yield step
        yield from _enumerate_steps(step.steps or [])
        for branch in step.branches or []:
            yield from _enumerate_steps(branch.steps)
        for case in step.cases or []:
            yield from _enumerate_steps(case.steps)
        yield from _enumerate_steps(step.default or [])


def _try_get_path(node: Any, path: list[str]) -> Any:
    current = node
    for segment in path:
        if not isinstance(current, dict) or segment not in current:
            return None
        current = current[segment]
    return current


def _try_parse_exact_data_input_expression(expression: str) -> str | None:
    body = _try_extract_exact_expression_body(expression)
    if body is None or not body.startswith("data.inputs."):
        return None
    segments = [segment.strip() for segment in body[len("data.inputs.") :].split(".") if segment.strip()]
    if not segments or any(not _is_identifier_like_path_segment(segment) for segment in segments):
        return None
    return ".".join(segments)


def _try_parse_exact_step_path_expression(expression: str) -> tuple[str, list[str]] | None:
    body = _try_extract_exact_expression_body(expression)
    if body is None or not body.startswith("data.steps."):
        return None
    segments = [segment.strip() for segment in body[len("data.steps.") :].split(".") if segment.strip()]
    if len(segments) < 2 or not _is_identifier_like_path_segment(segments[0]):
        return None
    if any(not _is_identifier_like_path_segment(segment) for segment in segments[1:]):
        return None
    return segments[0], segments[1:]


def _try_extract_exact_expression_body(expression: str) -> str | None:
    trimmed = expression.strip()
    if not trimmed.startswith("${") or not trimmed.endswith("}"):
        return None
    body = trimmed[2:-1].strip()
    if not body or "${" in body:
        return None
    return body


def _is_identifier_like_path_segment(value: str) -> bool:
    if not value:
        return False
    if not (value[0].isalpha() or value[0] == "_"):
        return False
    return all(ch.isalnum() or ch in {"_", "-"} for ch in value[1:])
