from __future__ import annotations

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import ErrorCodes
from gnougo_flow_core.integrations import InMemoryMcpClientFactory, MockMcpServerConfig
from gnougo_flow_core.models import LLMResponse, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def _generated(steps: str) -> str:
    return f"""
version: 1
name: generated
skill:
  description: Generated parity workflow.
  tags: [generated]
  inputs: {{}}
  outputs: {{}}
workflows:
  main:
    steps:
{steps}
"""


def _plan_workflow(preflight: str, *, instruction: str = "Perform the requested operation.", attempts: int = 1):
    return WorkflowCompiler().compile(
        WorkflowParser.parse(
            f"""
            version: 1
            workflows:
              main:
                steps:
                  - id: plan
                    type: workflow.plan
                    input:
                      mode: basic
{preflight}
                      generator:
                        model: fake
                        prefilter: false
                        instruction: {instruction}
                      validate:
                        compile: true
                      on_invalid:
                        action: reprompt
                        max_attempts: {attempts}
            """
        )
    ).workflows["main"]


class _PlanLlm:
    def __init__(self, generated_yaml: str, inventory=None, matches=None) -> None:
        self.generated_yaml = generated_yaml
        self.inventory = inventory
        self.matches = matches
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        if "domain-neutral workflow runtime analyst" in request.prompt:
            return LLMResponse(json=self.inventory)
        if "domain-neutral capability matcher" in request.prompt:
            return LLMResponse(json=self.matches)
        if isinstance(request.structured_output_schema, dict) and "yaml" in request.structured_output_schema.get("properties", {}):
            return LLMResponse(json=_generation_envelope(request, self.generated_yaml))
        return LLMResponse(text=self.generated_yaml)


class _RepairingPreflightLlm:
    def __init__(self, generated_yaml: str, inventory: dict, matches: dict) -> None:
        self.generated_yaml = generated_yaml
        self.inventory_responses = [{"complete": False}, inventory]
        self.match_responses = [{"operation_matches": []}, matches]
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        if "domain-neutral workflow runtime analyst" in request.prompt:
            return LLMResponse(json=self.inventory_responses.pop(0))
        if "domain-neutral capability matcher" in request.prompt:
            return LLMResponse(json=self.match_responses.pop(0))
        if isinstance(request.structured_output_schema, dict) and "yaml" in request.structured_output_schema.get("properties", {}):
            return LLMResponse(json=_generation_envelope(request, self.generated_yaml))
        return LLMResponse(text=self.generated_yaml)


def _generation_envelope(request, yaml_text: str) -> dict:
    properties = request.structured_output_schema["properties"]

    def enum_value(name: str) -> str:
        return properties[name]["enum"][0]

    return {
        "schema_version": enum_value("schema_version"),
        "contract_fingerprint": enum_value("contract_fingerprint"),
        "base_candidate_fingerprint": enum_value("base_candidate_fingerprint"),
        "diagnostic_fingerprint": enum_value("diagnostic_fingerprint"),
        "addressed_diagnostic_codes": [],
        "yaml": yaml_text,
    }


class _StatusFailure(Exception):
    def __init__(self, status_code: int) -> None:
        super().__init__("provider rejected capability inventory")
        self.status_code = status_code


class _FailingPreflightLlm:
    def __init__(self, status_code: int) -> None:
        self.failure = _StatusFailure(status_code)
        self.requests = []

    async def call_async(self, request):
        self.requests.append(request)
        raise self.failure


def _assert_openai_strict_schema(schema: object, path: str = "$") -> None:
    if isinstance(schema, list):
        for index, item in enumerate(schema):
            _assert_openai_strict_schema(item, f"{path}[{index}]")
        return
    if not isinstance(schema, dict):
        return
    if schema.get("type") == "object" and isinstance(schema.get("properties"), dict):
        properties = set(schema["properties"])
        required = set(schema.get("required", []))
        assert required == properties, f"{path}: required={sorted(required)} properties={sorted(properties)}"
        assert schema.get("additionalProperties") is False, f"{path}: additionalProperties must be false"
    for key, value in schema.items():
        _assert_openai_strict_schema(value, f"{path}.{key}")


