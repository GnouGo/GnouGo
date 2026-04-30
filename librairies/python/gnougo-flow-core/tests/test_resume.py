from __future__ import annotations

import pytest

from gnougo_flow_core.checkpointing import InMemoryWorkflowCheckpointer
from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.errors import WorkflowRuntimeException
from gnougo_flow_core.models import WorkflowCheckpoint
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine

WORKFLOW_YAML = """
version: 1
name: checkpoint-demo
workflows:
  main:
    inputs:
      name:
        type: string
        required: false
        default: Alice
    steps:
      - id: first
        type: set
        input:
          greeting: "Hello ${data.inputs.name}"
      - id: second
        type: set
        input:
          final: "${data.steps.first.greeting}!"
    outputs:
      answer: "${data.steps.second.final}"
"""


def _compile_main(yaml_text: str = WORKFLOW_YAML):
    compiled = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return compiled.workflows[compiled.entrypoint]


@pytest.mark.asyncio
async def test_execute_async_saves_checkpoint_after_each_successful_top_level_step() -> None:
    workflow = _compile_main()
    checkpointer = InMemoryWorkflowCheckpointer()
    engine = WorkflowEngine()
    engine.checkpointer = checkpointer
    engine.limits.run_id = "run-1"

    result = await engine.execute_async(workflow, {})

    assert result.success
    checkpoint = await checkpointer.load_async("run-1")
    assert checkpoint is not None
    assert checkpoint.run_id == "run-1"
    assert checkpoint.workflow_name == "checkpoint-demo"
    assert checkpoint.next_step_index == 2
    assert checkpoint.status == "running"
    assert checkpoint.inputs == {"name": "Alice"}
    assert checkpoint.step_outputs["first"]["greeting"] == "Hello Alice"
    assert checkpoint.step_outputs["second"]["final"] == "Hello Alice!"
    assert checkpoint.workflow_yaml.strip().startswith("version: 1")
    assert checkpoint.timestamp


@pytest.mark.asyncio
async def test_resume_async_continues_from_checkpoint_and_marks_completed() -> None:
    workflow = _compile_main()
    checkpointer = InMemoryWorkflowCheckpointer()
    await checkpointer.save_async(
        WorkflowCheckpoint(
            run_id="run-2",
            workflow_name="checkpoint-demo",
            next_step_index=1,
            step_outputs={"first": {"greeting": "Hello Bob"}},
            inputs={"name": "Bob"},
            workflow_yaml=WORKFLOW_YAML,
            status="running",
        )
    )
    engine = WorkflowEngine()
    engine.checkpointer = checkpointer

    result = await engine.resume_async("run-2", workflow)

    assert result.success
    assert result.outputs == {"answer": "Hello Bob!"}
    assert [step.step_id for step in result.step_results] == ["second"]
    checkpoint = await checkpointer.load_async("run-2")
    assert checkpoint is not None
    assert checkpoint.status == "completed"
    assert checkpoint.step_outputs["second"]["final"] == "Hello Bob!"


@pytest.mark.asyncio
async def test_resume_async_marks_failed_when_remaining_step_fails() -> None:
    workflow = _compile_main(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: done
                type: set
                input:
                  ok: true
              - id: bad
                type: missing.step
                input: {}
        """
    )
    checkpointer = InMemoryWorkflowCheckpointer()
    await checkpointer.save_async(
        WorkflowCheckpoint(
            run_id="run-3",
            workflow_name="main",
            next_step_index=1,
            step_outputs={"done": {"ok": True}},
            inputs={},
            status="running",
        )
    )
    engine = WorkflowEngine()
    engine.checkpointer = checkpointer

    result = await engine.resume_async("run-3", workflow)

    assert not result.success
    assert result.error is not None
    assert result.error.code == "STEP_TYPE_UNKNOWN"
    checkpoint = await checkpointer.load_async("run-3")
    assert checkpoint is not None
    assert checkpoint.status == "failed"


@pytest.mark.asyncio
async def test_resume_async_requires_checkpointer_and_existing_checkpoint() -> None:
    workflow = _compile_main()
    engine = WorkflowEngine()
    with pytest.raises(RuntimeError):
        await engine.resume_async("missing", workflow)

    engine.checkpointer = InMemoryWorkflowCheckpointer()
    with pytest.raises(WorkflowRuntimeException) as exc:
        await engine.resume_async("missing", workflow)
    assert exc.value.code == "CHECKPOINT_NOT_FOUND"


@pytest.mark.asyncio
async def test_in_memory_checkpointer_lists_filters_and_isolates_copies() -> None:
    checkpointer = InMemoryWorkflowCheckpointer()
    checkpoint = WorkflowCheckpoint(
        run_id="run-4",
        workflow_name="wf",
        step_outputs={"a": {"value": 1}},
        tenant_id="tenant-a",
        status="paused",
    )
    await checkpointer.save_async(checkpoint)
    checkpoint.step_outputs["a"]["value"] = 2

    loaded = await checkpointer.load_async("run-4")
    assert loaded is not None
    assert loaded.step_outputs["a"]["value"] == 1
    loaded.step_outputs["a"]["value"] = 3
    loaded_again = await checkpointer.load_async("run-4")
    assert loaded_again is not None
    assert loaded_again.step_outputs["a"]["value"] == 1

    assert [cp.run_id for cp in await checkpointer.list_async(tenant_id="tenant-a")] == ["run-4"]
    assert [cp.run_id for cp in await checkpointer.list_async(status="paused")] == ["run-4"]
    assert await checkpointer.list_async(tenant_id="tenant-b") == []

    await checkpointer.delete_async("run-4")
    assert await checkpointer.load_async("run-4") is None

