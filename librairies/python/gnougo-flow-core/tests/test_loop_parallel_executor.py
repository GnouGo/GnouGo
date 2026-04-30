import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def _compile(yaml_text: str):
    return WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))


@pytest.mark.asyncio
async def test_loop_parallel_items_basic() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: fanout
            type: loop.parallel
            item_var: x
            index_var: i
            input:
              items: [10, 20, 30]
            steps:
              - id: doubled
                type: set
                input: { v: "${x}", idx: "${i}" }
        outputs:
          count: "${data.steps.fanout.count}"
          results: "${data.steps.fanout.results}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["count"] == 3
    results = result.outputs["results"]
    assert [r["doubled"]["v"] for r in results] == [10, 20, 30]
    assert [r["doubled"]["idx"] for r in results] == [0, 1, 2]


@pytest.mark.asyncio
async def test_loop_parallel_items_exceed_loop_limit() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: fanout
            type: loop.parallel
            input:
              items: [1, 2, 3, 4, 5]
            steps:
              - id: noop
                type: set
                input: { v: "${item}" }
    """
    engine = WorkflowEngine()
    engine.limits.max_loop_iterations = 3
    engine.limits.max_parallel_branches = 100
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "LOOP_LIMIT"
    assert "(5)" in result.error.message and "(3)" in result.error.message


@pytest.mark.asyncio
async def test_loop_parallel_items_exceed_parallel_limit() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: fanout
            type: loop.parallel
            input:
              items: [1, 2, 3, 4, 5]
            steps:
              - id: noop
                type: set
                input: { v: "${item}" }
    """
    engine = WorkflowEngine()
    engine.limits.max_loop_iterations = 100
    engine.limits.max_parallel_branches = 2
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "PARALLEL_LIMIT"
    assert "(5)" in result.error.message and "(2)" in result.error.message


@pytest.mark.asyncio
async def test_loop_parallel_strips_dunder_keys() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: fanout
            type: loop.parallel
            input:
              items: [1]
            steps:
              - id: only_step
                type: set
                input: { v: "${item}" }
        outputs:
          first: "${data.steps.fanout.results[0]}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    first = result.outputs["first"]
    assert all(not (k.startswith("__") and k.endswith("__")) for k in first.keys())
    assert "only_step" in first

