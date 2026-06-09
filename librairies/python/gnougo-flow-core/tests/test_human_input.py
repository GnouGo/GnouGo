import asyncio

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import ErrorCodes
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


class FakeHumanInputProvider:
    def __init__(self):
        self.last_request = None

    async def request_input_async(self, request):
        self.last_request = request
        await asyncio.sleep(0)
        return {"response": "approve"}


@pytest.mark.asyncio
async def test_human_input_step() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: ask_human
            type: human.input
            input:
              mode: text
              prompt: "Approve?"
              timeout_ms: 1000
        outputs:
          answer: "${data.steps.ask_human.response}"
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    provider = FakeHumanInputProvider()
    engine = WorkflowEngine()
    engine.human_input_provider = provider

    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True
    assert result.outputs["answer"] == "approve"
    assert provider.last_request.mode == "text"


@pytest.mark.asyncio
async def test_human_input_date_field() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: form
            type: human.input
            input:
              mode: form
              prompt: "Pick a date"
              fields:
                - name: due_date
                  type: date
                  required: true
                  default: "2026-06-09"
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    provider = FakeHumanInputProvider()
    engine = WorkflowEngine()
    engine.human_input_provider = provider

    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    assert provider.last_request.mode == "form"
    assert provider.last_request.fields[0].type == "date"
    assert provider.last_request.fields[0].default == "2026-06-09"


@pytest.mark.asyncio
async def test_human_input_scalar_field_metadata_is_normalized() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: form
            type: human.input
            input:
              mode: form
              prompt: "Numeric select"
              fields:
                - name: sets_homme
                  type: select
                  required: "true"
                  options: [3, 4, 5, 6]
                  default: 5
                - name: optional_note
                  type: string
                  required: "false"
                  description: 123
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    provider = FakeHumanInputProvider()
    engine = WorkflowEngine()
    engine.human_input_provider = provider

    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True
    fields = provider.last_request.fields
    assert fields[0].options == ["3", "4", "5", "6"]
    assert fields[0].default == "5"
    assert fields[0].required is True
    assert fields[1].required is False
    assert fields[1].description == "123"


def test_human_input_rejects_unknown_field_type() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: form
            type: human.input
            input:
              mode: form
              prompt: "Bad field"
              fields:
                - name: value
                  type: magical
    """

    errors = WorkflowCompiler().validate(WorkflowParser.parse(yaml_text))

    assert any(error.code == ErrorCodes.INPUT_VALIDATION and "unsupported type" in error.message for error in errors)
