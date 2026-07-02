from __future__ import annotations

import copy
from typing import Any

from gnougo_flow_core.errors import ErrorCodes, WorkflowRuntimeException
from gnougo_flow_core.runtime import StepExecutionContext

from .workflow_plan.auto_mode import _WorkflowPlanAutoModeMixin
from .workflow_plan.common import _WorkflowPlanCommonMixin
from .workflow_plan.mcp_discovery import _WorkflowPlanMcpDiscoveryMixin
from .workflow_plan.mcp_prefilter import _WorkflowPlanMcpPrefilterMixin
from .workflow_plan.pipeline_assembly import _WorkflowPlanPipelineAssemblyMixin
from .workflow_plan.pipeline_core import _WorkflowPlanPipelineCoreMixin
from .workflow_plan.pipeline_extraction import _WorkflowPlanPipelineExtractionMixin
from .workflow_plan.pipeline_generation import _WorkflowPlanPipelineGenerationMixin
from .workflow_plan.pipeline_reporting import _WorkflowPlanPipelineReportingMixin
from .workflow_plan.policy import _WorkflowPlanPolicyMixin
from .workflow_plan.repair_mode import _WorkflowPlanRepairModeMixin
from .workflow_plan.repair_prompt import _WorkflowPlanRepairPromptMixin
from .workflow_plan.single_plan import _WorkflowPlanSinglePlanMixin
from .workflow_plan.telemetry import _WorkflowPlanTelemetryMixin
from .workflow_plan.validation import _WorkflowPlanValidationMixin


class WorkflowPlanExecutor(
    _WorkflowPlanCommonMixin,
    _WorkflowPlanAutoModeMixin,
    _WorkflowPlanRepairModeMixin,
    _WorkflowPlanSinglePlanMixin,
    _WorkflowPlanPipelineCoreMixin,
    _WorkflowPlanPipelineExtractionMixin,
    _WorkflowPlanPipelineGenerationMixin,
    _WorkflowPlanPipelineAssemblyMixin,
    _WorkflowPlanPipelineReportingMixin,
    _WorkflowPlanMcpDiscoveryMixin,
    _WorkflowPlanMcpPrefilterMixin,
    _WorkflowPlanRepairPromptMixin,
    _WorkflowPlanTelemetryMixin,
    _WorkflowPlanValidationMixin,
    _WorkflowPlanPolicyMixin,
):
    step_type = "workflow.plan"
    _AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD = 10
    step_description = "Generate a YAML workflow dynamically under policy/limits."
    dsl_snippet = """
### workflow.plan - Generate a workflow YAML dynamically
```yaml
- id: generate
  type: workflow.plan
  input:
    mode: auto
    generator:
      model: gpt-4o
      instruction: |
        Build a workflow named generated that solves this task:
        ${data.inputs.task}
    policy:
      denied_step_types: [workflow.plan]
      allow_remote_workflow_refs: false
    limits:
      max_steps_total: 20
    validate:
      compile: true
      dry_run: true
    on_invalid:
      action: reprompt
      max_attempts: 3
```

Repair an existing workflow:
```yaml
- id: repair
  type: workflow.plan
  input:
    mode: repair
    generator:
      model: gpt-4o
      reasoning: medium
      prefilter: true
    repair:
      existing_yaml: "${data.inputs.workflow_yaml}"
      prompt: "Fix only the failed output mapping."
      failed_input: "${data.inputs.failed_prompt}"
      error:
        message: "Runtime error message."
    on_invalid:
      max_attempts: 3
```
Output: `{ workflow, yaml, meta, diagnostics }`.
"""
    documented_exceptions = [
        (ErrorCodes.INPUT_VALIDATION, False, "workflow.plan input/generator is malformed."),
        (ErrorCodes.TEMPLATE_PLAN, False, "planning LLM output could not be validated."),
        (ErrorCodes.TEMPLATE_POLICY, False, "planned workflow violates policy/limits."),
        (ErrorCodes.WORKFLOW_FETCH_POLICY, False, "planned workflow uses a remote workflow reference forbidden by policy."),
    ]
    _MCP_INPUT_CONTRACT_CHECKLIST = [
        "1. Inspect every MCP tool used by this workflow.",
        "2. For each required MCP argument, ensure the workflow has a matching input or a previous step that produces it.",
        "3. If a required MCP argument is missing, add it to skill.inputs and workflow.inputs with the exact MCP schema type.",
        "4. Never satisfy a missing required MCP argument with data.env.*, empty string, fake values, or casts.",
        "5. Never convert a string input to a number just to satisfy an MCP schema.",
        "6. Follow the discovered MCP schema and tool description exactly without adding Flow-specific request conventions.",
        "7. Prefer the exact MCP argument name and type.",
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.plan input must be object")

        generator = input_obj.get("generator") if isinstance(input_obj.get("generator"), dict) else {}
        mode = input_obj.get("mode") or generator.get("mode")
        normalized_mode = mode.strip().lower() if isinstance(mode, str) else None
        if normalized_mode == "repair":
            result = await self._execute_repair_plan_async(ctx, copy.deepcopy(input_obj))
            self._attach_plan_mode_metadata(result, "repair", None)
            return result
        if normalized_mode == "pipeline":
            result = await self._execute_pipeline_async(ctx, copy.deepcopy(input_obj))
            self._attach_plan_mode_metadata(result, "pipeline", None)
            return result
        if normalized_mode == "basic":
            result = await self._execute_single_plan_async(ctx, input_obj)
            self._attach_plan_mode_metadata(result, "basic", None)
            return result
        if normalized_mode and normalized_mode != "auto":
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"workflow.plan mode '{mode}' is not supported. Use auto, basic, pipeline, or repair.")

        selection = await self._classify_plan_mode_async(ctx, input_obj)
        if selection.selected_mode == "pipeline":
            result = await self._execute_pipeline_async(ctx, copy.deepcopy(input_obj))
            self._attach_plan_mode_metadata(result, "pipeline", selection)
            return result

        result = await self._execute_single_plan_async(ctx, input_obj)
        self._attach_plan_mode_metadata(result, "basic", selection)
        return result
