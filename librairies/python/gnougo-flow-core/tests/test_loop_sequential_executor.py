import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def _compile(yaml_text: str):
    return WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))


@pytest.mark.asyncio
async def test_loop_times_basic_exposes_index() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: loop
            type: loop.sequential
            input:
              times: 3
            steps:
              - id: pass
                type: set
                input: { idx: "${data._loop.index}" }
        outputs:
          count: "${data.steps.loop.count}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["count"] == 3


@pytest.mark.asyncio
async def test_loop_while_breaks_when_false() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: loop
            type: loop.sequential
            input:
              while: "${data._loop.index < 2}"
              max_times: 10
            steps:
              - id: pass
                type: set
                input: { v: "${data._loop.index}" }
        outputs:
          count: "${data.steps.loop.count}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["count"] == 2


@pytest.mark.asyncio
async def test_loop_while_hits_loop_limit() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: loop
            type: loop.sequential
            input:
              while: "${true}"
              max_times: 4
            steps:
              - id: noop
                type: set
                input: { x: 1 }
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "LOOP_LIMIT"
    assert "(4)" in result.error.message


@pytest.mark.asyncio
async def test_loop_over_with_item_and_index_vars() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: loop
            type: loop.sequential
            item_var: row
            index_var: i
            input:
              over: ["a", "b", "c"]
            steps:
              - id: handle
                type: set
                input: { value: "${row}", idx: "${i}" }
        outputs:
          count: "${data.steps.loop.count}"
          last_value: "${data.steps.handle.value}"
          last_idx: "${data.steps.handle.idx}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["count"] == 3
    assert result.outputs["last_value"] == "c"
    assert result.outputs["last_idx"] == 2


@pytest.mark.asyncio
async def test_loop_over_and_times_combined_raises() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: loop
            type: loop.sequential
            input:
              times: 2
              over: ["a"]
            steps:
              - id: noop
                type: set
                input: { v: 1 }
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "INPUT_VALIDATION"
    assert "mutually exclusive" in result.error.message

