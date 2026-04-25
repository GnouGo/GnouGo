
import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import ITelemetrySpan, WorkflowEngine


class CaptureSpan(ITelemetrySpan):
    def __init__(self, name: str) -> None:
        self.name = name
        self.attributes: dict[str, object] = {}
        self.events: list[tuple[str, list[tuple[str, object]]]] = []

    def set_attribute(self, key: str, value):
        self.attributes[key] = value

    def add_event(self, name: str, attributes=None):
        self.events.append((name, list(attributes or [])))


class CaptureTelemetry:
    def __init__(self) -> None:
        self.step_spans: list[CaptureSpan] = []

    def workflow_start(self, info):
        return CaptureSpan("workflow")

    def workflow_end(self, span, info):
        return

    def step_start(self, parent, info):
        span = CaptureSpan(info.get("step_id", "step"))
        self.step_spans.append(span)
        return span

    def step_end(self, span, info):
        return


_YAML = """
dsl: 1
workflows:
  main:
    steps:
      - id: a
        type: set
        input: { x: 1 }
"""


@pytest.mark.asyncio
async def test_step_input_output_events_disabled_by_default() -> None:
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(_YAML))
    engine = WorkflowEngine()
    tel = CaptureTelemetry()
    engine.telemetry = tel
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True
    span = tel.step_spans[0]
    names = [n for n, _ in span.events]
    assert "gnougo-flow.step.input" not in names
    assert "gnougo-flow.step.output" not in names


@pytest.mark.asyncio
async def test_step_input_output_events_when_enabled() -> None:
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(_YAML))
    engine = WorkflowEngine()
    engine.limits.log_step_content = True
    tel = CaptureTelemetry()
    engine.telemetry = tel
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True
    span = tel.step_spans[0]
    by_name = {n: dict(attrs) for n, attrs in span.events}
    assert "gnougo-flow.step.input" in by_name
    assert "gnougo-flow.step.output" in by_name
    in_attrs = by_name["gnougo-flow.step.input"]
    assert in_attrs["gnougo-flow.step.id"] == "a"
    assert in_attrs["gnougo-flow.step.type"] == "set"
    assert in_attrs["gnougo-flow.step.call_depth"] == 0
    assert "gnougo-flow.content.input" in in_attrs
    out_attrs = by_name["gnougo-flow.step.output"]
    assert "gnougo-flow.content.output" in out_attrs

