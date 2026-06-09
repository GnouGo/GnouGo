import json

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.runtime_contracts import ITelemetrySpan, IWorkflowTelemetry


def compile_main(yaml_text):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


class Session:
    server_name = "srv"

    def __init__(self):
        self.list_tools_calls = 0

    async def list_tools_async(self):
        self.list_tools_calls += 1
        return [McpToolInfo(name="ping")]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments):
        raise NotImplementedError

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class Factory:
    def __init__(self):
        self.session = Session()
        self.server_metadata = [type("Meta", (), {"name": "srv"})()]

    async def get_client_async(self, server_name):
        return self.session


@pytest.mark.asyncio
async def test_mcp_list_uses_cache_across_steps():
    engine = WorkflowEngine()
    factory = Factory()
    engine.mcp_client_factory = factory
    wf = compile_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: first
                type: mcp.list
                input:
                  servers: [srv]
              - id: second
                type: mcp.list
                input:
                  servers: [srv]
            outputs:
              first_count: "${len(data.steps.first.tools)}"
              second_count: "${len(data.steps.second.tools)}"
        """
    )

    result = await engine.execute_async(wf, {})

    assert result.success is True
    assert result.outputs == {"first_count": 1, "second_count": 1}
    assert factory.session.list_tools_calls == 1


@pytest.mark.asyncio
async def test_mcp_list_rejects_mixing_wildcard_and_explicit_servers():
    engine = WorkflowEngine()
    engine.mcp_client_factory = Factory()
    wf = compile_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: list
                type: mcp.list
                input:
                  servers: ["*", srv]
        """
    )

    result = await engine.execute_async(wf, {})

    assert result.success is False
    assert result.error.code == "INPUT_VALIDATION"
    assert "cannot mix '*'" in result.error.message


class CaptureSpan(ITelemetrySpan):
    def __init__(self):
        self.events = []

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


class HumanProvider:
    def __init__(self):
        self.last_request = None

    async def request_input_async(self, request):
        self.last_request = request
        return {"email": "user@example.com", "priority": "high"}


@pytest.mark.asyncio
async def test_human_input_fields_telemetry_and_zero_timeout():
    telemetry = CaptureTelemetry()
    provider = HumanProvider()
    engine = WorkflowEngine()
    engine.telemetry = telemetry
    engine.human_input_provider = provider
    engine.limits.run_id = "run-phase5"

    wf = compile_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: form
                type: human.input
                input:
                  mode: form
                  prompt: Please fill in your details
                  timeout_ms: 0
                  fields:
                    - name: email
                      type: string
                      required: true
                      description: Your email
                    - name: priority
                      type: select
                      options: [low, medium, high]
                      default: medium
        """
    )

    result = await engine.execute_async(wf, {})

    assert result.success is True
    assert provider.last_request.run_id == "run-phase5"
    assert provider.last_request.mode == "form"
    assert provider.last_request.timeout_ms == 0
    assert provider.last_request.fields[0].name == "email"
    assert provider.last_request.fields[1].options == ["low", "medium", "high"]

    events = [event for span in telemetry.spans for event in span.events]
    waiting = next(e for e in events if e[0] == "gnougo-flow.step.waiting_for_human")
    payload = json.loads(waiting[1]["gnougo-flow.human.request"])
    assert payload["run_id"] == "run-phase5"
    assert payload["mode"] == "form"
    assert payload["fields"][1]["default"] == "medium"
    assert any(e[0] == "gnougo-flow.step.thinking" for e in events)
