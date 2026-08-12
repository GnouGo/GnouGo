from __future__ import annotations

import asyncio

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import WorkflowRuntimeException
from gnougo_flow_core.integrations import (
    ConfiguredMcpClientFactory,
    McpHumanInputSignalPhase,
    McpServerOptions,
)
from gnougo_flow_core.models import McpCallResult, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.runtime_steps.mcp_call_executor import McpCorrelationContext


def _compile(yaml_text: str):
    document = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return document.workflows[document.entrypoint]


class _CaptureClient:
    def __init__(self, *, delay: float = 0.0, cancel_transport: bool = False) -> None:
        self.delay = delay
        self.cancel_transport = cancel_transport
        self.list_tools_calls = 0
        self.calls: list[tuple[str, object, object]] = []
        self.elicitation_handler = None

    async def list_tools_async(self):
        self.list_tools_calls += 1
        await asyncio.sleep(0.01)
        return [
            {
                "name": "write",
                "description": "Write a record",
                "inputSchema": {
                    "type": "object",
                    "properties": {
                        "kind": {"type": "string", "enum": ["file", "url"]},
                        "target": {"type": "string"},
                        "note": {"type": ["string", "null"]},
                    },
                    "required": ["kind"],
                    "if": {"properties": {"kind": {"const": "file"}}},
                    "then": {"required": ["target"]},
                },
                "outputSchema": {"type": "object", "properties": {"ok": {"type": "boolean"}}},
                "_meta": {"gnougo": {"artifacts": {"produces": ["record"]}}},
            }
        ]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, name, arguments, meta=None):
        if self.delay:
            await asyncio.sleep(self.delay)
        if self.cancel_transport:
            raise asyncio.CancelledError()
        self.calls.append((name, arguments, meta))
        return McpCallResult(is_error=False, content={"ok": True})


class _InjectedSession:
    server_name = "demo"

    def __init__(self) -> None:
        self.list_tools_calls = 0
        self.call_tools_calls = 0

    async def list_tools_async(self):
        self.list_tools_calls += 1
        return [McpToolInfo(name="write", input_schema={"type": "object"})]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, name, arguments, meta=None):
        self.call_tools_calls += 1
        return McpCallResult(is_error=False, content={"ok": True})


class _InjectedFactory:
    def __init__(self, session: _InjectedSession) -> None:
        self.session = session

    async def get_client_async(self, server_name: str):
        return self.session


@pytest.mark.asyncio
async def test_injected_factory_is_concurrency_safe_and_preserves_live_tool_metadata() -> None:
    client = _CaptureClient()
    factory = ConfiguredMcpClientFactory({"demo": McpServerOptions(client=client)})

    sessions = await asyncio.gather(*(factory.get_client_async("demo") for _ in range(12)))
    tools = await sessions[0].list_tools_async()

    assert len({id(session) for session in sessions}) == 1
    assert client.list_tools_calls == 1
    assert tools[0].output_schema["properties"]["ok"]["type"] == "boolean"
    assert tools[0].meta == {"gnougo": {"artifacts": {"produces": ["record"]}}}


@pytest.mark.asyncio
async def test_tool_call_forces_one_live_listing_per_injected_session_despite_cached_catalog() -> None:
    session = _InjectedSession()
    engine = WorkflowEngine()
    engine.mcp_client_factory = _InjectedFactory(session)
    engine.mcp_cache.cache_tools("demo", [McpToolInfo(name="stale")])
    workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: invoke
                type: mcp.call
                input: {server: demo, method: write, request: {}}
        """
    )

    first = await engine.execute_async(workflow, {})
    second = await engine.execute_async(workflow, {})

    assert first.success and second.success
    assert session.list_tools_calls == 1
    assert session.call_tools_calls == 2


@pytest.mark.asyncio
async def test_mcp_context_uses_host_metadata_and_omits_only_optional_nulls() -> None:
    client = _CaptureClient()
    engine = WorkflowEngine()
    engine.mcp_client_factory = ConfiguredMcpClientFactory({"demo": McpServerOptions(client=client)})
    engine.limits.tenant_id = "tenant-host"
    engine.limits.execution_id = "execution-host"
    engine.limits.agent_id = "agent-host"
    engine.limits.agent_name = "Agent Host"
    engine.limits.run_id = "run-host"
    workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: invoke
                type: mcp.call
                input:
                  server: demo
                  kind: tool
                  method: write
                  context:
                    domain: billing
                    labels: [{region: eu}]
                  request:
                    kind: file
                    target: report.json
                    note: null
        """
    )

    result = await engine.execute_async(workflow, {})

    assert result.success
    _, arguments, meta = client.calls[0]
    assert arguments == {"kind": "file", "target": "report.json"}
    assert meta["gnougo"]["tenantId"] == "tenant-host"
    assert meta["gnougo"]["executionId"] == "execution-host"
    assert meta["gnougo"]["agentId"] == "agent-host"
    assert meta["gnougo"]["context"] == {"domain": "billing", "labels": [{"region": "eu"}]}
    assert "tenantId" not in meta["gnougo"]["context"]


