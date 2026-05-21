import asyncio
import json

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.integrations import ConfiguredMcpClientFactory
from gnougo_flow_core.models import McpCallResult, McpServerMetadata, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.runtime_contracts import ITelemetrySpan, IWorkflowTelemetry


def compile_main(yaml_text):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


class CaptureSpan(ITelemetrySpan):
    def __init__(self):
        self.events = []
        self.attributes = {}

    def set_attribute(self, key, value):
        self.attributes[key] = value

    def add_event(self, name, attributes=None):
        self.events.append((name, dict(attributes or [])))


class CaptureTelemetry(IWorkflowTelemetry):
    def __init__(self):
        self.spans = []

    def workflow_start(self, info):
        span = CaptureSpan()
        self.spans.append(span)
        return span

    def step_start(self, parent, info):
        span = CaptureSpan()
        self.spans.append(span)
        return span


def thinking_events(telemetry):
    return [event for span in telemetry.spans for event in span.events if event[0] == "gnougo-flow.step.thinking"]


class ProgressSession:
    server_name = "srv"

    async def list_tools_async(self):
        return [McpToolInfo(name="work")]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments, mcp_meta=None):
        return McpCallResult(
            is_error=False,
            content={
                "ok": True,
                "progressEvents": [
                    {
                        "kind": "completed",
                        "level": "info",
                        "message": "Final progress block.",
                        "timestamp": "2026-05-20T10:00:00Z",
                    }
                ],
            },
        )

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class RealtimeProgressSession(ProgressSession):
    async def call_tool_async(self, tool_name, arguments, mcp_meta=None):
        envelope = {
            "type": "gnougo.mcp.progress",
            "correlationId": (mcp_meta or {}).get("gnougo", {}).get("correlationId"),
            "runId": (mcp_meta or {}).get("gnougo", {}).get("runId"),
            "stepId": (mcp_meta or {}).get("gnougo", {}).get("stepId"),
            "server": "srv",
            "method": tool_name,
            "kind": "tool",
            "event": {
                "kind": "completed",
                "level": "info",
                "message": "Realtime progress block.",
                "timestamp": "2026-05-20T10:00:00Z",
            },
        }
        ConfiguredMcpClientFactory.capture_stdio_error_line("srv", json.dumps(envelope))
        return McpCallResult(
            is_error=False,
            content={
                "ok": True,
                "progressEvents": [
                    {
                        "kind": "completed",
                        "level": "info",
                        "message": "Realtime progress block.",
                        "timestamp": "2026-05-20T10:00:00Z",
                    }
                ],
            },
        )


class SlowSession(ProgressSession):
    async def list_tools_async(self):
        await asyncio.sleep(0.05)
        return [McpToolInfo(name="work")]

    async def call_tool_async(self, tool_name, arguments, mcp_meta=None):
        await asyncio.sleep(0.05)
        return McpCallResult(is_error=False, content={"ok": True})


class Factory:
    def __init__(self, session, metadata=None):
        self.session = session
        self.server_metadata = metadata or [McpServerMetadata(name="srv")]

    async def get_client_async(self, server_name):
        self.session.server_name = server_name
        return self.session


async def run_workflow(yaml_text, factory, telemetry=None):
    engine = WorkflowEngine()
    engine.mcp_client_factory = factory
    if telemetry is not None:
        engine.telemetry = telemetry
    return await engine.execute_async(compile_main(yaml_text), {})


@pytest.mark.asyncio
async def test_mcp_call_final_progress_events_are_forwarded_as_thinking_telemetry():
    telemetry = CaptureTelemetry()

    result = await run_workflow(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: call
                type: mcp.call
                input:
                  server: srv
                  kind: tool
                  method: work
        """,
        Factory(ProgressSession()),
        telemetry,
    )

    assert result.success is True
    events = thinking_events(telemetry)
    assert any(e[1].get("gnougo-flow.thinking.message") == "Final progress block." for e in events)
    progress = next(e for e in events if e[1].get("gnougo-flow.thinking.message") == "Final progress block.")
    assert progress[1]["gnougo-flow.thinking.source"] == "mcp.progress"
    assert progress[1]["gnougo-flow.thinking.kind"] == "completed"


@pytest.mark.asyncio
async def test_mcp_call_realtime_progress_events_are_forwarded_and_final_duplicate_is_skipped():
    telemetry = CaptureTelemetry()

    result = await run_workflow(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: call
                type: mcp.call
                input:
                  server: srv
                  kind: tool
                  method: work
        """,
        Factory(RealtimeProgressSession()),
        telemetry,
    )

    assert result.success is True
    matching = [
        e for e in thinking_events(telemetry)
        if e[1].get("gnougo-flow.thinking.message") == "Realtime progress block."
    ]
    assert len(matching) == 1
    assert matching[0][1]["gnougo-flow.thinking.source"] == "mcp.realtime_progress"


@pytest.mark.asyncio
async def test_mcp_call_server_call_timeout_is_effective_minimum_over_workflow_timeout():
    result = await run_workflow(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: call
                type: mcp.call
                input:
                  server: srv
                  kind: tool
                  method: work
                  timeout_ms: 1
        """,
        Factory(SlowSession(), [McpServerMetadata(name="srv", call_timeout_seconds=1)]),
    )

    assert result.success is True
    assert result.step_results[0].output["response"] == {"ok": True}


@pytest.mark.asyncio
async def test_mcp_list_server_discovery_timeout_is_effective_minimum_over_workflow_timeout():
    result = await run_workflow(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: list
                type: mcp.list
                input:
                  servers: [srv]
                  include: [tools]
                  timeout_ms: 1
        """,
        Factory(SlowSession(), [McpServerMetadata(name="srv", discovery_timeout_seconds=1)]),
    )

    assert result.success is True
    assert result.step_results[0].output["tools"][0]["name"] == "work"

