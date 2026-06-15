import pytest

from gnougo_flow_core.compilation import WorkflowCompiler, WorkflowValidator
from gnougo_flow_core.errors import ErrorCodes, WorkflowParseException
from gnougo_flow_core.parsing import WorkflowParser


def test_parse_and_compile_basic_workflow() -> None:
    yaml_text = """
    version: 1
    name: basic
    skill:
      description: Basic test workflow.
      tags: [test]
      inputs: {}
      outputs: {}
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


def test_validate_requires_top_level_skill() -> None:
    yaml_text = """
    version: 1
    name: missing-skill
    workflows:
      main:
        steps:
          - id: init
            type: set
            input:
              answer: 42
    """

    doc = WorkflowParser.parse(yaml_text)
    errors = WorkflowValidator().validate(doc)

    assert any(error.code == ErrorCodes.SKILL_REQUIRED and error.field == "skill" for error in errors)


def test_parse_with_skill_sets_top_level_skill_metadata() -> None:
    yaml_text = """
    version: 1
    name: skill-demo
    skill:
      description: Answers document questions.
      tags: [documents, rag]
      inputs:
        prompt:
          type: string
          required: true
      outputs:
        answer:
          type: string
          description: Final answer.
    workflows:
      main:
        steps:
          - id: s1
            type: template.render
    """

    doc = WorkflowParser.parse(yaml_text)

    assert doc.skill is not None
    assert doc.skill.description == "Answers document questions."
    assert doc.skill.tags == ["documents", "rag"]
    assert doc.skill.inputs["prompt"].type == "string"
    assert doc.skill.outputs["answer"].type == "string"


def test_parse_skill_reads_only_top_level_skill_metadata() -> None:
    yaml_text = """
    version: 1
    skill:
      description: Inspects git repositories.
      tags:
        - git
        - code
    workflows:
      main:
        steps:
          - id: s1
            type: template.render
    """

    skill = WorkflowParser.parse_skill(yaml_text)

    assert skill is not None
    assert skill.description == "Inspects git repositories."
    assert skill.tags == ["git", "code"]


def test_parse_requires_version() -> None:
    yaml_text = """
    workflows:
      main:
        steps:
          - id: init
            type: set
            input: {}
    """

    with pytest.raises(WorkflowParseException, match="Missing required field 'version'"):
        WorkflowParser.parse(yaml_text)


def test_parse_rejects_legacy_dsl_without_version() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: init
            type: set
            input: {}
    """

    with pytest.raises(WorkflowParseException, match="Missing required field 'version'"):
        WorkflowParser.parse(yaml_text)


@pytest.mark.parametrize("version", ["1", "1.0"])
def test_parse_accepts_version_string_one(version: str) -> None:
    yaml_text = f'''
    version: "{version}"
    workflows:
      main:
        steps:
          - id: init
            type: set
            input: {{}}
    '''

    doc = WorkflowParser.parse(yaml_text)
    assert doc.version == 1


def test_parse_on_error_object_set_output_preserves_template_object() -> None:
    yaml_text = '''
    version: 1
    workflows:
      main:
        steps:
          - id: fetch_page
            type: mcp.call
            on_error:
              cases:
                - action: continue
                  set_output:
                    status: error
                    response:
                      url: "${data.item.url}"
                      error_code: "${error.code}"
                      error_message: "${error.message}"
    '''

    doc = WorkflowParser.parse(yaml_text)
    set_output = doc.workflows["main"].steps[0].on_error.cases[0].set_output

    assert isinstance(set_output, dict)
    assert set_output["status"] == "error"
    assert set_output["response"]["url"] == "${data.item.url}"
    assert set_output["response"]["error_code"] == "${error.code}"


def test_parse_input_required_bool_and_object_required_list() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        inputs:
          optional_name:
            type: string
            required: false
          config:
            type: object
            properties:
              host: { type: string }
              port: { type: number }
            required: [host]
        steps:
          - id: init
            type: set
            input: {}
    """

    doc = WorkflowParser.parse(yaml_text)
    inputs = doc.workflows["main"].inputs
    assert inputs is not None
    assert inputs["optional_name"].required is False
    assert inputs["optional_name"].required_properties is None
    assert inputs["config"].required is True
    assert inputs["config"].required_properties == ["host"]


def test_parse_output_type_only_schema_branch() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: init
            type: set
            input: {}
        outputs:
          report:
            type: object
            properties:
              tags:
                type: array
                items:
                  type: string
    """

    doc = WorkflowParser.parse(yaml_text)
    report = doc.workflows["main"].outputs["report"]
    assert report.type == "object"
    assert report.expr == ""
    assert report.properties is not None
    assert report.properties["tags"].type == "array"
    assert report.properties["tags"].items is not None
    assert report.properties["tags"].items.type == "string"


def test_validator_reports_export_and_schema_errors() -> None:
    yaml_text = """
    version: 1
    exports: [missing_workflow]
    workflows:
      main:
        inputs:
          bad_input:
            type: string
            items: { type: string }
          bad_object:
            type: object
            properties:
              name: { type: string }
            required: [missing]
        steps:
          - id: choose
            type: switch
            cases:
              - when: "${oops(}"
                steps:
                  - id: init
                    type: set
                    input: {}
        outputs:
          bad_output:
            type: string
            properties:
              nested:
                expr: "${oops(}"
                type: string
    """

    errors = WorkflowCompiler().validate(WorkflowParser.parse(yaml_text))
    codes = {err.code for err in errors}
    assert "INVALID_EXPORT" in codes
    assert "INVALID_INPUT_SCHEMA" in codes
    assert "INVALID_OUTPUT_SCHEMA" in codes
    assert "EXPR_PARSE" in codes


def test_validator_reports_invalid_input_and_output_types() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        inputs:
          bad:
            type: mystery
        steps:
          - id: init
            type: set
            input: {}
        outputs:
          answer:
            expr: "${data.steps.init}"
            type: nope
    """

    errors = WorkflowCompiler().validate(WorkflowParser.parse(yaml_text))
    codes = {err.code for err in errors}
    assert "INVALID_INPUT_TYPE" in codes
    assert "INVALID_OUTPUT_TYPE" in codes
