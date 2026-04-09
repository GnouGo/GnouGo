from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser


def test_parse_and_compile_basic_workflow() -> None:
    yaml_text = """
    dsl: 1
    name: basic
    workflows:
      main:
        steps:
          - id: init
            type: set
            input:
              answer: 42
        outputs:
          answer: "${data.steps.init.answer}"
    """

    doc = WorkflowParser.parse(yaml_text)
    compiled = WorkflowCompiler().compile(doc)

    assert compiled.entrypoint == "main"
    assert "main" in compiled.workflows
    assert compiled.workflows["main"].steps[0].id == "init"

