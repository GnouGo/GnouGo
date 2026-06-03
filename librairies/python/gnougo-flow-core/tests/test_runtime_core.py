import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import ExecutionLimits
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


@pytest.mark.asyncio
async def test_runtime_executes_set_template_loop_and_switch() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: init
            type: set
            input:
              name: "${data.inputs.name}"
              should_loop: true
          - id: greet
            type: template.render
            input:
              template: "Hello {{name}}"
              data:
                name: "${data.steps.init.name}"
          - id: loop
            type: loop.sequential
            input:
              times: 2
            steps:
              - id: pass
                type: set
                input:
                  idx: "${data._loop.index}"
          - id: route
            type: switch
            expr: "${data.steps.init.name}"
            cases:
              - value: Alice
                steps:
                  - id: branch
                    type: set
                    input:
                      picked: "alice"
            default:
              - id: branch_default
                type: set
                input:
                  picked: "default"
        outputs:
          text: "${data.steps.greet.text}"
          picked: "${data.steps.branch.picked}"
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    engine = WorkflowEngine()
    result = await engine.execute_async(compiled.workflows["main"], {"name": "Alice"})

    assert result.success is True
    assert result.outputs["text"] == "Hello Alice"
    assert result.outputs["picked"] == "alice"


@pytest.mark.asyncio
async def test_runtime_switch_supports_boolean_numeric_and_null_case_values() -> None:
    yaml_text = """
    version: 1
    workflows:
      bool_case:
        steps:
          - id: route
            type: switch
            expr: "${data.inputs.flag}"
            cases:
              - value: true
                steps:
                  - id: picked
                    type: set
                    input: { value: "bool" }
            default:
              - id: picked_default
                type: set
                input: { value: "default" }
        outputs:
          picked: "${data.steps.picked.value}"

      number_case:
        steps:
          - id: route
            type: switch
            expr: "${data.inputs.code}"
            cases:
              - value: 2
                steps:
                  - id: picked
                    type: set
                    input: { value: "number" }
            default:
              - id: picked_default
                type: set
                input: { value: "default" }
        outputs:
          picked: "${data.steps.picked.value}"

      null_case:
        steps:
          - id: route
            type: switch
            expr: "${data.inputs.optional}"
            cases:
              - when: "${data.inputs.optional == null}"
                steps:
                  - id: picked
                    type: set
                    input: { value: "null" }
            default:
              - id: picked_default
                type: set
                input: { value: "default" }
        outputs:
          picked: "${data.steps.picked.value}"
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    engine = WorkflowEngine()

    bool_result = await engine.execute_async(compiled.workflows["bool_case"], {"flag": True})
    number_result = await engine.execute_async(compiled.workflows["number_case"], {"code": 2})
    null_result = await engine.execute_async(compiled.workflows["null_case"], {"optional": None})

    assert bool_result.success is True
    assert bool_result.outputs["picked"] == "bool"
    assert number_result.success is True
    assert number_result.outputs["picked"] == "number"
    assert null_result.success is True
    assert null_result.outputs["picked"] == "null"


def test_execution_limits_expression_defaults_match_dotnet_runtime() -> None:
    limits = ExecutionLimits()

    assert limits.max_total_steps_executed == 10_000
    assert limits.max_expression_statements == 100_000
    assert limits.expression_timeout_seconds == 15
    assert limits.expression_memory_limit_bytes == 50_000_000


