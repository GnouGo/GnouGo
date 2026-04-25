from __future__ import annotations

from gnougo_flow_core.models import InputDef, WorkflowDef
from gnougo_flow_core.runtime import apply_workflow_input_defaults


def test_apply_workflow_input_defaults_adds_missing_defaults() -> None:
    workflow = WorkflowDef(
        inputs={
            "site": InputDef(type="string", required=False, default="https://example.com/"),
            "count": InputDef(type="number", required=False, default=3),
            "flags": InputDef(type="array", required=False, default=[True, False]),
        }
    )

    merged = apply_workflow_input_defaults(workflow, {})

    assert merged["site"] == "https://example.com/"
    assert merged["count"] == 3
    assert merged["flags"] == [True, False]


def test_apply_workflow_input_defaults_does_not_override_explicit_inputs() -> None:
    workflow = WorkflowDef(
        inputs={
            "site": InputDef(type="string", required=False, default="https://example.com/"),
        }
    )

    merged = apply_workflow_input_defaults(workflow, {"site": "https://www.iana.org/"})

    assert merged["site"] == "https://www.iana.org/"


def test_apply_workflow_input_defaults_handles_missing_workflow_or_inputs() -> None:
    assert apply_workflow_input_defaults(None, {"x": 1}) == {"x": 1}
    assert apply_workflow_input_defaults(WorkflowDef(), None) == {}

