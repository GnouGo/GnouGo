import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


@pytest.mark.asyncio
async def test_runtime_executes_set_template_loop_and_switch() -> None:
    yaml_text = """
    dsl: 1
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

