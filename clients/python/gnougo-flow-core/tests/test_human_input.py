import asyncio

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


class FakeHumanInputProvider:
    async def request_input_async(self, request):
        await asyncio.sleep(0)
        return {"response": "approve"}


@pytest.mark.asyncio
async def test_human_input_step() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: ask_human
            type: human.input
            input:
              prompt: "Approve?"
              timeout_ms: 1000
        outputs:
          answer: "${data.steps.ask_human.response}"
    """

    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    engine = WorkflowEngine()
    engine.human_input_provider = FakeHumanInputProvider()

    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True
    assert result.outputs["answer"] == "approve"