def _inventory_factory(*tools: McpToolInfo) -> InMemoryMcpClientFactory:
    factory = InMemoryMcpClientFactory()
    factory.register_server("inventory", MockMcpServerConfig(tools=list(tools)))
    return factory


@pytest.mark.asyncio
async def test_preflight_off_is_compatible_and_explicit_unavailable_fails_before_generation() -> None:
    off_llm = _PlanLlm(_generated("      - id: done\n        type: set\n        input: {value: ok}"))
    off_engine = WorkflowEngine()
    off_engine.llm_client = off_llm
    off_result = await off_engine.execute_async(_plan_workflow(""), {})

    assert off_result.success
    assert off_result.outputs["plan"]["meta"]["capability_preflight"]["mode"] == "off"

    explicit = """
                      capability_preflight:
                        mode: explicit
                        requirements:
                          - id: missing
                            description: Missing operation.
                            required: true
                            alternatives:
                              - {server: inventory, kind: tool, method: absent}
    """
    unavailable_llm = _PlanLlm(_generated("      - id: done\n        type: set\n        input: {value: ok}"))
    unavailable_engine = WorkflowEngine()
    unavailable_engine.llm_client = unavailable_llm
    unavailable_engine.mcp_client_factory = _inventory_factory(McpToolInfo(name="read"))

    unavailable = await unavailable_engine.execute_async(_plan_workflow(explicit), {})

    assert not unavailable.success
    assert unavailable.error is not None and unavailable.error.code == "CAPABILITY_PREFLIGHT_UNAVAILABLE"
    assert unavailable_llm.requests == []


@pytest.mark.asyncio
async def test_explicit_selector_binding_and_selector_aware_denial_lock_exact_call() -> None:
    tool = McpToolInfo(
        name="inventory_read",
        input_schema={
            "type": "object",
            "properties": {"method": {"type": "string", "enum": ["get_status", "list_items"]}},
            "required": ["method"],
        },
    )
    preflight = """
                      capability_preflight:
                        mode: explicit
                        requirements:
                          - id: get_status
                            description: Read inventory status.
                            required: true
                            alternatives:
                              - server: inventory
                                kind: tool
                                method: inventory_read
                                request_bindings:
                                  - {path: /method, value: get_status}
                        constraints:
                          - id: never_list
                            description: Never list items.
                            required: true
                            denied_alternatives:
                              - server: inventory
                                kind: tool
                                method: inventory_read
                                request_bindings:
                                  - {path: /method, value: list_items}
    """
    yaml_text = _generated(
        """      - id: status
        type: mcp.call
        input:
          server: inventory
          kind: tool
          method: inventory_read
          request: {method: get_status}"""
    )
    engine = WorkflowEngine()
    engine.llm_client = _PlanLlm(yaml_text)
    engine.mcp_client_factory = _inventory_factory(tool)

    result = await engine.execute_async(_plan_workflow(preflight), {})

    assert result.success, result.error
    metadata = result.outputs["plan"]["meta"]["capability_preflight"]
    assert metadata["mode"] == "explicit"
    assert metadata["requirements"][0]["match_status"] == "matched"
    assert metadata["requirements"][0]["catalog_ids"]


@pytest.mark.asyncio
async def test_locked_capabilities_are_a_multiset_and_optional_unavailable_is_reported() -> None:
    repeated = """
                      capability_preflight:
                        mode: explicit
                        requirements:
                          - id: first_read
                            description: First read.
                            alternatives: [{server: inventory, kind: tool, method: read}]
                          - id: second_read
                            description: Second read.
                            alternatives: [{server: inventory, kind: tool, method: read}]
                          - id: optional_notify
                            description: Optional notification.
                            required: false
                            alternatives: [{server: inventory, kind: tool, method: notify}]
    """
    yaml_text = _generated(
        """      - id: read_once
        type: mcp.call
        input: {server: inventory, kind: tool, method: read, request: {}}"""
    )
    engine = WorkflowEngine()
    engine.llm_client = _PlanLlm(yaml_text)
    engine.mcp_client_factory = _inventory_factory(McpToolInfo(name="read", input_schema={"type": "object"}))

    result = await engine.execute_async(_plan_workflow(repeated), {})

    assert not result.success
    assert result.error is not None and result.error.code == "CAPABILITY_PREFLIGHT_UNAVAILABLE"
    assert "second_read" in result.error.message


