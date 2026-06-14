from __future__ import annotations

import copy
import json
from typing import Any

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import ErrorCodes, WorkflowRuntimeException
from gnougo_flow_core.integrations.mcp import InMemoryMcpClientFactory, MockMcpServerConfig
from gnougo_flow_core.models import (
    ExecutionLimits,
    HumanInputFieldDef,
    HumanInputRequest,
    InputDef,
    LLMRequest,
    LLMResponse,
    LlmRuntimeDefaults,
    McpCallResult,
    McpToolInfo,
    WorkflowDocument,
    WorkflowRouteCandidate,
    WorkflowRouteCandidateQuery,
)
from gnougo_flow_core.runtime import WorkflowEngine, apply_workflow_input_defaults
from gnougo_flow_core.workflow_plan_semantic_validator import McpToolOutputContract


async def validate_workflow_plan_dry_run(
    generated_doc: WorkflowDocument,
    mcp_tool_contracts: list[McpToolOutputContract] | None = None,
) -> None:
    try:
        compiled = WorkflowCompiler().compile(generated_doc)
    except Exception as exc:
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"Generated workflow dry_run compilation failed: {exc}",
        ) from exc

    entrypoint = compiled.entrypoint
    workflow = compiled.workflows.get(entrypoint or "") if entrypoint else None
    if workflow is None:
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            "Generated workflow dry_run failed: compiled workflow has no executable entrypoint.",
        )

    engine = WorkflowEngine()
    engine.llm_client = _DryRunLlmClient()
    engine.mcp_client_factory = _build_dry_run_mcp_factory(mcp_tool_contracts or [])
    engine.human_input_provider = _DryRunHumanInputProvider()
    engine.workflow_candidate_provider = _DryRunWorkflowCandidateProvider()
    engine.lm_defaults = LlmRuntimeDefaults(model="dry-run-model")
    engine.limits = ExecutionLimits(
        max_total_steps_executed=250,
        max_loop_iterations=2,
        max_parallel_branches=10,
        max_call_depth=10,
        run_id="workflow-plan-dry-run",
    )

    inputs = apply_workflow_input_defaults(workflow.source, _build_sample_inputs(workflow.source.inputs))
    try:
        result = await engine.execute_async(workflow, inputs)
    except Exception as exc:
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"Generated workflow dry_run failed before execution: {exc}",
        ) from exc

    if result.success:
        return

    code = result.error.code if result.error and result.error.code else "UNKNOWN"
    message = result.error.message if result.error and result.error.message else "No error message returned."
    raise WorkflowRuntimeException(
        ErrorCodes.TEMPLATE_PLAN,
        f"Generated workflow dry_run failed: [{code}] {message}",
    )


def create_sample_from_json_schema(schema: Any) -> Any:
    if not isinstance(schema, dict):
        return "dry-run"

    for keyword in ("anyOf", "oneOf", "allOf"):
        variants = schema.get(keyword)
        if isinstance(variants, list):
            for variant in variants:
                if isinstance(variant, dict) and _read_schema_type(variant) != "null":
                    return create_sample_from_json_schema(variant)

    enum_values = schema.get("enum")
    if isinstance(enum_values, list) and enum_values:
        return copy.deepcopy(enum_values[0])

    schema_type = _read_schema_type(schema)
    if schema_type == "object" or (schema_type is None and isinstance(schema.get("properties"), dict)):
        return {
            str(name): create_sample_from_json_schema(property_schema)
            for name, property_schema in (schema.get("properties") or {}).items()
        }
    if schema_type == "array":
        return [create_sample_from_json_schema(schema.get("items"))]
    if schema_type == "integer":
        return 1
    if schema_type == "number":
        return 1.25
    if schema_type == "boolean":
        return True
    if schema_type == "null":
        return None
    return "dry-run"


def _read_schema_type(schema: dict[str, Any]) -> str | None:
    schema_type = schema.get("type")
    if isinstance(schema_type, str):
        return schema_type
    if isinstance(schema_type, list):
        for item in schema_type:
            if isinstance(item, str) and item != "null":
                return item
    return None


