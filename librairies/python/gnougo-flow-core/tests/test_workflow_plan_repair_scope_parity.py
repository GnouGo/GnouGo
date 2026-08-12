from __future__ import annotations

import textwrap

import pytest

from gnougo_flow_core.compilation import WorkflowCompiler
from gnougo_flow_core.models import LLMResponse
from gnougo_flow_core.parsing import WorkflowParser
from gnougo_flow_core.runtime import WorkflowEngine

EXISTING = """\
version: 1
name: repair-target
skill:
  description: Preserve the repair topology.
  tags: [repair]
  inputs: {}
  outputs: {}
workflows:
  main:
    steps:
      - id: target
        type: set
        input: {value: old}
      - id: consumer
        type: set
        input: {value: "${data.steps.target.value}"}
      - id: unrelated
        type: set
        input: {value: stable}
"""


class _RepairLlm:
    def __init__(self, repaired: str) -> None:
        self.repaired = repaired

    async def call_async(self, request):
        return LLMResponse(text=self.repaired)


def _repair_workflow():
    existing = textwrap.indent(EXISTING, " " * 24)
    source = f"""
    version: 1
    workflows:
      main:
        steps:
          - id: repair
            type: workflow.plan
            input:
              mode: repair
              generator:
                model: fake
                prefilter: false
              repair:
                existing_yaml: |
{existing}
                prompt: Correct the target value.
                scope:
                  workflow: main
                  step_id: target
              validate:
                compile: true
              on_invalid:
                max_attempts: 1
    """
    document = WorkflowCompiler().compile(WorkflowParser.parse(source))
    return document.workflows["main"]


@pytest.mark.asyncio
async def test_surgical_repair_allows_target_and_direct_consumer_only() -> None:
    repaired = EXISTING.replace("{value: old}", "{value: corrected}").replace(
        '{value: "${data.steps.target.value}"}',
        '{value: "fixed ${data.steps.target.value}"}',
    )
    engine = WorkflowEngine()
    engine.llm_client = _RepairLlm(repaired)

    result = await engine.execute_async(_repair_workflow(), {})

    assert result.success, result.error
    assert "value: corrected" in result.outputs["repair"]["yaml"]
    assert result.outputs["repair"]["meta"]["repair"] == {
        "has_prompt": True,
        "has_error": False,
    }


@pytest.mark.asyncio
async def test_surgical_repair_rejects_unrelated_changes_and_topology_changes() -> None:
    repaired = EXISTING.replace("{value: old}", "{value: corrected}").replace(
        "{value: stable}", "{value: changed-outside-scope}"
    )
    engine = WorkflowEngine()
    engine.llm_client = _RepairLlm(repaired)

    result = await engine.execute_async(_repair_workflow(), {})

    assert not result.success
    assert result.error is not None and result.error.code == "TEMPLATE_PLAN"
    assert "unrelated" in result.error.message


@pytest.mark.asyncio
async def test_repair_scope_requires_workflow_and_step_id_together() -> None:
    existing = textwrap.indent(EXISTING, " " * 24)
    source = f"""
    version: 1
    workflows:
      main:
        steps:
          - id: repair
            type: workflow.plan
            input:
              mode: repair
              generator: {{model: fake}}
              repair:
                existing_yaml: |
{existing}
                prompt: Fix it.
                scope: {{workflow: main}}
    """
    workflow = WorkflowCompiler().compile(WorkflowParser.parse(source)).workflows["main"]

    engine = WorkflowEngine()
    engine.llm_client = _RepairLlm(EXISTING)

    result = await engine.execute_async(workflow, {})

    assert not result.success
    assert result.error is not None and result.error.code == "INPUT_VALIDATION"
    assert "both workflow and step_id" in result.error.message
