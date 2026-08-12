from __future__ import annotations

import asyncio

import pytest

from gnougo_flow_core.checkpointing import InMemoryWorkflowCheckpointer
from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import WorkflowCheckpoint
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import StepExecutionContext, WorkflowEngine


def _compile(yaml_text: str):
    document = WorkflowCompiler().compile(WorkflowParser.parse(yaml_text))
    return document.workflows[document.entrypoint]


class _WaitExecutor:
    step_type = "test.wait"

    async def execute_async(self, ctx: StepExecutionContext):
        del ctx
        await asyncio.Event().wait()


@pytest.mark.asyncio
async def test_finally_runs_after_success_and_outputs_are_evaluated_after_cleanup() -> None:
    workflow = _compile(
        """
        version: 1
        workflows:
          main:
            inputs:
              resource: string
            steps:
              - id: allocate
                type: set
                input: {value: "${data.inputs.resource}"}
            finally:
              - id: cleanup
                type: set
                input:
                  value: "${data.steps.allocate.value}"
                  had_error: "${data.workflow_error != null}"
            outputs:
              cleaned: "${data.steps.cleanup.value}"
              had_error: "${data.steps.cleanup.had_error}"
        """
    )

    result = await WorkflowEngine().execute_async(workflow, {"resource": "lease-1"})

    assert result.success
    assert result.outputs == {"cleaned": "lease-1", "had_error": False}


@pytest.mark.asyncio
async def test_finally_receives_primary_error_and_preserves_it_when_cleanup_fails() -> None:
    workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: primary_failure
                type: assert.non_null
                input: {value: null}
            finally:
              - id: capture
                type: set
                input: {error_code: "${data.workflow_error.code}"}
              - id: cleanup_failure
                type: assert.non_null
                input: {value: null}
        """
    )

    result = await WorkflowEngine().execute_async(workflow, {})

    assert not result.success
    assert result.error is not None
    assert result.error.code == "INPUT_VALIDATION"
    assert next(step for step in result.step_results if step.step_id == "capture").output == {
        "error_code": "INPUT_VALIDATION"
    }
    errors = result.error.details["finalization_errors"]
    assert errors[0]["code"] == "INPUT_VALIDATION"


@pytest.mark.asyncio
async def test_finally_uses_independent_cancellation_and_step_budget() -> None:
    workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: skipped
                type: set
                input: {value: main}
            finally:
              - id: cleanup
                type: set
                input: {error_code: "${data.workflow_error.code}"}
        """
    )
    cancellation = asyncio.Event()
    cancellation.set()
    engine = WorkflowEngine()
    engine.limits.max_total_steps_executed = 1
    engine.limits.max_finalization_steps = 1

    result = await engine.execute_async(workflow, {}, ct=cancellation)

    assert not result.success
    assert result.error is not None and result.error.code == "CANCELLED"
    assert next(step for step in result.step_results if step.step_id == "cleanup").output == {
        "error_code": "CANCELLED"
    }


@pytest.mark.asyncio
async def test_finally_budget_includes_nested_body_and_nested_finalizer() -> None:
    workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: main_step
                type: set
                input: {value: main}
            finally:
              - id: cleanup_call
                type: workflow.call
                input:
                  ref: {kind: local, name: cleanup}
          cleanup:
            steps:
              - id: cleanup_body
                type: set
                input: {value: cleaned}
            finally:
              - id: cleanup_tail
                type: set
                input: {value: finalized}
        """
    )
    engine = WorkflowEngine()
    engine.limits.max_finalization_steps = 2

    result = await engine.execute_async(workflow, {})

    assert not result.success
    assert result.error is not None and result.error.code == "WORKFLOW_FINALIZATION_FAILED"


@pytest.mark.asyncio
async def test_finally_timeout_is_classified_and_nested_outputs_see_child_finalizer() -> None:
    timeout_workflow = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: done
                type: set
                input: {value: main}
            finally:
              - id: wait_forever
                type: test.wait
        """
    )
    engine = WorkflowEngine()
    engine.registry.register(_WaitExecutor())
    engine.limits.finalization_timeout_seconds = 1

    timeout_result = await engine.execute_async(timeout_workflow, {})

    assert not timeout_result.success
    assert timeout_result.error is not None
    assert timeout_result.error.code == "WORKFLOW_FINALIZATION_FAILED"
    assert timeout_result.error.details["finalization_errors"][0]["code"] == "WORKFLOW_FINALIZATION_TIMEOUT"

    nested = _compile(
        """
        version: 1
        workflows:
          main:
            steps:
              - id: call_child
                type: workflow.call
                input:
                  ref: {kind: local, name: child}
            outputs:
              cleaned: "${data.steps.call_child.outputs.cleaned}"
          child:
            steps:
              - id: allocate
                type: set
                input: {resource: lease-2}
            finally:
              - id: cleanup
                type: set
                input: {resource: "${data.steps.allocate.resource}"}
            outputs:
              cleaned: "${data.steps.cleanup.resource}"
        """
    )

    nested_result = await WorkflowEngine().execute_async(nested, {})

    assert nested_result.success
    assert nested_result.outputs == {"cleaned": "lease-2"}


@pytest.mark.asyncio
async def test_finally_runs_when_checkpointed_workflow_resumes() -> None:
    yaml_text = """
    version: 1
    workflows:
      main:
        steps:
          - id: allocate
            type: set
            input: {resource: lease-3}
          - id: use
            type: set
            input: {resource: "${data.steps.allocate.resource}"}
        finally:
          - id: cleanup
            type: set
            input: {resource: "${data.steps.allocate.resource}"}
    """
    workflow = _compile(yaml_text)
    checkpointer = InMemoryWorkflowCheckpointer()
    await checkpointer.save_async(
        WorkflowCheckpoint(
            run_id="finalization-resume",
            workflow_name="main",
            workflow_yaml=yaml_text,
            next_step_index=1,
            step_outputs={"allocate": {"resource": "lease-3"}},
            inputs={},
            status="paused",
        )
    )
    engine = WorkflowEngine()
    engine.checkpointer = checkpointer

    result = await engine.resume_async("finalization-resume", workflow)

    assert result.success
    checkpoint = await checkpointer.load_async("finalization-resume")
    assert checkpoint is not None
    assert checkpoint.status == "completed"
    assert checkpoint.step_outputs["cleanup"] == {"resource": "lease-3"}