@pytest.mark.asyncio
async def test_inferred_external_write_requires_human_confirmation_before_call() -> None:
    inventory = {
        "complete": True,
        "incomplete_reasons": [],
        "operations": [
            {
                "id": "write_record",
                "description": "Write an inventory record.",
                "required": True,
                "execution_kind": "external_effect",
                "external_effect_kind": "write",
            }
        ],
        "constraints": [],
    }
    matches = {
        "operation_matches": [
            {"operation_id": "write_record", "status": "matched", "catalog_ids": ["cap_000001"]},
            {"operation_id": "platform_confirm_external_write", "status": "local", "catalog_ids": []},
        ],
        "constraint_matches": [],
    }
    preflight = """
                      capability_preflight:
                        mode: infer
    """
    yaml_text = _generated(
        """      - id: approve
        type: human.input
        input:
          mode: confirm
          prompt: Write the record?
          choices: [approve, reject]
      - id: write
        type: mcp.call
        input: {server: inventory, kind: tool, method: write, request: {value: 1}}"""
    )
    llm = _PlanLlm(yaml_text, inventory, matches)
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = _inventory_factory(McpToolInfo(name="write"))

    result = await engine.execute_async(
        _plan_workflow(preflight, instruction="Write a record to inventory."), {}
    )

    assert result.success, result.error
    metadata = result.outputs["plan"]["meta"]["capability_preflight"]
    assert {item["id"] for item in metadata["requirements"]} == {
        "write_record",
        "platform_confirm_external_write",
    }
    assert len(llm.requests) == 3


@pytest.mark.asyncio
async def test_inferred_write_without_confirmation_fails_closed() -> None:
    inventory = {
        "complete": True,
        "incomplete_reasons": [],
        "operations": [
            {
                "id": "write_record",
                "description": "Write an inventory record.",
                "required": True,
                "execution_kind": "external_effect",
                "external_effect_kind": "write",
            }
        ],
        "constraints": [],
    }
    matches = {
        "operation_matches": [
            {"operation_id": "write_record", "status": "matched", "catalog_ids": ["cap_000001"]},
            {"operation_id": "platform_confirm_external_write", "status": "local", "catalog_ids": []},
        ],
        "constraint_matches": [],
    }
    llm = _PlanLlm(
        _generated(
            """      - id: write
        type: mcp.call
        input: {server: inventory, kind: tool, method: write, request: {value: 1}}"""
        ),
        inventory,
        matches,
    )
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = _inventory_factory(McpToolInfo(name="write"))

    result = await engine.execute_async(
        _plan_workflow("                      capability_preflight:\n                        mode: infer"), {}
    )

    assert not result.success
    assert result.error is not None and result.error.code == "CAPABILITY_PREFLIGHT_UNAVAILABLE"
    assert "platform_confirm_external_write" in result.error.message


@pytest.mark.asyncio
async def test_inferred_preflight_repairs_inventory_and_candidates_at_most_once_each() -> None:
    inventory = {
        "complete": True,
        "incomplete_reasons": [],
        "operations": [
            {
                "id": "shape_result",
                "description": "Shape the result locally.",
                "required": True,
                "execution_kind": "local_processing",
                "external_effect_kind": "none",
            }
        ],
        "constraints": [],
    }
    matches = {
        "operation_matches": [
            {"operation_id": "shape_result", "status": "local", "catalog_ids": []}
        ],
        "constraint_matches": [],
    }
    llm = _RepairingPreflightLlm(
        _generated("      - id: shape\n        type: set\n        input: {value: ok}"),
        inventory,
        matches,
    )
    engine = WorkflowEngine()
    engine.llm_client = llm

    result = await engine.execute_async(
        _plan_workflow("                      capability_preflight:\n                        mode: infer"), {}
    )

    assert result.success, result.error
    assert len(llm.requests) == 5
    assert sum("previous structured response was invalid" in request.prompt for request in llm.requests) == 2
    assert all(request.use_background_mode is True for request in llm.requests)
    inference_requests = [request for request in llm.requests if request.structured_output_schema is not None]
    assert len(inference_requests) == 5
    assert all(request.structured_output_strict is True for request in inference_requests)
    for request in inference_requests:
        _assert_openai_strict_schema(request.structured_output_schema)


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("status", "expected_code", "retryable"),
    [
        (401, ErrorCodes.LLM_PROVIDER, False),
        (429, ErrorCodes.LLM_NETWORK, True),
        (503, ErrorCodes.LLM_NETWORK, True),
    ],
)
async def test_inferred_preflight_classifies_provider_failure_and_preserves_phase(
    status: int,
    expected_code: str,
    retryable: bool,
) -> None:
    llm = _FailingPreflightLlm(status)
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = _inventory_factory(McpToolInfo(name="read"))

    result = await engine.execute_async(
        _plan_workflow("                      capability_preflight:\n                        mode: infer"), {}
    )

    assert result.success is False
    assert result.error is not None
    assert result.error.code == expected_code
    assert result.error.retryable is retryable
    assert result.error.details["phase"] == "capability_inference"
    assert result.error.details["inference_phase"] == "capability_inventory_call"
    assert len(llm.requests) == 1
    assert llm.requests[0].use_background_mode is True