@pytest.mark.asyncio
@pytest.mark.parametrize("unsafe_key", ["tenant_id", "api_token", "nested_password"])
async def test_mcp_context_rejects_reserved_and_secret_looking_keys_recursively(unsafe_key: str) -> None:
    client = _CaptureClient()
    engine = WorkflowEngine()
    engine.mcp_client_factory = ConfiguredMcpClientFactory({"demo": McpServerOptions(client=client)})
    workflow = _compile(
        f"""
        version: 1
        workflows:
          main:
            steps:
              - id: invoke
                type: mcp.call
                input:
                  server: demo
                  method: write
                  context:
                    nested:
                      {unsafe_key}: forbidden
                  request: {{kind: file, target: report.json}}
        """
    )

    result = await engine.execute_async(workflow, {})

    assert not result.success
    assert result.error is not None and result.error.code == "INPUT_VALIDATION"
    assert client.calls == []


@pytest.mark.asyncio
async def test_conditional_mcp_request_is_validated_after_optional_null_removal() -> None:
    client = _CaptureClient()
    engine = WorkflowEngine()
    engine.mcp_client_factory = ConfiguredMcpClientFactory({"demo": McpServerOptions(client=client)})
    workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: invoke
                type: mcp.call
                input:
                  server: demo
                  method: write
                  request: {kind: file, note: null}
        """
    )

    result = await engine.execute_async(workflow, {})

    assert not result.success
    assert result.error is not None and result.error.code == "INPUT_VALIDATION"
    assert "target" in result.error.message
    assert client.calls == []


class _HumanProvider:
    async def request_input_async(self, request):
        return {"response": f"answer-for-{request.step_id}", "source": "test-provider"}


@pytest.mark.asyncio
async def test_elicitation_uses_exact_call_correlation_without_cross_talk() -> None:
    factory = ConfiguredMcpClientFactory(
        {"demo": McpServerOptions(client=_CaptureClient())},
        human_input_provider=_HumanProvider(),
    )
    first = McpCorrelationContext(
        correlation_id="same-run",
        run_id="same-run",
        step_id="first",
        mcp_server="demo",
        mcp_method="write",
    )
    second = McpCorrelationContext(
        correlation_id="same-run",
        run_id="same-run",
        step_id="second",
        mcp_server="demo",
        mcp_method="write",
    )
    first_phases = []
    second_phases = []
    request = {
        "message": "Choose a value",
        "requestedSchema": {
            "type": "object",
            "properties": {"choice": {"type": "string", "enum": ["a", "b"]}},
            "required": ["choice"],
        },
        "_meta": {
            "gnougo": {
                "correlationId": "same-run",
                "runId": "same-run",
                "stepId": "second",
                "mcpServer": "demo",
                "mcpMethod": "write",
            }
        },
    }

    with (
        factory.push_human_input_handler(first, lambda signal: first_phases.append(signal.phase)),
        factory.push_human_input_handler(second, lambda signal: second_phases.append(signal.phase)),
    ):
        response = await factory.handle_elicitation_async(request, "demo")

    assert response == {"action": "accept", "content": {"answer": "answer-for-second"}}
    assert first_phases == []
    assert second_phases == [McpHumanInputSignalPhase.WAITING, McpHumanInputSignalPhase.RESUMED]


@pytest.mark.asyncio
async def test_mcp_timeout_and_transport_cancellation_are_distinct() -> None:
    timeout_engine = WorkflowEngine()
    timeout_engine.mcp_client_factory = ConfiguredMcpClientFactory(
        {"demo": McpServerOptions(client=_CaptureClient(delay=0.05))}
    )
    timeout_workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: invoke
                type: mcp.call
                input: {server: demo, method: write, timeout_ms: 10, request: {kind: file, target: a}}
        """
    )

    timeout_result = await timeout_engine.execute_async(timeout_workflow, {})

    assert not timeout_result.success
    assert timeout_result.error is not None and timeout_result.error.code == "MCP_TIMEOUT"

    cancelled_engine = WorkflowEngine()
    cancelled_engine.mcp_client_factory = ConfiguredMcpClientFactory(
        {"demo": McpServerOptions(client=_CaptureClient(cancel_transport=True))}
    )
    transport_workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: invoke
                type: mcp.call
                input: {server: demo, method: write, timeout_ms: 1000, request: {kind: file, target: a}}
        """
    )
    cancelled_result = await cancelled_engine.execute_async(transport_workflow, {})

    assert not cancelled_result.success
    assert cancelled_result.error is not None and cancelled_result.error.code == "MCP_CALL_ERROR"
    assert "transport" in cancelled_result.error.message


@pytest.mark.asyncio
async def test_uncorrelated_elicitation_is_rejected_when_multiple_calls_are_active() -> None:
    factory = ConfiguredMcpClientFactory(
        {"demo": McpServerOptions(client=_CaptureClient())},
        human_input_provider=_HumanProvider(),
    )
    first = McpCorrelationContext(step_id="first", mcp_server="demo")
    second = McpCorrelationContext(step_id="second", mcp_server="demo")

    with (
        factory.push_human_input_handler(first, lambda signal: None),
        factory.push_human_input_handler(second, lambda signal: None),
    ):
        with pytest.raises(WorkflowRuntimeException) as exc:
            await factory.handle_elicitation_async({"message": "Need input"}, "demo")

    assert exc.value.code == "MCP_CALL_ERROR"
