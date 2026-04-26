import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import WorkflowParseException
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


def test_parse_requires_version_or_dsl() -> None:
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


