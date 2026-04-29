from gnougo_flow_cli.telemetry import (
    WORKFLOW_SOURCE_ATTRIBUTE_LIMIT,
    _apply_workflow_source_attributes,
)


class _FakeSpan:
    def __init__(self) -> None:
        self.attributes = {}

    def set_attribute(self, key, value) -> None:
        self.attributes[key] = value


def test_apply_workflow_source_attributes_emits_dotnet_compatible_tags() -> None:
    span = _FakeSpan()
    source = "dsl: 1\nworkflows:\n  main:\n    steps: []\n"

    _apply_workflow_source_attributes(
        span,
        {
            "source_text": source,
            "source_format": "yaml",
        },
    )

    assert span.attributes["gnougo-flow.workflow.source.format"] == "yaml"
    assert span.attributes["gnougo-flow.workflow.source.length"] == len(source)
    assert span.attributes["gnougo-flow.workflow.source.truncated"] is False
    assert span.attributes["gnougo-flow.workflow.source.limit"] == WORKFLOW_SOURCE_ATTRIBUTE_LIMIT
    assert span.attributes["gnougo-flow.workflow.source"] == source


def test_apply_workflow_source_attributes_truncates_large_sources() -> None:
    span = _FakeSpan()
    source = "x" * (WORKFLOW_SOURCE_ATTRIBUTE_LIMIT + 5)

    _apply_workflow_source_attributes(span, {"source_text": source})

    assert span.attributes["gnougo-flow.workflow.source.format"] == "yaml"
    assert span.attributes["gnougo-flow.workflow.source.length"] == len(source)
    assert span.attributes["gnougo-flow.workflow.source.truncated"] is True
    assert len(span.attributes["gnougo-flow.workflow.source"]) == WORKFLOW_SOURCE_ATTRIBUTE_LIMIT


def test_apply_workflow_source_attributes_ignores_empty_sources() -> None:
    span = _FakeSpan()

    _apply_workflow_source_attributes(span, {"source_text": "   "})

    assert span.attributes == {}

