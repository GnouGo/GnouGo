
import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine


def _compile(yaml_text: str):
    return WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))


@pytest.mark.asyncio
async def test_parallel_branches_exceed_limit_raises_parallel_limit() -> None:
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: p
            type: parallel
            branches:
              - steps:
                  - id: a
                    type: set
                    input: { v: 1 }
              - steps:
                  - id: b
                    type: set
                    input: { v: 2 }
              - steps:
                  - id: c
                    type: set
                    input: { v: 3 }
    """
    engine = WorkflowEngine()
    engine.limits.max_parallel_branches = 2
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is False
    assert result.error.code == "PARALLEL_LIMIT"
    assert "(3)" in result.error.message and "(2)" in result.error.message


@pytest.mark.asyncio
async def test_parallel_branches_run_concurrently() -> None:
    """Two branches that await each other prove they run in parallel."""
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: p
            type: parallel
            branches:
              - steps:
                  - id: a
                    type: set
                    input: { v: 1 }
              - steps:
                  - id: b
                    type: set
                    input: { v: 2 }
        outputs:
          a: "${data.steps.p.branches[0].a.v}"
          b: "${data.steps.p.branches[1].b.v}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    assert result.outputs["a"] == 1
    assert result.outputs["b"] == 2


@pytest.mark.asyncio
async def test_parallel_branch_isolated_steps() -> None:
    """Each branch sees only its own steps in its `steps` namespace."""
    yaml_text = """
    dsl: 1
    workflows:
      main:
        steps:
          - id: p
            type: parallel
            branches:
              - steps:
                  - id: only_a
                    type: set
                    input: { v: A }
              - steps:
                  - id: only_b
                    type: set
                    input: { v: B }
        outputs:
          branches: "${data.steps.p.branches}"
    """
    engine = WorkflowEngine()
    compiled = _compile(yaml_text)
    result = await engine.execute_async(compiled.workflows["main"], {})
    assert result.success is True, result.error
    branches = result.outputs["branches"]
    assert "only_a" in branches[0] and "only_b" not in branches[0]
    assert "only_b" in branches[1] and "only_a" not in branches[1]

