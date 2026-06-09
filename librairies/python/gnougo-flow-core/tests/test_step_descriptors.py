from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.runtime_steps import STEP_TYPES
from gnougo_flow_core.runtime_steps.human_input_executor import HumanInputExecutor
from gnougo_flow_core.step_types import STEP_TYPES as DECLARED_STEP_TYPES
from gnougo_flow_core.workflow_plan_semantic_validator import validate_workflow_semantics


def test_all_registered_steps_are_self_described() -> None:
    engine = WorkflowEngine()
    registry = engine.registry

    snippet_map = registry.get_dsl_snippet_map()
    catalogs = registry.get_step_exception_catalogs()

    described_types = set(snippet_map.keys())
    exception_types = {catalog.step_type for catalog in catalogs}

    assert described_types == set(STEP_TYPES)
    assert exception_types == set(STEP_TYPES)


def test_step_description_filtering_uses_allowed_step_types() -> None:
    engine = WorkflowEngine()
    allowed = {"mcp.list", "mcp.call"}

    snippet_map = engine.registry.get_dsl_snippet_map(allowed)
    catalogs = engine.registry.get_step_exception_catalogs(allowed)

    assert set(snippet_map.keys()) == allowed
    assert {catalog.step_type for catalog in catalogs} == allowed


def test_declared_step_types_match_runtime_step_types() -> None:
    assert STEP_TYPES == DECLARED_STEP_TYPES


def test_human_input_dsl_snippet_contains_planner_contract() -> None:
    snippet = HumanInputExecutor.dsl_snippet

    assert "Always set `input.mode` explicitly" in snippet
    assert "Valid modes: text, choice, form, confirm." in snippet
    assert "date" in snippet
    assert "mode: confirm" in snippet
    assert "data.steps.<id>.response" in snippet
    assert "data.steps.<id>.<field_name>" in snippet


def test_semantic_validator_allows_human_input_response_and_form_fields() -> None:
    doc = WorkflowParser.parse(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: approval
                type: human.input
                input:
                  mode: choice
                  prompt: "Approve?"
                  choices: [approve, reject]
              - id: schedule
                type: human.input
                input:
                  mode: form
                  prompt: "Pick a due date"
                  fields:
                    - name: due_date
                      type: date
                      required: true
                    - name: retry_count
                      type: integer
                      required: false
              - id: use_values
                type: set
                input:
                  decision: "${data.steps.approval.response}"
                  due: "${data.steps.schedule.due_date}"
                  retries: "${data.steps.schedule.retry_count}"
        """
    )

    validate_workflow_semantics(doc)

