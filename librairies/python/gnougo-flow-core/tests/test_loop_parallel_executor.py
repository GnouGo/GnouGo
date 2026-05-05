import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import WorkflowRuntimeException
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import StepExecutionContext, StepExecutorRegistry, WorkflowEngine


class ThrowingStepExecutor:
    step_type = "fail.step"

    async def execute_async(self, ctx: StepExecutionContext):
        raise WorkflowRuntimeException("MCP_TIMEOUT", "simulated timeout", retryable=True)


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


@pytest.mark.asyncio
async def test_loop_parallel_on_error_object_set_output_can_use_error_and_item_context() -> None:
    yaml_text = '''
    version: 1
    workflows:
      main:
        steps:
          - id: fetch_pages
            type: loop.parallel
            input:
              items:
                - url: https://slimfaas.dev/docs
              max_concurrency: 1
            item_var: item
            index_var: idx
            steps:
              - id: fetch_page
                type: fail.step
                on_error:
                  cases:
                    - if: '${error.code == "MCP_TIMEOUT"}'
                      action: continue
                      set_output:
                        status: error
                        response:
                          url: "${data.item.url}"
                          error_code: "${error.code}"
                          error_message: "${error.message}"
    '''
    registry = StepExecutorRegistry()
    from gnougo_flow_core.runtime_steps import LoopParallelExecutor

    registry.register(LoopParallelExecutor())
    registry.register(ThrowingStepExecutor())

    engine = WorkflowEngine(registry)
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})

    assert result.success is True, result.error
    fetch_pages = result.outputs["fetch_pages"]
    fetch_page = fetch_pages["results"][0]["fetch_page"]
    response = fetch_page["response"]

    assert fetch_page["status"] == "error"
    assert response["url"] == "https://slimfaas.dev/docs"
    assert response["error_code"] == "MCP_TIMEOUT"
    assert response["error_message"] == "simulated timeout"