@pytest.mark.asyncio
async def test_selector_safety_limit_fails_before_inventory_generation() -> None:
    values = [f"action_{index:02d}" for index in range(65)]
    tool = McpToolInfo(
        name="action",
        input_schema={
            "type": "object",
            "properties": {"action": {"type": "string", "enum": values}},
        },
    )
    llm = _PlanLlm(_generated("      - id: done\n        type: set\n        input: {value: ok}"))
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = _inventory_factory(tool)

    result = await engine.execute_async(
        _plan_workflow("                      capability_preflight:\n                        mode: infer"), {}
    )

    assert not result.success
    assert result.error is not None and result.error.code == "CAPABILITY_PREFLIGHT_INFERENCE_FAILED"
    assert llm.requests == []


@pytest.mark.asyncio
async def test_artifact_provenance_accepts_direct_producer_to_consumer_expression() -> None:
    producer = McpToolInfo(
        name="materialize",
        outputSchema={
            "type": "object",
            "properties": {"projectRoot": {"type": "string"}},
            "required": ["projectRoot"],
        },
        meta={
            "gnougo": {
                "artifacts": {
                    "produces": [{"kind": "workspace", "pointer": "/projectRoot"}],
                }
            }
        },
    )
    consumer = McpToolInfo(
        name="inspect",
        meta={
            "gnougo": {
                "artifacts": {
                    "consumes": [{"kind": "workspace", "pointer": "/projectRoot", "required": True}],
                }
            }
        },
    )
    preflight = """
                      capability_preflight:
                        mode: explicit
                        requirements:
                          - id: materialize
                            description: Materialize workspace.
                            alternatives: [{server: inventory, kind: tool, method: materialize}]
                          - id: inspect
                            description: Inspect workspace.
                            alternatives: [{server: inventory, kind: tool, method: inspect}]
    """
    yaml_text = _generated(
        """      - id: create
        type: mcp.call
        input: {server: inventory, kind: tool, method: materialize, request: {}}
      - id: inspect
        type: mcp.call
        input:
          server: inventory
          kind: tool
          method: inspect
          request:
            projectRoot: '${data.steps.create.response.projectRoot}'"""
    )
    engine = WorkflowEngine()
    engine.llm_client = _PlanLlm(yaml_text)
    engine.mcp_client_factory = _inventory_factory(producer, consumer)

    result = await engine.execute_async(_plan_workflow(preflight), {})

    assert result.success, result.error


@pytest.mark.asyncio
async def test_repair_stops_after_two_unchanged_diagnostic_fingerprints() -> None:
    preflight = """
                      capability_preflight:
                        mode: explicit
                        requirements:
                          - id: read
                            description: Read inventory.
                            alternatives: [{server: inventory, kind: tool, method: read}]
    """
    invalid = _generated("      - id: done\n        type: set\n        input: {value: no-read}")
    llm = _PlanLlm(invalid)
    engine = WorkflowEngine()
    engine.llm_client = llm
    engine.mcp_client_factory = _inventory_factory(McpToolInfo(name="read"))

    result = await engine.execute_async(_plan_workflow(preflight, attempts=5), {})

    assert not result.success
    assert result.error is not None and result.error.code == "WORKFLOW_PLAN_REPAIR_STALLED"
    assert len(llm.requests) == 3
