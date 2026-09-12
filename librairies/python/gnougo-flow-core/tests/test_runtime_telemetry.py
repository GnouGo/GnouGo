import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse, LLMToolCall, McpCallResult, McpServerMetadata, McpToolInfo
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import ITelemetrySpan, WorkflowEngine


class CaptureSpan(ITelemetrySpan):
    def __init__(self, name: str) -> None:
        self.name = name
        self.parent_name: str | None = None
        self.attributes: dict[str, object] = {}
        self.events: list[tuple[str, list[tuple[str, object]]]] = []

    def set_attribute(self, key: str, value):
        self.attributes[key] = value

    def add_event(self, name: str, attributes=None):
        self.events.append((name, list(attributes or [])))


class CaptureTelemetry:
    def __init__(self) -> None:
        self.workflow_starts: list[dict[str, object]] = []
        self.step_spans: list[CaptureSpan] = []
        self.child_spans: list[CaptureSpan] = []

    def workflow_start(self, info):
        self.workflow_starts.append(info)
        return CaptureSpan("workflow")

    def workflow_end(self, span, info):
        return

    def step_start(self, parent, info):
        span = CaptureSpan(info.get("step_id", "step"))
        self.step_spans.append(span)
        return span

    def step_end(self, span, info):
        return

    def span_start(self, parent, info):
        span = CaptureSpan(str(info.get("name", "span")))
        span.parent_name = getattr(parent, "name", None)
        for key, value in info.get("attributes") or []:
            span.attributes[key] = value
        self.child_spans.append(span)
        return span

    def span_end(self, span, info):
        span.attributes["gnougo-flow.span.success"] = info.get("success")


class FakeLLMClient:
    async def call_async(self, request):
        if request.tools:
            return LLMResponse(
                text="selected capability",
                tool_calls=[LLMToolCall(id="tc1", name=request.tools[0].name, arguments={"query": "x"})],
                usage={"prompt_tokens": 3, "completion_tokens": 2, "total_tokens": 5},
            )
        if request.structured_output_schema is not None:
            return LLMResponse(
                text='{"answer":"ok"}',
                json_payload={"answer": "ok"},
                usage={"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2},
            )
        return LLMResponse(text="hello", usage={"prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2})


class FakeMcpSession:
    server_name = "demo"

    async def list_tools_async(self):
        return [McpToolInfo(name="search", description="search tool")]

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments):
        return McpCallResult(
            is_error=False,
            content={"tool": tool_name, "args": arguments},
            usage={"prompt_tokens": 4, "completion_tokens": 2, "total_tokens": 6},
            model="mcp-model",
        )

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class FakeMcpFactory:
    def __init__(self):
        self.server_metadata = []

    async def get_client_async(self, server_name):
        return FakeMcpSession()


class TraceCapturingMcpSession:
    server_name = "GnOuGo.GithubCopilot.Mcp"

    def __init__(self) -> None:
        self.captured_meta = None

    async def list_tools_async(self):
        return []

    async def list_resources_async(self):
        return []

    async def list_prompts_async(self):
        return []

    async def call_tool_async(self, tool_name, arguments, mcp_meta=None):
        self.captured_meta = mcp_meta
        return McpCallResult(is_error=False, content={"ok": True})

    async def get_prompt_async(self, prompt_name, arguments):
        raise NotImplementedError


class TraceCapturingMcpFactory:
    def __init__(self) -> None:
        self.server_metadata = []
        self.session = TraceCapturingMcpSession()

    async def get_client_async(self, server_name):
        return self.session






@pytest.mark.asyncio
async def test_llm_and_mcp_emit_expected_telemetry_keys() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: ask
            type: llm.call
            input:
              model: fake
              prompt: "hello"
          - id: discover
            type: mcp.list
            input:
              servers: [demo]
              include: [tools]
          - id: smart
            type: mcp.call
            input:
              server: demo
              model: fake
              prompt: "find capability"
              tools: "${data.steps.discover.tools}"
              structured_output:
                schema_inline:
                  type: object
                  properties:
                    answer: { type: string }
                  required: [answer]
                strict: true
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    engine = WorkflowEngine()
    engine.llm_client = FakeLLMClient()
    engine.mcp_client_factory = FakeMcpFactory()
    telemetry = CaptureTelemetry()
    engine.telemetry = telemetry

    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert telemetry.workflow_starts[0]["source_format"] == "yaml"
    assert "type: llm.call" in telemetry.workflow_starts[0]["source_text"]

    spans = {s.name: s for s in telemetry.step_spans}

    assert spans["ask"].attributes["gen_ai.operation.name"] == "chat"
    assert spans["ask"].attributes["gen_ai.request.model"] == "fake"
    assert spans["ask"].attributes["gen_ai.response.finish_reason"] == "stop"

    assert spans["discover"].attributes["gen_ai.operation.name"] == "tool_list"
    assert spans["discover"].attributes["mcp.server.name"] == "demo"

    assert spans["smart"].attributes["mcp.server.name"] == "demo"
    assert spans["smart"].attributes["gen_ai.request.model"] == "fake"
    assert spans["smart"].attributes["mcp.methods_count"] == 1
    assert spans["smart"].attributes["gen_ai.usage.total_tokens"] >= 1


@pytest.mark.asyncio
async def test_mcp_call_forwards_opentelemetry_parent_meta_to_session(monkeypatch) -> None:
    monkeypatch.setenv("TRACEPARENT", "00-11111111111111111111111111111111-2222222222222222-01")
    monkeypatch.setenv("TRACESTATE", "vendor=value")

    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: code
            type: mcp.call
            input:
              server: GnOuGo.GithubCopilot.Mcp
              method: code_project_summary
              request:
                projectRoot: null
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    engine = WorkflowEngine()
    factory = TraceCapturingMcpFactory()
    engine.mcp_client_factory = factory

    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    meta = factory.session.captured_meta
    assert meta["traceparent"] == "00-11111111111111111111111111111111-2222222222222222-01"
    assert meta["tracestate"] == "vendor=value"
    assert meta["gnougo"]["traceId"] == "11111111111111111111111111111111"
    assert meta["gnougo"]["parentSpanId"] == "2222222222222222"
    assert meta["gnougo"]["mcpServer"] == "GnOuGo.GithubCopilot.Mcp"
    assert meta["gnougo"]["mcpMethod"] == "code_project_summary"
    assert meta["gnougo"]["mcpKind"] == "tool"
