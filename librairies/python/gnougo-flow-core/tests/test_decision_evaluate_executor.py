import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import ExecutionLimits
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine
from gnougo_flow_core.workflow_plan_semantic_validator import (
    WorkflowSemanticValidationException,
    validate_workflow_semantics,
)


def _compile(input_yaml: str):
    indented = "\n".join(f"          {line}" for line in input_yaml.splitlines())
    return WorkflowCompiler().compile(
        WorkflowParser.parse(
            f"""
version: 1
workflows:
  main:
    steps:
      - id: evaluate
        type: decision.evaluate
        input:
{indented}
"""
        )
    ).workflows["main"]


async def _execute(input_yaml: str, inputs=None, limits=None):
    engine = WorkflowEngine()
    if limits is not None:
        engine.limits = limits
    return await engine.execute_async(_compile(input_yaml), inputs or {})


@pytest.mark.asyncio
async def test_selects_matching_case_and_no_effect_default() -> None:
    result = await _execute(
        """decisions:
  selected:
    allowed_values: [ACCEPT, REJECT, NONE]
    cases:
      - { when: "${data.inputs.accept}", value: ACCEPT }
      - { when: "${data.inputs.reject}", value: REJECT }
    default: NONE
  defaulted:
    allowed_values: [WRITE, NO_EFFECT]
    cases:
      - { when: false, value: WRITE }
    default: NO_EFFECT""",
        {"accept": True, "reject": False},
    )

    assert result.success is True, result.error
    assert result.step_results[0].output == {
        "selected": "ACCEPT",
        "defaulted": "NO_EFFECT",
    }


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("cases", "expected_message"),
    [
        ("[{ when: true, value: FIRST }, { when: true, value: SECOND }]", "more than one"),
        ("[{ when: false, value: FIRST }, { when: false, value: SECOND }]", "no matching"),
    ],
)
async def test_unresolved_selection_fails_closed(cases: str, expected_message: str) -> None:
    result = await _execute(
        f"""decisions:
  outcome:
    allowed_values: [FIRST, SECOND]
    cases: {cases}"""
    )

    assert result.success is False
    assert result.error.code == "DECISION_EVALUATION_UNRESOLVED"
    assert expected_message in result.error.message
    assert result.step_results[0].output is None


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("contract", "expected_message"),
    [
        ("allowed_values: [A, A]\n    cases: [{ when: true, value: A }]", "unique strings"),
        ("allowed_values: [A]\n    cases: [{ when: 1, value: A }]", "boolean"),
        ("allowed_values: [A]\n    cases: [{ when: true, value: B }]", "allowed_values"),
        (
            "allowed_values: [A]\n    cases: [{ when: true, value: A }, { when: false, value: A }]",
            "case values must be unique",
        ),
        (
            "allowed_values: [A]\n    cases: [{ when: true, value: A }]\n    extra: value",
            "unknown field",
        ),
    ],
)
async def test_malformed_contracts_use_input_validation(contract: str, expected_message: str) -> None:
    result = await _execute(f"decisions:\n  outcome:\n    {contract}")

    assert result.success is False
    assert result.error.code == "INPUT_VALIDATION"
    assert expected_message in result.error.message


@pytest.mark.asyncio
async def test_oversized_and_atomic_failure() -> None:
    oversized = await _execute(
        """decisions:
  first: { allowed_values: [A], cases: [{ when: true, value: A }] }
  second: { allowed_values: [A], cases: [{ when: true, value: A }] }
  third: { allowed_values: [A], cases: [{ when: true, value: A }] }""",
        limits=ExecutionLimits(max_switch_cases=2),
    )
    assert oversized.success is False
    assert oversized.error.code == "INPUT_VALIDATION"

    atomic = await _execute(
        """decisions:
  resolved: { allowed_values: [A], cases: [{ when: true, value: A }] }
  unresolved: { allowed_values: [A], cases: [{ when: false, value: A }] }"""
    )
    assert atomic.success is False
    assert atomic.error.code == "DECISION_EVALUATION_UNRESOLVED"
    assert atomic.step_results[0].output is None


@pytest.mark.asyncio
async def test_oversized_case_set_uses_switch_case_limit() -> None:
    result = await _execute(
        """decisions:
  outcome:
    allowed_values: [A, B, C]
    cases:
      - { when: true, value: A }
      - { when: false, value: B }
      - { when: false, value: C }""",
        limits=ExecutionLimits(max_switch_cases=2),
    )

    assert result.success is False
    assert result.error.code == "INPUT_VALIDATION"
    assert "case count (3) exceeds limit (2)" in result.error.message


def test_semantic_validation_uses_finite_decision_output_contract() -> None:
    document = WorkflowParser.parse(
        """
version: 1
workflows:
  main:
    steps:
      - id: decide
        type: decision.evaluate
        input:
          decisions:
            outcome:
              allowed_values: [ACCEPT, REJECT]
              cases: [{ when: true, value: ACCEPT }]
      - id: consume
        type: template.render
        input:
          template: "Decision ${data.steps.decide.missing}"
"""
    )

    with pytest.raises(WorkflowSemanticValidationException) as exception:
        validate_workflow_semantics(document)

    assert "STEP_OUTPUT_PROPERTY_UNKNOWN" in str(exception.value)
    assert "data.steps.decide.missing" in str(exception.value)
    assert "data.steps.decide.outcome" in str(exception.value)