def _build_sample_inputs(inputs: dict[str, InputDef] | None) -> dict[str, Any]:
    if not inputs:
        return {}
    return {name: _create_sample_from_input_def(definition) for name, definition in inputs.items()}


def _create_sample_from_input_def(definition: InputDef | None) -> Any:
    if definition is None:
        return "dry-run"
    if definition.default is not None:
        return copy.deepcopy(definition.default)

    type_name = (definition.type or "any").strip().lower()
    if type_name in {"string", "text", "markdown", "yaml", "json", "url", "email", "date", "file", "directory"}:
        return "dry-run"
    if type_name == "integer":
        return 1
    if type_name == "number":
        return 1.25
    if type_name in {"boolean", "bool"}:
        return True
    if type_name == "array":
        return [_create_sample_from_input_def(definition.items)]
    if type_name == "object":
        return {
            name: _create_sample_from_input_def(property_def)
            for name, property_def in (definition.properties or {}).items()
        }
    if type_name == "dictionary":
        return {"key": _create_sample_from_input_def(definition.additional_properties)}
    return "dry-run"


def _build_dry_run_mcp_factory(contracts: list[McpToolOutputContract]) -> InMemoryMcpClientFactory | None:
    if not contracts:
        return None

    factory = InMemoryMcpClientFactory()
    by_server: dict[str, list[McpToolOutputContract]] = {}
    for contract in contracts:
        by_server.setdefault(contract.server_name, []).append(contract)

    for server_name, server_contracts in by_server.items():
        config = MockMcpServerConfig(
            description="Dry-run MCP server",
            tools=[
                McpToolInfo(
                    name=contract.tool_name,
                    input_schema=copy.deepcopy(contract.input_schema),
                    output_schema=copy.deepcopy(contract.output_schema),
                    example_response=copy.deepcopy(contract.example_response),
                )
                for contract in server_contracts
            ],
        )

        for contract in server_contracts:
            output_schema = copy.deepcopy(contract.output_schema)
            example_response = copy.deepcopy(contract.example_response)

            def handler(_arguments: Any, *, schema: Any = output_schema, example: Any = example_response) -> McpCallResult:
                return McpCallResult(
                    is_error=False,
                    content=copy.deepcopy(example) if example is not None else create_sample_from_json_schema(schema),
                    model="dry-run-mcp",
                    usage={"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
                )

            config.tool_handlers[contract.tool_name] = handler

        factory.register_server(server_name, config)

    return factory


class _DryRunLlmClient:
    async def call_async(self, request: LLMRequest) -> LLMResponse:
        structured_json = (
            create_sample_from_json_schema(request.structured_output_schema)
            if request.structured_output_schema is not None
            else None
        )
        return LLMResponse(
            text=json.dumps(structured_json, ensure_ascii=False) if structured_json is not None else "dry-run text response",
            json=copy.deepcopy(structured_json),
            usage={"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
        )


class _DryRunHumanInputProvider:
    async def request_input_async(self, request: HumanInputRequest) -> Any:
        if request.fields:
            return {field.name: _create_sample_from_human_field(field) for field in request.fields}
        if request.mode.lower() == "confirm":
            return True
        if request.choices:
            return request.choices[0]
        return "dry-run human response"


def _create_sample_from_human_field(field: HumanInputFieldDef) -> Any:
    if field.default:
        return field.default
    field_type = field.type.strip().lower()
    if field.options and field_type in {"select", "radio", "multiselect", "checkbox"}:
        if field_type in {"multiselect", "checkbox"}:
            return [field.options[0]]
        return field.options[0]
    if field_type == "number":
        return 1.25
    if field_type == "integer":
        return 1
    if field_type == "boolean":
        return True
    if field_type == "json":
        return {"value": "dry-run"}
    return "dry-run"


class _DryRunWorkflowCandidateProvider:
    async def get_candidates_async(self, query: WorkflowRouteCandidateQuery) -> list[WorkflowRouteCandidate]:
        return []
