from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from opentelemetry import trace
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor

WORKFLOW_SOURCE_ATTRIBUTE_LIMIT = 64 * 1024
_WORKFLOW_SOURCE_INFO_KEYS = {"source_text", "source_format"}


@dataclass(slots=True)
class TelemetryConfig:
    service_name: str = "gnougo-flow-cli"
    otlp_endpoint: str | None = None


def setup_tracing(config: TelemetryConfig) -> None:
    resource = Resource.create({"service.name": config.service_name})
    provider = TracerProvider(resource=resource)
    if config.otlp_endpoint:
        exporter = OTLPSpanExporter(endpoint=config.otlp_endpoint)
        provider.add_span_processor(BatchSpanProcessor(exporter))
    trace.set_tracer_provider(provider)


class OTelWorkflowTelemetry:
    """Small adapter compatible with gnougo_flow_core runtime telemetry hooks."""

    def __init__(self) -> None:
        self._tracer = trace.get_tracer("gnougo-flow-cli.flow")

    def workflow_start(self, info: dict[str, Any]):
        span = _SpanAdapter(self._tracer.start_span("workflow.run"))
        for key, value in info.items():
            if key not in _WORKFLOW_SOURCE_INFO_KEYS and value is not None:
                span.set_attribute(f"gnougo-flow.{key}", _to_attr(value))
        _apply_workflow_source_attributes(span, info)
        return span

    def workflow_end(self, span, info: dict[str, Any]) -> None:
        for key, value in info.items():
            if value is not None:
                span.set_attribute(f"gnougo-flow.result.{key}", _to_attr(value))
        span.end()

    def step_start(self, parent, info: dict[str, Any]):
        parent_span = parent.span if isinstance(parent, _SpanAdapter) else parent
        ctx = trace.set_span_in_context(parent_span)
        span = _SpanAdapter(self._tracer.start_span("workflow.step", context=ctx))
        for key, value in info.items():
            if value is not None:
                span.set_attribute(f"gnougo-flow.step.{key}", _to_attr(value))
        return span

    def step_end(self, span, info: dict[str, Any]) -> None:
        for key, value in info.items():
            if value is not None:
                span.set_attribute(f"gnougo-flow.step.result.{key}", _to_attr(value))
        span.end()

    def span_start(self, parent, info: dict[str, Any]):
        parent_span = parent.span if isinstance(parent, _SpanAdapter) else parent
        ctx = trace.set_span_in_context(parent_span)
        span = _SpanAdapter(
            self._tracer.start_span(
                str(info.get("name") or "workflow.phase"),
                context=ctx,
            )
        )
        phase = info.get("phase")
        if phase:
            span.set_attribute("gnougo-flow.plan.phase", _to_attr(phase))
        for key in ("step_id", "step_type", "call_depth"):
            value = info.get(key)
            if value is not None:
                span.set_attribute(f"gnougo-flow.step.{key}", _to_attr(value))
        for key, value in info.get("attributes") or []:
            if value is not None:
                span.set_attribute(str(key), _to_attr(value))
        return span

    def span_end(self, span, info: dict[str, Any]) -> None:
        for key, value in info.items():
            if value is not None:
                span.set_attribute(f"gnougo-flow.span.result.{key}", _to_attr(value))


def _to_attr(value: Any) -> Any:
    if isinstance(value, (str, bool, int, float)):
        return value
    return str(value)


def _apply_workflow_source_attributes(span, info: dict[str, Any]) -> None:
    source = info.get("source_text")
    if not isinstance(source, str) or not source.strip():
        return

    source_format = info.get("source_format")
    if not isinstance(source_format, str) or not source_format.strip():
        source_format = "yaml"

    truncated = len(source) > WORKFLOW_SOURCE_ATTRIBUTE_LIMIT
    emitted_source = source[:WORKFLOW_SOURCE_ATTRIBUTE_LIMIT] if truncated else source

    span.set_attribute("gnougo-flow.workflow.source.format", source_format)
    span.set_attribute("gnougo-flow.workflow.source.length", len(source))
    span.set_attribute("gnougo-flow.workflow.source.truncated", truncated)
    span.set_attribute("gnougo-flow.workflow.source.limit", WORKFLOW_SOURCE_ATTRIBUTE_LIMIT)
    span.set_attribute("gnougo-flow.workflow.source", emitted_source)


class _SpanAdapter:
    def __init__(self, span) -> None:
        self.span = span

    def set_attribute(self, key: str, value: Any) -> None:
        self.span.set_attribute(key, value)

    def add_event(self, name: str, attributes: list[tuple[str, Any]] | None = None) -> None:
        self.span.add_event(name, attributes=dict(attributes or []))

    def end(self) -> None:
        self.span.end()


