from __future__ import annotations

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import ErrorCodes
from gnougo_flow_core.models import LLMResponse
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


class _Llm:
    def __init__(self, response: LLMResponse) -> None:
        self.response = response

    async def call_async(self, request):
        return self.response


def _compile_main(yaml_text: str):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


@pytest.mark.asyncio
async def test_llm_call_structured_output_parses_text_json() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: classify
            type: llm.call
            input:
              model: gpt-4
              prompt: classify
              structured_output:
                schema_inline:
                  type: object
                  properties:
                    category: { type: string }
                  required: [category]
                  additionalProperties: false
                strict: true
        outputs:
          category: "${data.steps.classify.json.category}"
    """
    engine = WorkflowEngine()
    engine.llm_client = _Llm(LLMResponse(text='{"category":"billing"}'))

    result = await engine.execute_async(_compile_main(source), {})

    assert result.success
    assert result.outputs["category"] == "billing"


@pytest.mark.asyncio
async def test_llm_call_structured_output_mismatch_raises_llm_schema() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: classify
            type: llm.call
            input:
              model: gpt-4
              prompt: classify
              structured_output:
                schema_inline:
                  type: object
                  properties:
                    category: { type: string }
                  required: [category]
                  additionalProperties: false
                strict: true
    """
    engine = WorkflowEngine()
    engine.llm_client = _Llm(LLMResponse(text='{"priority":"high"}'))

    result = await engine.execute_async(_compile_main(source), {})

    assert not result.success
    assert result.error.code == ErrorCodes.LLM_SCHEMA


@pytest.mark.asyncio
async def test_llm_call_structured_output_invalid_json_raises_llm_schema() -> None:
    source = """
    version: 1
    workflows:
      main:
        steps:
          - id: classify
            type: llm.call
            input:
              model: gpt-4
              prompt: classify
              structured_output:
                schema_inline:
                  type: object
                  required: [category]
    """
    engine = WorkflowEngine()
    engine.llm_client = _Llm(LLMResponse(text="not json"))

    result = await engine.execute_async(_compile_main(source), {})

    assert not result.success
    assert result.error.code == ErrorCodes.LLM_SCHEMA
