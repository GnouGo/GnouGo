from __future__ import annotations

import asyncio
import copy
import json
import re
import textwrap
from dataclasses import dataclass, field
from typing import Any

import yaml

from gnougo_flow_core.compilation import ValidationError, WorkflowValidator
from gnougo_flow_core.mcp_cache import cache_prompts, cache_resources, cache_tools, get_cached_prompts, get_cached_resources, get_cached_tools
from gnougo_flow_core.models import InputDef, OutputDef, StepDef, WorkflowDef, WorkflowDocument
from gnougo_flow_core.runtime import *  # noqa: F401,F403
from gnougo_flow_core.workflow_plan_contract_normalizer import (
    collect_weak_output_schema_diagnostics,
    is_weak_yaml_output_schema,
)
from gnougo_flow_core.workflow_plan_diagnostics import (
    build_exception_details,
    build_mcp_discovery_coverage_details,
    build_structured_plan_error,
    build_validation_failure_details,
    format_validation_errors,
    infer_plan_error_code,
    to_prompt_json,
)
from gnougo_flow_core.workflow_plan_dry_run_validator import validate_workflow_plan_dry_run
from gnougo_flow_core.workflow_plan_pipeline_quality_analyzer import (
    analyze_external_artifact_readiness,
    build_main_dataflow_quality_details,
    validate_external_artifact_readiness,
)
from gnougo_flow_core.workflow_plan_semantic_validator import (
    McpToolOutputContract,
    WorkflowSemanticValidationException,
    normalize_mcp_call_input_requests,
    validate_workflow_semantics,
)

_MCP_DISCOVERY_MAX_ATTEMPTS = 3
_MCP_DISCOVERY_RETRY_BASE_DELAY_SECONDS = 0.5


@dataclass(slots=True)
class _PipelinePlannedTool:
    server: str
    kind: str
    method: str
    required: bool = False
    purpose: str = ""
    consumes: list[str] = field(default_factory=list)
    produces: list[str] = field(default_factory=list)


@dataclass(slots=True)
class _WorkflowPipelineSubworkflowSpec:
    name: str
    goal: str
    inputs: dict[str, str]
    outputs: dict[str, str]
    extract_reason: str
    content: str
    generation_prompt: str
    description: str = ""
    work_kind: str = ""
    contract_role: str = ""
    concrete_outcome: str = ""
    input_schemas: dict[str, Any] = field(default_factory=dict)
    output_schemas: dict[str, Any] = field(default_factory=dict)
    planned_tools: list[_PipelinePlannedTool] = field(default_factory=list)
    required_capabilities: list[str] = field(default_factory=list)


@dataclass(slots=True)
class _WorkflowPipelineExtraction:
    subworkflows: list[_WorkflowPipelineSubworkflowSpec]
    main_workflow_prompt: str
    validation_errors: list[str]
    root_causes: list[dict[str, Any]] = field(default_factory=list)
    quality_review: dict[str, Any] | None = None


@dataclass(slots=True)
class _GeneratedLeafWorkflow:
    name: str
    workflow_name: str
    document: WorkflowDocument
    yaml_text: str
    workflow_node: dict[str, Any]
    spec: _WorkflowPipelineSubworkflowSpec | None = None


@dataclass(slots=True)
class _GeneratedMainAssembly:
    main_workflow_node: dict[str, Any]
    document_name: str | None = None
    skill_node: dict[str, Any] | None = None


@dataclass(slots=True)
class _WorkflowPlanModeSelection:
    selected_mode: str
    cyclomatic_complexity: int | None = None
    branch_count: int | None = None
    confidence: float | None = None
    reason: str | None = None
    used_fallback: bool = False
    raw_response: str | None = None


class _NoAliasDumper(yaml.SafeDumper):
    def ignore_aliases(self, data):
        return True


class WorkflowPlanExecutor:
    step_type = "workflow.plan"
    _AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD = 10
    step_description = "Generate a YAML workflow dynamically under policy/limits."
    dsl_snippet = """
### workflow.plan - Generate a workflow YAML dynamically
```yaml
- id: generate
  type: workflow.plan
  input:
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
        "6. Prefer the exact MCP argument name and type.",
    ]

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.plan input must be object")

        generator = input_obj.get("generator") if isinstance(input_obj.get("generator"), dict) else {}
        mode = input_obj.get("mode") or generator.get("mode")
        normalized_mode = mode.strip().lower() if isinstance(mode, str) else None
        if normalized_mode == "pipeline":
            result = await self._execute_pipeline_async(ctx, copy.deepcopy(input_obj))
            self._attach_plan_mode_metadata(result, "pipeline", None)
            return result
        if normalized_mode == "basic":
            result = await self._execute_single_plan_async(ctx, input_obj)
            self._attach_plan_mode_metadata(result, "basic", None)
            return result
        if normalized_mode and normalized_mode != "auto":
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, f"workflow.plan mode '{mode}' is not supported. Use auto, basic, or pipeline.")

        selection = await self._classify_plan_mode_async(ctx, input_obj)
        if selection.selected_mode == "pipeline":
            result = await self._execute_pipeline_async(ctx, copy.deepcopy(input_obj))
            self._attach_plan_mode_metadata(result, "pipeline", selection)
            return result

        result = await self._execute_single_plan_async(ctx, input_obj)
        self._attach_plan_mode_metadata(result, "basic", selection)
        return result

    async def _classify_plan_mode_async(self, ctx: StepExecutionContext, input_obj: dict[str, Any]) -> _WorkflowPlanModeSelection:
        generator = input_obj.get("generator") if isinstance(input_obj.get("generator"), dict) else {}
        provider, model = ctx.engine.resolve_llm_target(generator.get("provider"), generator.get("model"))
        model = model or "gpt-4"
        reasoning_raw = generator.get("reasoning")
        reasoning = reasoning_raw.strip() if isinstance(reasoning_raw, str) and reasoning_raw.strip() else "low"
        prompt = self._build_auto_mode_classification_prompt(input_obj)

        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            [
                ("gnougo-flow.thinking.message", "Classifying workflow planning complexity for auto mode."),
                ("gnougo-flow.thinking.level", "thinking"),
            ],
        )

        try:
            with ctx.begin_telemetry_span(
                "workflow.plan.classify_mode",
                "classification",
                [
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.system", provider or "unknown"),
                    ("gen_ai.request.model", model),
                    ("gnougo-flow.plan.mode.requested", "auto"),
                    ("gnougo-flow.plan.auto.threshold", self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD),
                ],
            ) as span:
                if ctx.limits.log_step_content:
                    span.add_event(
                        "gen_ai.content.prompt",
                        [
                            ("gen_ai.prompt", prompt),
                            ("prompt.role", "user"),
                            ("gnougo-flow.plan.phase", "classification"),
                        ],
                    )
                response = await ctx.engine.call_llm_async(
                    LLMRequest(
                        provider=provider,
                        model=model,
                        prompt=prompt,
                        reasoning=reasoning,
                        structured_output_strict=True,
                        structured_output_schema={
                            "type": "object",
                            "additionalProperties": False,
                            "required": ["mode", "cyclomatic_complexity", "branch_count", "confidence", "reason"],
                            "properties": {
                                "mode": {"type": "string", "enum": ["basic", "pipeline"]},
                                "cyclomatic_complexity": {"type": "integer", "minimum": 1},
                                "branch_count": {"type": "integer", "minimum": 0},
                                "confidence": {"type": "number", "minimum": 0, "maximum": 1},
                                "reason": {"type": "string"},
                            },
                        },
                    )
                )
                span.set_attribute("gen_ai.response.model", model)
                span.set_attribute("gen_ai.response.finish_reason", "stop")
                self._add_usage_attributes(span, response.usage)
                if ctx.limits.log_step_content and response.text:
                    span.add_event(
                        "gen_ai.content.completion",
                        [
                            ("gen_ai.completion", response.text),
                            ("completion.role", "assistant"),
                            ("completion.finish_reason", "stop"),
                            ("gnougo-flow.plan.phase", "classification"),
                        ],
                    )

                selection = self._parse_plan_mode_selection(response)
                span.set_attribute("gnougo-flow.plan.mode.selected", selection.selected_mode)
                if selection.cyclomatic_complexity is not None:
                    span.set_attribute("gnougo-flow.plan.auto.cyclomatic_complexity", selection.cyclomatic_complexity)
                if selection.branch_count is not None:
                    span.set_attribute("gnougo-flow.plan.auto.branch_count", selection.branch_count)
                if selection.confidence is not None:
                    span.set_attribute("gnougo-flow.plan.auto.confidence", selection.confidence)
                if selection.used_fallback:
                    span.set_attribute("gnougo-flow.plan.auto.fallback", True)

            ctx.set_telemetry_attribute("gnougo-flow.plan.mode", selection.selected_mode)
            ctx.set_telemetry_attribute("gnougo-flow.plan.mode.source", "auto_fallback" if selection.used_fallback else "auto")
            return selection
        except Exception:
            fallback = _WorkflowPlanModeSelection(
                selected_mode="basic",
                reason="Classifier failed or returned invalid JSON; defaulted to basic mode.",
                used_fallback=True,
            )
            ctx.set_telemetry_attribute("gnougo-flow.plan.mode", fallback.selected_mode)
            ctx.set_telemetry_attribute("gnougo-flow.plan.mode.source", "auto_fallback")
            return fallback

    def _build_auto_mode_classification_prompt(self, input_obj: dict[str, Any]) -> str:
        generator = input_obj.get("generator") if isinstance(input_obj.get("generator"), dict) else {}
        raw_prompt = str(input_obj.get("raw_prompt") or generator.get("raw_prompt") or "")
        instruction = str(generator.get("instruction") or "")
        context_text = str(generator.get("context") or "")
        policy = json.dumps(input_obj.get("policy") or {}, indent=2, sort_keys=True)
        limits = json.dumps(input_obj.get("limits") or {}, indent=2, sort_keys=True)
        threshold = self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD
        return (
            "You classify a GnOuGo workflow.plan request before workflow generation.\n"
            "Return ONLY JSON that matches the requested schema.\n\n"
            "Decision rule:\n"
            f'- Choose "basic" when estimated cyclomatic complexity is less than {threshold} branching points and the workflow can be generated coherently in one plan.\n'
            f'- Choose "pipeline" when estimated cyclomatic complexity is {threshold} or more, or when the request should be decomposed into many small leaf workflows before assembling a main workflow.\n'
            "- Count branching points from conditions, switch/case paths, loops, retries, error handling, cleanup paths, validation branches, tool-orchestration choices, and state transitions.\n"
            '- Prefer "pipeline" when several independent phases, tools, or responsibilities would make one generated workflow brittle.\n'
            '- Prefer "basic" for simple linear flows, small conditionals, or requests with fewer than 10 meaningful branches.\n\n'
            f"<raw_prompt>\n{raw_prompt}\n</raw_prompt>\n\n"
            f"<generator_instruction>\n{instruction}\n</generator_instruction>\n\n"
            f"<generator_context>\n{context_text}\n</generator_context>\n\n"
            f"<policy_json>\n{policy}\n</policy_json>\n\n"
            f"<limits_json>\n{limits}\n</limits_json>"
        )

    def _parse_plan_mode_selection(self, response: LLMResponse) -> _WorkflowPlanModeSelection:
        payload = response.json_payload if isinstance(response.json_payload, dict) else None
        if payload is None and response.text:
            try:
                payload = json.loads(self._strip_markdown_code_fence(response.text).strip())
            except Exception:
                payload = None
        if not isinstance(payload, dict):
            return _WorkflowPlanModeSelection(
                selected_mode="basic",
                reason="Classifier returned non-JSON content; defaulted to basic mode.",
                used_fallback=True,
                raw_response=response.text,
            )

        mode = str(payload.get("mode") or "").strip().lower()
        complexity = self._coerce_int(payload.get("cyclomatic_complexity"))
        branch_count = self._coerce_int(payload.get("branch_count"))
        confidence = self._coerce_float(payload.get("confidence"))
        if mode not in {"basic", "pipeline"} and complexity is None and branch_count is None:
            return _WorkflowPlanModeSelection(
                selected_mode="basic",
                reason="Classifier JSON did not include a mode or complexity signal; defaulted to basic mode.",
                used_fallback=True,
                raw_response=response.text,
            )
        selected_mode = (
            "pipeline"
            if mode == "pipeline"
            or (complexity is not None and complexity >= self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD)
            or (branch_count is not None and branch_count >= self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD)
            else "basic"
        )
        return _WorkflowPlanModeSelection(
            selected_mode=selected_mode,
            cyclomatic_complexity=complexity,
            branch_count=branch_count,
            confidence=confidence,
            reason=str(payload.get("reason") or ""),
            raw_response=response.text,
        )

    @staticmethod
    def _coerce_int(value: Any) -> int | None:
        if isinstance(value, bool) or value is None:
            return None
        if isinstance(value, int):
            return value
        if isinstance(value, float):
            return round(value)
        try:
            return int(str(value).strip())
        except Exception:
            return None

    @staticmethod
    def _coerce_float(value: Any) -> float | None:
        if isinstance(value, bool) or value is None:
            return None
        if isinstance(value, (int, float)):
            return float(value)
        try:
            return float(str(value).strip())
        except Exception:
            return None

    def _attach_plan_mode_metadata(self, result: Any, mode: str, selection: _WorkflowPlanModeSelection | None) -> None:
        if not isinstance(result, dict):
            return
        meta = result.setdefault("meta", {})
        if not isinstance(meta, dict):
            meta = {}
            result["meta"] = meta
        meta["mode"] = mode
        if selection is None:
            return
        mode_selection: dict[str, Any] = {
            "source": "auto_fallback" if selection.used_fallback else "auto",
            "selected_mode": selection.selected_mode,
            "threshold": self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD,
        }
        if selection.cyclomatic_complexity is not None:
            mode_selection["cyclomatic_complexity"] = selection.cyclomatic_complexity
        if selection.branch_count is not None:
            mode_selection["branch_count"] = selection.branch_count
        if selection.confidence is not None:
            mode_selection["confidence"] = selection.confidence
        if selection.reason:
            mode_selection["reason"] = selection.reason
        meta["mode_selection"] = mode_selection

    async def _execute_single_plan_async(self, ctx: StepExecutionContext, input_obj: dict[str, Any]) -> Any:
        client = ctx.engine.llm_client
        if client is None:
            raise WorkflowRuntimeException(ErrorCodes.LLM_NETWORK, "No LLM client configured")

        generator = input_obj.get("generator")
        if not isinstance(generator, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.plan requires 'generator'")
        instruction = str(generator.get("instruction", ""))

        provider, model = ctx.engine.resolve_llm_target(generator.get("provider"), generator.get("model"))
        model = model or "gpt-4"

        # Reasoning effort: workflow planning is reasoning-heavy, default to "medium".
        # Authors can override via `generator.reasoning: auto|minimal|low|medium|high|max`.
        plan_reasoning_raw = generator.get("reasoning")
        plan_reasoning = plan_reasoning_raw.strip() if isinstance(plan_reasoning_raw, str) and plan_reasoning_raw.strip() else "medium"

        policy = input_obj.get("policy") if isinstance(input_obj.get("policy"), dict) else {}
        limits = input_obj.get("limits") if isinstance(input_obj.get("limits"), dict) else {}
        validate = input_obj.get("validate") if isinstance(input_obj.get("validate"), dict) else {}
        on_invalid = input_obj.get("on_invalid") if isinstance(input_obj.get("on_invalid"), dict) else {}
        max_attempts = max(1, int(on_invalid.get("max_attempts", 3)))
        on_invalid_action = str(on_invalid.get("action", "fail"))
        context_text = str(generator.get("context", ""))

        prompt_mcp_tool_contracts: list[McpToolOutputContract] = []
        validation_mcp_tool_contracts: list[McpToolOutputContract] = []
        validation_mcp_server_metadata = self._get_configured_mcp_server_metadata(ctx)
        forced_mcp_server_names = self._extract_required_mcp_server_names(instruction, context_text, validation_mcp_server_metadata)
        needs_mcp_validation_contracts = bool(validate.get("compile", True)) or bool(validate.get("dry_run", False))
        if needs_mcp_validation_contracts and validation_mcp_server_metadata:
            validation_mcp_tool_contracts = await self._collect_mcp_tool_contracts(ctx, validation_mcp_server_metadata)

        base_prompt = await self._build_planning_prompt(
            ctx,
            instruction,
            context_text,
            policy,
            limits,
            generator,
            plan_reasoning,
            prompt_mcp_tool_contracts,
            forced_mcp_server_names,
        )
        prompt = base_prompt
        last_error: Exception | None = None
        last_invalid_yaml: str | None = None
        last_repair_context: str | None = None

        for attempt in range(1, max_attempts + 1):
            if last_error is not None:
                prompt = self._build_reprompt(
                    instruction,
                    context_text,
                    policy,
                    last_invalid_yaml,
                    last_error,
                    last_repair_context,
                )

            with ctx.begin_telemetry_span(
                "workflow.plan.generate",
                "generation",
                [
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.system", provider or "unknown"),
                    ("gen_ai.request.model", model),
                    ("gnougo-flow.plan.attempt", attempt),
                ],
            ) as generation_span:
                if ctx.limits.log_step_content:
                    generation_span.add_event(
                        "gen_ai.content.prompt",
                        [
                            ("gen_ai.prompt", prompt),
                            ("prompt.role", "user"),
                            ("gnougo-flow.plan.attempt", attempt),
                            ("gnougo-flow.plan.phase", "generation"),
                        ],
                    )
                response = await ctx.engine.call_llm_async(LLMRequest(provider=provider, model=model, prompt=prompt, reasoning=plan_reasoning))
                generation_span.set_attribute("gen_ai.response.model", model)
                generation_span.set_attribute("gen_ai.response.finish_reason", "stop")
                self._add_usage_attributes(generation_span, response.usage)
                if ctx.limits.log_step_content and response.text:
                    generation_span.add_event(
                        "gen_ai.content.completion",
                        [
                            ("gen_ai.completion", response.text),
                            ("completion.role", "assistant"),
                            ("completion.finish_reason", "stop"),
                            ("gnougo-flow.plan.attempt", attempt),
                            ("gnougo-flow.plan.phase", "generation"),
                        ],
                    )

            try:
                with ctx.begin_telemetry_span("workflow.plan.validate", "validation", [("gnougo-flow.plan.attempt", attempt)]) as validation_span:
                    yaml_text = self._strip_markdown_code_fence(textwrap.dedent(response.text).strip())
                    yaml_text = self._normalize_planned_yaml(yaml_text)
                    validation_span.set_attribute("gnougo-flow.plan.yaml_length", len(yaml_text))
                    doc = self._parse_and_validate_generated_workflow(yaml_text)
                    validation_span.set_attribute("gnougo-flow.plan.workflow_count", len(doc.workflows))
                    self._enforce_plan_policy(doc, policy, limits)
                    if bool(validate.get("compile", True)):
                        normalization_count = self._validate_generated_workflow_for_plan(
                            doc,
                            validation_mcp_tool_contracts,
                            validation_mcp_server_metadata,
                        )
                        if normalization_count > 0:
                            yaml_text = self._dump_workflow_yaml(doc)
                    elif bool(validate.get("dry_run", False)):
                        self._validate_mcp_discovery_coverage(
                            doc,
                            validation_mcp_tool_contracts,
                            validation_mcp_server_metadata,
                        )
                    if bool(validate.get("dry_run", False)):
                        validation_span.set_attribute("gnougo-flow.plan.dry_run", True)
                        await validate_workflow_plan_dry_run(
                            doc,
                            validation_mcp_tool_contracts,
                            validation_mcp_server_metadata,
                        )
                return {
                    "yaml": yaml_text,
                    "workflow": {
                        "version": doc.version,
                        "name": doc.name,
                        "workflows": list(doc.workflows.keys()),
                    },
                    "meta": {"model": model, "attempt": attempt},
                    "diagnostics": [],
                }
            except WorkflowRuntimeException as exc:
                if on_invalid_action != "reprompt" or attempt >= max_attempts:
                    raise
                last_error = exc
                last_invalid_yaml = self._strip_markdown_code_fence(textwrap.dedent(response.text).strip())
                last_repair_context = await self._build_repair_context_with_mcp_docs(
                    ctx,
                    policy,
                    last_invalid_yaml,
                    exc,
                    forced_mcp_server_names,
                    validation_mcp_tool_contracts,
                )
            except Exception as exc:
                last_error = exc
                if on_invalid_action != "reprompt" or attempt >= max_attempts:
                    break
                last_invalid_yaml = self._strip_markdown_code_fence(textwrap.dedent(response.text).strip())
                last_repair_context = await self._build_repair_context_with_mcp_docs(
                    ctx,
                    policy,
                    last_invalid_yaml,
                    exc,
                    forced_mcp_server_names,
                    validation_mcp_tool_contracts,
                )

        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"Failed to generate valid workflow after {max_attempts} attempts: {last_error or 'unknown error'}",
        )

    async def _execute_pipeline_async(self, ctx: StepExecutionContext, input_obj: dict[str, Any]) -> Any:
        generator = input_obj.get("generator") if isinstance(input_obj.get("generator"), dict) else {}
        raw_prompt = input_obj.get("raw_prompt") or generator.get("raw_prompt") or generator.get("instruction") or ""
        raw_prompt = str(raw_prompt)
        if not raw_prompt.strip():
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.plan pipeline mode requires 'raw_prompt' or generator.instruction")

        self._normalize_pipeline_main_policy(input_obj)

        provider, model = ctx.engine.resolve_llm_target(generator.get("provider"), generator.get("model"))
        model = model or "gpt-4"
        reasoning_raw = generator.get("reasoning")
        reasoning = reasoning_raw.strip() if isinstance(reasoning_raw, str) and reasoning_raw.strip() else "medium"

        ctx.set_telemetry_attribute("gnougo-flow.plan.mode", "pipeline")
        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            [
                ("gnougo-flow.thinking.message", "Preparing workflow generation prompt through pipeline mode."),
                ("gnougo-flow.thinking.level", "thinking"),
            ],
        )

        normalized_markdown = await self._normalize_user_prompt(ctx, raw_prompt, provider, model, reasoning)
        use_structured_extraction = await self._should_use_structured_pipeline_extraction(ctx, provider, model)
        pipeline_mcp_doc, pipeline_mcp_tool_contracts, pipeline_mcp_server_metadata = await self._build_pipeline_global_mcp_context(
            ctx,
            generator,
            normalized_markdown,
            raw_prompt,
            provider,
            model,
            reasoning,
        )
        annotated_markdown, extraction = await self._mark_and_extract_subworkflow_specs(
            ctx,
            normalized_markdown,
            input_obj,
            provider,
            model,
            reasoning,
            use_structured_extraction,
            pipeline_mcp_doc,
            pipeline_mcp_tool_contracts,
        )

        generated_leaves = [
            await self._generate_leaf_workflow_async(ctx, input_obj, generator, spec)
            for spec in extraction.subworkflows
        ]

        validate = input_obj.get("validate") if isinstance(input_obj.get("validate"), dict) else {}
        validation_mcp_server_metadata = pipeline_mcp_server_metadata or self._get_configured_mcp_server_metadata(ctx)
        validation_mcp_tool_contracts: list[McpToolOutputContract] = list(pipeline_mcp_tool_contracts)
        if (bool(validate.get("compile", True)) or bool(validate.get("dry_run", False))) and validation_mcp_server_metadata:
            if not validation_mcp_tool_contracts:
                validation_mcp_tool_contracts = await self._collect_mcp_tool_contracts(ctx, validation_mcp_server_metadata)

        configured_main_inputs = self._build_configured_main_input_contract(input_obj, generator)
        generated_leaf_inputs = self._build_generated_main_input_contract(generated_leaves)
        base_prompt = self._build_main_assembly_prompt(
            input_obj,
            generator,
            normalized_markdown,
            extraction,
            generated_leaves,
            configured_main_inputs,
            generated_leaf_inputs,
        )
        max_attempts = self._get_pipeline_generation_max_attempts(input_obj)
        previous_response: str | None = None
        previous_error: str | None = None
        last_error: Exception | None = None
        final_yaml: str | None = None
        final_doc: WorkflowDocument | None = None
        main_retry_count = 0

        for attempt in range(1, max_attempts + 1):
            prompt = base_prompt if previous_error is None else self._build_main_assembly_repair_prompt(base_prompt, previous_response, previous_error)
            try:
                response = await ctx.engine.call_llm_async(
                    LLMRequest(
                        provider=provider,
                        model=model,
                        prompt=prompt,
                        reasoning=reasoning,
                        use_background_mode=True,
                    )
                )
                previous_response = response.text
                assembly = self._parse_generated_main_assembly(response.text or "")
                main_inputs = self._resolve_main_input_contract(configured_main_inputs, assembly, generated_leaf_inputs)
                assembly.main_workflow_node["inputs"] = copy.deepcopy(main_inputs)
                self._ensure_main_workflow_outputs(assembly.main_workflow_node, extraction.subworkflows)
                self._validate_declared_main_input_references(assembly.main_workflow_node, main_inputs)

                candidate_yaml = self._compose_pipeline_workflow_yaml(input_obj, generator, extraction, generated_leaves, assembly, main_inputs)
                candidate_doc = self._parse_and_validate_generated_workflow(candidate_yaml)
                self._enforce_pipeline_workflow_hierarchy(candidate_doc, {leaf.name for leaf in generated_leaves})
                self._validate_pipeline_leaf_call_arguments(candidate_doc, generated_leaves)
                self._validate_pipeline_main_graph_boundaries(candidate_doc)
                self._validate_pipeline_main_leaf_output_contracts(candidate_doc, generated_leaves)
                self._validate_pipeline_main_dataflow_quality(candidate_doc)
                self._run_standard_plan_validation_sequence(
                    candidate_doc,
                    input_obj.get("policy") if isinstance(input_obj.get("policy"), dict) else {},
                    input_obj.get("limits") if isinstance(input_obj.get("limits"), dict) else {},
                    validate,
                    validation_mcp_tool_contracts,
                    validation_mcp_server_metadata,
                )
                if bool(validate.get("dry_run", False)):
                    await validate_workflow_plan_dry_run(candidate_doc, validation_mcp_tool_contracts, validation_mcp_server_metadata)

                final_yaml = candidate_yaml
                final_doc = candidate_doc
                break
            except Exception as exc:
                last_error = exc
                if attempt >= max_attempts:
                    break
                main_retry_count += 1
                previous_error = self._build_structured_plan_error(exc)

        if final_yaml is None or final_doc is None:
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                f"Pipeline main workflow assembly failed after {max_attempts} attempt(s): {last_error or 'unknown error'}",
            )

        quality_report = self._build_pipeline_quality_report(extraction, generated_leaves, main_retry_count, final_doc)
        inspection = self._build_pipeline_inspection(
            normalized_markdown,
            annotated_markdown,
            extraction,
            generated_leaves,
            main_retry_count,
            final_doc,
            pipeline_mcp_doc,
            pipeline_mcp_tool_contracts,
        )

        return {
            "yaml": final_yaml,
            "workflow": {
                "version": final_doc.version,
                "name": final_doc.name,
                "workflows": list(final_doc.workflows.keys()),
            },
            "meta": {
                "model": model,
                "mode": "pipeline",
                "leaf_subworkflow_count": len(generated_leaves),
            },
            "diagnostics": [],
            "pipeline": {
                "normalized_markdown": normalized_markdown,
                "annotated_markdown": annotated_markdown,
                "specs": self._build_extraction_json(extraction),
                "quality_report": quality_report,
                "inspection": inspection,
            },
        }

    async def _normalize_user_prompt(self, ctx: StepExecutionContext, raw_prompt: str, provider: str | None, model: str, reasoning: str | None) -> str:
        prompt = (
            "You are preparing a raw user automation prompt for GnOuGo workflow generation.\n"
            "Return ONLY clean Markdown. Do not wrap the result in code fences.\n\n"
            "Behavior:\n"
            "- Correct spelling and grammar.\n"
            "- Rewrite the raw prompt as clean Markdown.\n"
            "- Preserve the exact business meaning.\n"
            "- Do not invent requirements.\n"
            "- Do not remove requirements.\n"
            "- Do not change the user intent.\n"
            "- Keep all important business rules.\n"
            "- Keep input parameters, defaults, conditions, loops, security rules, reporting rules, and cleanup rules.\n"
            "- Make the result easier to read and easier to transform into workflows.\n\n"
            f"<raw_prompt>\n{raw_prompt}\n</raw_prompt>"
        )
        return await self._execute_pipeline_llm_text_phase(ctx, "normalize_user_prompt", prompt, provider, model, reasoning)

    async def _mark_extractable_blocks(
        self,
        ctx: StepExecutionContext,
        normalized_markdown: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
    ) -> str:
        prompt = self._build_mark_extractable_blocks_prompt(normalized_markdown)
        return await self._execute_pipeline_llm_text_phase(ctx, "mark_extractable_blocks", prompt, provider, model, reasoning)

    async def _mark_and_extract_subworkflow_specs(
        self,
        ctx: StepExecutionContext,
        normalized_markdown: str,
        pipeline_input: dict[str, Any],
        provider: str | None,
        model: str,
        reasoning: str | None,
        use_structured_extraction: bool,
        pipeline_mcp_doc: str | None,
        pipeline_mcp_tool_contracts: list[McpToolOutputContract],
    ) -> tuple[str, _WorkflowPipelineExtraction]:
        max_attempts = self._get_pipeline_generation_max_attempts(pipeline_input)
        previous_annotated_markdown: str | None = None
        previous_validation_errors: list[str] | None = None
        last_error: Exception | None = None

        for attempt in range(1, max_attempts + 1):
            prompt = (
                self._build_mark_extractable_blocks_prompt(normalized_markdown, pipeline_mcp_doc, use_structured_extraction)
                if previous_validation_errors is None
                else self._build_mark_extractable_blocks_repair_prompt(
                    normalized_markdown,
                    previous_annotated_markdown,
                    previous_validation_errors,
                    pipeline_mcp_doc,
                    use_structured_extraction,
                )
            )

            try:
                if use_structured_extraction:
                    structured = await self._execute_pipeline_llm_structured_phase(
                        ctx,
                        "mark_extractable_blocks",
                        prompt,
                        provider,
                        model,
                        reasoning,
                        self._build_mark_extractable_blocks_structured_output_schema(),
                        attempt=attempt,
                        max_attempts=max_attempts,
                    )
                    annotated_markdown, extraction = self._parse_structured_pipeline_extraction(structured)
                else:
                    annotated_markdown = await self._execute_pipeline_llm_text_phase(
                        ctx,
                        "mark_extractable_blocks",
                        prompt,
                        provider,
                        model,
                        reasoning,
                        attempt=attempt,
                        max_attempts=max_attempts,
                    )
                    extraction = self._extract_subworkflow_specs(annotated_markdown)

                extraction.validation_errors.extend(self._validate_pipeline_extraction_contracts(extraction, pipeline_mcp_tool_contracts))
                if use_structured_extraction:
                    extraction.quality_review = await self._review_pipeline_extraction_quality(
                        ctx,
                        normalized_markdown,
                        annotated_markdown,
                        extraction,
                        pipeline_mcp_doc,
                        provider,
                        model,
                        reasoning,
                    )
                    if self._should_retry_pipeline_extraction_review(extraction.quality_review):
                        extraction.validation_errors.append(self._format_pipeline_extraction_quality_review_error(extraction.quality_review))

                if not extraction.validation_errors:
                    return annotated_markdown, extraction

                validation_error = self._build_pipeline_extraction_exception(extraction.validation_errors, annotated_markdown)
                if attempt >= max_attempts:
                    raise validation_error

                last_error = validation_error
                previous_annotated_markdown = annotated_markdown
                previous_validation_errors = list(extraction.validation_errors)
                self._add_pipeline_extraction_retry_telemetry(ctx, attempt, max_attempts, validation_error)
            except Exception as exc:
                if attempt >= max_attempts:
                    raise
                last_error = exc
                previous_annotated_markdown = None
                previous_validation_errors = [str(exc)]
                self._add_pipeline_extraction_retry_telemetry(ctx, attempt, max_attempts, exc)

        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"workflow.plan pipeline extraction failed after {max_attempts} attempt(s): {last_error or 'unknown error'}",
        )

    @staticmethod
    def _build_mark_extractable_blocks_prompt(
        normalized_markdown: str,
        pipeline_mcp_servers_doc: str | None = None,
        use_structured_extraction: bool = False,
    ) -> str:
        return_mode = (
            "Return ONLY JSON matching the requested structured output schema. Do not wrap the result in code fences."
            if use_structured_extraction
            else "Return ONLY annotated Markdown. Do not wrap the result in code fences."
        )
        structured_rules = (
            "\nStructured metadata rules:\n"
            "- Structured subworkflow metadata must classify each leaf with `work_kind`: `orchestration`, `deterministic_shaping`, or `external_work`.\n"
            "- Structured subworkflow metadata must also declare `contract_role`: `external_action`, `typed_data_producer`, `algorithmic_transform`, `deterministic_glue`, `orchestration`, or `abstract_policy`.\n"
            "- Only `external_action`, `typed_data_producer`, and `algorithmic_transform` are valid leaf roles. `deterministic_glue`, `orchestration`, and `abstract_policy` must stay in `## Main workflow orchestration`.\n"
            "- Structured subworkflow metadata must include `concrete_outcome`: the exact concrete value, side effect, or typed data product this leaf owns.\n"
            "- Avoid `any`, bare `object`, and bare `array` outputs. If an output may be looped over or inspected by the main workflow, declare concrete `items` and object `properties`.\n"
            "- Structured `planned_tools` must list every MCP server tool or prompt this leaf is expected to call directly.\n"
            "- Mark planned tools as required when omitting that MCP call would violate the leaf goal.\n"
            "- For each relevant MCP tool or prompt, add a structured planned_tools entry with the exact server name, kind, method name, purpose, consumed fields, and produced fields.\n"
            "- External-work leaves that clone, read/fetch/query/list external data, write, delete, cleanup, report, post, push, or call outside systems must declare concrete planned_tools when matching MCP tools/prompts are documented above.\n"
            "- If no MCP tool or prompt is required for a leaf, use an empty planned_tools array.\n"
            if use_structured_extraction
            else ""
        )
        mcp_section = (
            "\n<pipeline_available_mcp_servers>\n"
            "Use this context only to choose extraction boundaries and explicit input/output variables for leaf contracts.\n"
            f"{pipeline_mcp_servers_doc.strip()}\n"
            "</pipeline_available_mcp_servers>\n"
            if pipeline_mcp_servers_doc and pipeline_mcp_servers_doc.strip()
            else ""
        )
        return (
            "You annotate normalized automation Markdown for GnOuGo workflow generation.\n"
            f"{return_mode}\n\n"
            "Identify only the parts that contain significant algorithmic logic and wrap them in exactly this block syntax:\n\n"
            ":::subworkflow name=\"snake_case_name\"\n"
            "goal: Short goal.\n"
            "inputs:\n"
            "  input_name: type\n"
            "outputs:\n"
            "  output_name: type\n"
            "extract_reason: Why this deserves a sub-workflow.\n"
            "content:\n"
            "  Markdown description of the logic to implement.\n"
            ":::\n\n"
            "A part is algorithmic if it contains:\n"
            "- a loop;\n"
            "- a conditional decision;\n"
            "- a multi-step sequence with state;\n"
            "- tool orchestration;\n"
            "- retry or error handling;\n"
            "- branching logic;\n"
            "- file or report generation;\n"
            "- cleanup logic;\n"
            "- a reusable technical operation.\n\n"
            "Do not extract:\n"
            "- simple one-line or few actions;\n"
            "- global style rules;\n"
            "- constants;\n"
            "- footer text;\n"
            "- wording rules;\n"
            "- tiny isolated actions that do not deserve a workflow.\n\n"
            "Keep extracted blocks focused:\n"
            "- Do not create one large block that mixes several responsibilities.\n"
            "- Avoid blocks with high cyclomatic complexity: too many branches, nested conditionals, nested loops, retry paths, cleanup paths, or state transitions.\n"
            "- When one algorithmic section has several independent decision paths or phases, split it into multiple self-contained leaf subworkflow blocks.\n"
            "- Prefer cohesive blocks that a workflow generator can implement without needing to reason about unrelated branches.\n"
            "- Do not over-split into trivial one-line operations; split only when the reduced complexity improves workflow generation quality.\n\n"
            "Rules for subworkflow blocks:\n"
            "- The name must use snake_case.\n"
            "- Each block must describe exactly one responsibility.\n"
            "- Each block must be self-contained.\n"
            "- Each block must be detailed enough to generate a workflow later.\n"
            "- Each block must be a leaf workflow.\n"
            "- The block content must not mention calling another subworkflow.\n"
            "- The block content must not contain another :::subworkflow block.\n"
            "- Inputs and outputs must be explicit and typed.\n\n"
            "- Keep global rules outside subworkflow blocks when they apply to the whole automation.\n\n"
            "At the end of the Markdown, add:\n\n"
            "## Main workflow orchestration\n\n"
            "In that section, explain how the main workflow calls the leaf subworkflows in order.\n"
            "The architecture must have only one hierarchy level:\n"
            "- Only the main workflow can call subworkflows.\n"
            "- Every subworkflow is a leaf workflow.\n"
            "- A subworkflow must never call another subworkflow.\n"
            "- A subworkflow must never depend on another subworkflow.\n"
            "- The final YAML will contain the main workflow and all leaf subworkflows in the same local YAML file.\n"
            "- The main workflow calls leaf workflows with local workflow.call.\n\n"
            f"{structured_rules}"
            f"{mcp_section}"
            f"<normalized_markdown>\n{normalized_markdown}\n</normalized_markdown>"
        )

    @staticmethod
    def _build_mark_extractable_blocks_repair_prompt(
        normalized_markdown: str,
        previous_annotated_markdown: str | None,
        validation_errors: list[str],
        pipeline_mcp_servers_doc: str | None = None,
        use_structured_extraction: bool = False,
    ) -> str:
        parts = [
            WorkflowPlanExecutor._build_mark_extractable_blocks_prompt(
                normalized_markdown,
                pipeline_mcp_servers_doc,
                use_structured_extraction,
            ).rstrip(),
            "",
            "The previous `mark_extractable_blocks` response failed extraction validation.",
            (
                "Return a complete corrected structured extraction JSON document. Keep the original user intent and fix only the extraction shape."
                if use_structured_extraction
                else "Return a complete corrected annotated Markdown document. Keep the original user intent and fix only the annotation shape."
            ),
            "",
            "<validation_errors>",
            *[f"- {error}" for error in validation_errors],
            "</validation_errors>",
            "",
            "<correction_checklist>",
            "- Every extracted block must open with exactly `:::subworkflow name=\"snake_case_name\"` and close with exactly `:::`.",
            "- Never nest `:::subworkflow` blocks.",
            "- Each block must include non-empty `goal:`, `inputs:`, `outputs:`, `extract_reason:`, and `content:` sections.",
            "- Each input and output line must be `identifier: type`; use explicit simple types such as string, number, boolean, array, object, or dictionary.",
            "- Block names and input/output names must be identifiers; block names must be snake_case and unique.",
            "- Block content must describe leaf logic only and must not mention calling another subworkflow.",
            "- Structured work_kind must match the leaf role: orchestration, deterministic_shaping, or external_work.",
            "- Structured contract_role must be one of external_action, typed_data_producer, algorithmic_transform, deterministic_glue, orchestration, or abstract_policy.",
            "- Only external_action, typed_data_producer, and algorithmic_transform can remain as leaf blocks; move deterministic_glue, orchestration, and abstract_policy back to the main workflow.",
            "- Every remaining leaf must have a concrete_outcome and strongly typed output schemas.",
            "- External-work leaves with matching MCP capabilities must include concrete planned_tools entries.",
            "- The document must include `## Main workflow orchestration` after the leaf blocks.",
            "</correction_checklist>",
        ]
        if previous_annotated_markdown and previous_annotated_markdown.strip():
            parts.extend(["", WorkflowPlanExecutor._prompt_section("invalid_annotated_markdown", previous_annotated_markdown)])
        parts.extend(
            [
                "",
                (
                    "Fix the validation errors above and return ONLY the corrected structured extraction JSON."
                    if use_structured_extraction
                    else "Fix the validation errors above and return ONLY the corrected annotated Markdown."
                ),
            ]
        )
        return "\n".join(parts)

    @staticmethod
    def _build_pipeline_extraction_exception(validation_errors: list[str], annotated_markdown: str | None) -> WorkflowRuntimeException:
        details: dict[str, Any] = {"validation": {"errors": list(validation_errors)}}
        if annotated_markdown and annotated_markdown.strip():
            details["invalid_annotated_markdown"] = annotated_markdown
        return WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            "workflow.plan pipeline extraction failed: " + "; ".join(validation_errors),
            details=details,
        )

    @staticmethod
    def _add_pipeline_extraction_retry_telemetry(ctx: StepExecutionContext, attempt: int, max_attempts: int, exc: Exception) -> None:
        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            [
                (
                    "gnougo-flow.thinking.message",
                    f"Pipeline extraction attempt {attempt}/{max_attempts} failed; retrying mark_extractable_blocks with validation feedback.",
                ),
                ("gnougo-flow.thinking.level", "info"),
            ],
        )
        ctx.add_telemetry_event(
            "gnougo-flow.plan.pipeline.extractable_blocks_retry",
            [
                ("gnougo-flow.plan.attempt", attempt),
                ("gnougo-flow.plan.max_attempts", max_attempts),
                ("error.type", type(exc).__name__),
                ("error.message", str(exc)),
            ],
        )

    async def _execute_pipeline_llm_text_phase(
        self,
        ctx: StepExecutionContext,
        phase: str,
        prompt: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
        attempt: int | None = None,
        max_attempts: int | None = None,
    ) -> str:
        attributes = [
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.system", provider or "unknown"),
            ("gen_ai.request.model", model),
            ("gen_ai.request.background", True),
        ]
        if attempt is not None:
            attributes.append(("gnougo-flow.plan.attempt", attempt))
        if max_attempts is not None:
            attributes.append(("gnougo-flow.plan.max_attempts", max_attempts))
        with ctx.begin_telemetry_span(
            f"workflow.plan.pipeline.{phase}",
            phase,
            attributes,
        ) as span:
            response = await ctx.engine.call_llm_async(
                LLMRequest(provider=provider, model=model, prompt=prompt, reasoning=reasoning, use_background_mode=True)
            )
            self._add_usage_attributes(span, response.usage)
        text = self._strip_markdown_code_fence(textwrap.dedent(response.text or "")).strip()
        if not text:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"workflow.plan pipeline phase '{phase}' returned empty text.")
        return text

    async def _execute_pipeline_llm_structured_phase(
        self,
        ctx: StepExecutionContext,
        phase: str,
        prompt: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
        structured_output_schema: dict[str, Any],
        attempt: int | None = None,
        max_attempts: int | None = None,
    ) -> dict[str, Any]:
        attributes = [
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.system", provider or "unknown"),
            ("gen_ai.request.model", model),
            ("gen_ai.request.background", True),
            ("gnougo-flow.plan.structured_output", True),
        ]
        if attempt is not None:
            attributes.append(("gnougo-flow.plan.attempt", attempt))
        if max_attempts is not None:
            attributes.append(("gnougo-flow.plan.max_attempts", max_attempts))
        with ctx.begin_telemetry_span(f"workflow.plan.pipeline.{phase}", phase, attributes) as span:
            response = await ctx.engine.call_llm_async(
                LLMRequest(
                    provider=provider,
                    model=model,
                    prompt=prompt,
                    reasoning=reasoning,
                    use_background_mode=True,
                    structured_output_schema=structured_output_schema,
                    structured_output_strict=True,
                )
            )
            self._add_usage_attributes(span, response.usage)
        payload = response.json_payload
        if not isinstance(payload, dict) and response.text:
            try:
                payload = json.loads(self._strip_markdown_code_fence(response.text))
            except Exception:
                payload = None
        if not isinstance(payload, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"workflow.plan pipeline phase '{phase}' returned empty structured output.")
        return payload

    async def _should_use_structured_pipeline_extraction(self, ctx: StepExecutionContext, provider: str | None, model: str) -> bool:
        resolver = getattr(ctx.engine, "llm_capabilities", None)
        if resolver is None:
            return False
        try:
            result = await resolver.supports_structured_output_async(provider, model)
            return result is True
        except Exception:
            return False

    async def _build_pipeline_global_mcp_context(
        self,
        ctx: StepExecutionContext,
        generator: dict[str, Any],
        normalized_markdown: str,
        raw_prompt: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
    ) -> tuple[str | None, list[McpToolOutputContract], list[McpServerMetadata]]:
        if ctx.engine.mcp_client_factory is None:
            return None, [], []
        context_text = "\n".join(part for part in (normalized_markdown, raw_prompt) if part)
        candidate_servers = await self._maybe_prefilter_mcp_server_metadata(ctx, generator, raw_prompt, context_text, reasoning)
        server_metadata = candidate_servers if candidate_servers is not None else self._get_configured_mcp_server_metadata(ctx)
        contracts: list[McpToolOutputContract] = []
        if not server_metadata:
            return None, contracts, []
        doc = await self._build_mcp_documentation(ctx, server_metadata, contracts)
        doc = await self._maybe_prefilter_mcp_documentation(ctx, generator, raw_prompt, context_text, doc, reasoning)
        return doc, contracts, list(server_metadata)

    @staticmethod
    def _build_mark_extractable_blocks_structured_output_schema() -> dict[str, Any]:
        typed_field = {
            "type": "object",
            "additionalProperties": False,
            "required": ["name", "type", "description", "required", "item_type"],
            "properties": {
                "name": {"type": "string"},
                "type": {"type": "string", "enum": ["string", "number", "boolean", "array", "object", "dictionary", "any"]},
                "description": {"type": "string"},
                "required": {"type": "boolean"},
                "item_type": {"type": "string"},
            },
        }
        nested_typed_field = copy.deepcopy(typed_field)
        nested_typed_field["required"] = ["name", "type", "description", "required", "item_type", "properties"]
        nested_typed_field["properties"]["properties"] = {"type": "array", "items": typed_field}
        return {
            "type": "object",
            "additionalProperties": False,
            "required": ["annotated_markdown", "subworkflows", "main_orchestration"],
            "properties": {
                "annotated_markdown": {"type": "string"},
                "main_orchestration": {"type": "string"},
                "subworkflows": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "additionalProperties": False,
                        "required": [
                            "name",
                            "goal",
                            "description",
                            "work_kind",
                            "contract_role",
                            "concrete_outcome",
                            "inputs",
                            "outputs",
                            "extract_reason",
                            "content",
                            "planned_tools",
                        ],
                        "properties": {
                            "name": {"type": "string"},
                            "goal": {"type": "string"},
                            "description": {"type": "string"},
                            "work_kind": {"type": "string", "enum": ["orchestration", "deterministic_shaping", "external_work"]},
                            "contract_role": {
                                "type": "string",
                                "enum": [
                                    "external_action",
                                    "typed_data_producer",
                                    "algorithmic_transform",
                                    "deterministic_glue",
                                    "orchestration",
                                    "abstract_policy",
                                ],
                            },
                            "concrete_outcome": {"type": "string"},
                            "inputs": {"type": "array", "items": nested_typed_field},
                            "outputs": {"type": "array", "items": nested_typed_field},
                            "extract_reason": {"type": "string"},
                            "content": {"type": "string"},
                            "planned_tools": {
                                "type": "array",
                                "items": {
                                    "type": "object",
                                    "additionalProperties": False,
                                    "required": ["server", "kind", "method", "required", "purpose", "consumes", "produces"],
                                    "properties": {
                                        "server": {"type": "string"},
                                        "kind": {"type": "string", "enum": ["tool", "prompt"]},
                                        "method": {"type": "string"},
                                        "required": {"type": "boolean"},
                                        "purpose": {"type": "string"},
                                        "consumes": {"type": "array", "items": {"type": "string"}},
                                        "produces": {"type": "array", "items": {"type": "string"}},
                                    },
                                },
                            },
                        },
                    },
                },
            },
        }

    def _parse_structured_pipeline_extraction(self, payload: dict[str, Any]) -> tuple[str, _WorkflowPipelineExtraction]:
        annotated_markdown = textwrap.dedent(str(payload.get("annotated_markdown") or "")).strip()
        if not annotated_markdown:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Structured pipeline extraction must include annotated_markdown.")
        extraction = self._extract_subworkflow_specs(annotated_markdown)
        by_name = {spec.name: spec for spec in extraction.subworkflows}
        for item in payload.get("subworkflows") if isinstance(payload.get("subworkflows"), list) else []:
            if not isinstance(item, dict):
                continue
            name = str(item.get("name") or "").strip()
            spec = by_name.get(name)
            if spec is None:
                extraction.validation_errors.append(f"Structured extraction references unknown subworkflow '{name}'.")
                continue
            self._apply_structured_subworkflow_metadata(spec, item)
        main_orchestration = str(payload.get("main_orchestration") or "").strip()
        if main_orchestration:
            extraction.main_workflow_prompt = main_orchestration
        return annotated_markdown, extraction

    def _apply_structured_subworkflow_metadata(self, spec: _WorkflowPipelineSubworkflowSpec, item: dict[str, Any]) -> None:
        spec.description = str(item.get("description") or "")
        spec.work_kind = str(item.get("work_kind") or "")
        spec.contract_role = str(item.get("contract_role") or "")
        spec.concrete_outcome = str(item.get("concrete_outcome") or "")
        spec.input_schemas = self._typed_fields_to_schema_map(item.get("inputs"))
        spec.output_schemas = self._typed_fields_to_schema_map(item.get("outputs"))
        if spec.input_schemas:
            spec.inputs = {name: str(schema.get("type", "any")) for name, schema in spec.input_schemas.items()}
        if spec.output_schemas:
            spec.outputs = {name: str(schema.get("type", "any")) for name, schema in spec.output_schemas.items()}
        planned_tools: list[_PipelinePlannedTool] = []
        for tool in item.get("planned_tools") if isinstance(item.get("planned_tools"), list) else []:
            if not isinstance(tool, dict):
                continue
            server = str(tool.get("server") or "").strip()
            method = str(tool.get("method") or "").strip()
            kind = str(tool.get("kind") or "tool").strip() or "tool"
            if not server or not method:
                continue
            planned_tools.append(
                _PipelinePlannedTool(
                    server=server,
                    kind=kind,
                    method=method,
                    required=bool(tool.get("required", False)),
                    purpose=str(tool.get("purpose") or ""),
                    consumes=[str(value) for value in tool.get("consumes", []) if isinstance(value, str)],
                    produces=[str(value) for value in tool.get("produces", []) if isinstance(value, str)],
                )
            )
        spec.planned_tools = planned_tools
        spec.required_capabilities = [f"{tool.server}/{tool.method}" for tool in planned_tools if tool.required]
        spec.generation_prompt = self._build_subworkflow_generation_prompt(
            spec.name,
            spec.goal,
            spec.inputs,
            spec.outputs,
            spec.content,
            spec.planned_tools,
            spec.output_schemas,
        )

    def _typed_fields_to_schema_map(self, fields: Any) -> dict[str, Any]:
        schemas: dict[str, Any] = {}
        if not isinstance(fields, list):
            return schemas
        for field_info in fields:
            if not isinstance(field_info, dict):
                continue
            name = str(field_info.get("name") or "").strip()
            if not name:
                continue
            schemas[name] = self._typed_field_to_schema(field_info)
        return schemas

    def _typed_field_to_schema(self, field_info: dict[str, Any]) -> dict[str, Any]:
        type_name = self._normalize_workflow_schema_type(str(field_info.get("type") or "any"))
        schema: dict[str, Any] = {"type": type_name}
        description = field_info.get("description")
        if isinstance(description, str) and description.strip():
            schema["description"] = description.strip()
        item_type = self._normalize_workflow_schema_type(str(field_info.get("item_type") or "any"))
        properties = field_info.get("properties") if isinstance(field_info.get("properties"), list) else []
        if type_name == "array":
            if properties:
                schema["items"] = {
                    "type": "object",
                    "properties": {str(child.get("name")): self._typed_field_to_schema(child) for child in properties if isinstance(child, dict) and child.get("name")},
                }
                required = [str(child.get("name")) for child in properties if isinstance(child, dict) and child.get("required") is True and child.get("name")]
                if required:
                    schema["items"]["required_properties"] = required
            elif item_type and item_type != "any":
                schema["items"] = {"type": item_type}
        elif type_name == "object":
            schema["properties"] = {str(child.get("name")): self._typed_field_to_schema(child) for child in properties if isinstance(child, dict) and child.get("name")}
            required = [str(child.get("name")) for child in properties if isinstance(child, dict) and child.get("required") is True and child.get("name")]
            if required:
                schema["required_properties"] = required
        elif type_name == "dictionary" and item_type and item_type != "any":
            schema["additional_properties"] = {"type": item_type}
        if field_info.get("required") is False:
            schema["required"] = False
        return schema

    def _validate_pipeline_extraction_contracts(
        self,
        extraction: _WorkflowPipelineExtraction,
        mcp_tool_contracts: list[McpToolOutputContract],
    ) -> list[str]:
        errors: list[str] = []
        known_tools = {(contract.server_name, contract.tool_name) for contract in mcp_tool_contracts}
        for spec in extraction.subworkflows:
            if spec.contract_role in {"deterministic_glue", "orchestration", "abstract_policy"}:
                errors.append(
                    f"PIPELINE_EXTRACTION_INVALID_LEAF_ROLE: subworkflow '{spec.name}' has contract_role '{spec.contract_role}' and should stay in main orchestration."
                )
                extraction.root_causes.append(
                    {
                        "category": "invalid_leaf_role",
                        "phase": "mark_extractable_blocks",
                        "leaf": spec.name,
                        "invalid_path": f"subworkflows.{spec.name}.contract_role",
                        "message": "Only external_action, typed_data_producer, and algorithmic_transform are valid leaf roles.",
                    }
                )
            for output_name, schema in spec.output_schemas.items():
                if is_weak_yaml_output_schema(schema):
                    errors.append(
                        f"PIPELINE_EXTRACTION_WEAK_OUTPUT_CONTRACT: subworkflow '{spec.name}' output '{output_name}' has a weak output schema."
                    )
                    extraction.root_causes.append(
                        {
                            "category": "weak_output_contract",
                            "phase": "mark_extractable_blocks",
                            "leaf": spec.name,
                            "output": output_name,
                            "invalid_path": f"subworkflows.{spec.name}.outputs.{output_name}",
                            "message": "Leaf outputs must use concrete schemas.",
                        }
                    )
            self._promote_required_planned_tools(spec, known_tools)
            if self._requires_planned_tool(spec, mcp_tool_contracts) and not spec.planned_tools:
                errors.append(
                    f"PIPELINE_EXTRACTION_MISSING_REQUIRED_LEAF_TOOL: external-work subworkflow '{spec.name}' declares no planned_tools."
                )
                extraction.root_causes.append(
                    {
                        "category": "missing_required_leaf_tool",
                        "phase": "mark_extractable_blocks",
                        "leaf": spec.name,
                        "invalid_path": f"subworkflows.{spec.name}.planned_tools",
                        "message": "External-work leaves with matching MCP capabilities must declare planned_tools.",
                    }
                )
        return errors

    @staticmethod
    def _promote_required_planned_tools(spec: _WorkflowPipelineSubworkflowSpec, known_tools: set[tuple[str, str]]) -> None:
        if spec.contract_role != "external_action" and spec.work_kind != "external_work":
            return
        for tool in spec.planned_tools:
            if (tool.server, tool.method) in known_tools:
                tool.required = True
        spec.required_capabilities = [f"{tool.server}/{tool.method}" for tool in spec.planned_tools if tool.required]

    @staticmethod
    def _requires_planned_tool(spec: _WorkflowPipelineSubworkflowSpec, mcp_tool_contracts: list[McpToolOutputContract]) -> bool:
        if not mcp_tool_contracts:
            return False
        if spec.contract_role not in {"external_action", ""} and spec.work_kind != "external_work":
            return False
        text = " ".join([spec.goal, spec.description, spec.extract_reason, spec.content]).lower()
        external_words = {
            "clone",
            "fetch",
            "read",
            "query",
            "list",
            "write",
            "delete",
            "cleanup",
            "report",
            "post",
            "push",
            "external",
            "repository",
            "github",
            "file",
            "document",
        }
        return any(word in text for word in external_words)

    async def _review_pipeline_extraction_quality(
        self,
        ctx: StepExecutionContext,
        normalized_markdown: str,
        annotated_markdown: str,
        extraction: _WorkflowPipelineExtraction,
        pipeline_mcp_doc: str | None,
        provider: str | None,
        model: str,
        reasoning: str | None,
    ) -> dict[str, Any] | None:
        prompt = (
            "You are reviewing the quality of a `workflow.plan` pipeline extraction.\n"
            "Return ONLY JSON with score, verdict, diagnostics, and retry_guidance.\n"
            "A score below 75 or verdict retry means the extraction must be corrected before leaf generation.\n\n"
            f"{self._prompt_section('normalized_markdown', normalized_markdown)}\n"
            f"{self._prompt_section('annotated_markdown', annotated_markdown)}\n"
            f"{self._prompt_section('leaf_subworkflow_specs_json', json.dumps(self._build_extraction_json(extraction), ensure_ascii=False, indent=2))}\n"
            f"{self._prompt_section('pipeline_available_mcp_servers', pipeline_mcp_doc or '')}"
        )
        schema = {
            "type": "object",
            "additionalProperties": False,
            "required": ["score", "verdict", "diagnostics", "retry_guidance"],
            "properties": {
                "score": {"type": "integer"},
                "verdict": {"type": "string", "enum": ["pass", "retry"]},
                "diagnostics": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "additionalProperties": False,
                        "required": ["code", "severity", "leaf_name", "message", "recommendation"],
                        "properties": {
                            "code": {"type": "string"},
                            "severity": {"type": "string"},
                            "leaf_name": {"type": "string"},
                            "message": {"type": "string"},
                            "recommendation": {"type": "string"},
                        },
                    },
                },
                "retry_guidance": {"type": "string"},
            },
        }
        try:
            return await self._execute_pipeline_llm_structured_phase(
                ctx,
                "review_extraction_quality",
                prompt,
                provider,
                model,
                reasoning,
                schema,
            )
        except Exception as exc:
            return {
                "score": None,
                "verdict": "warning",
                "diagnostics": [
                    {
                        "code": "PIPELINE_EXTRACTION_QUALITY_REVIEW_WARNING",
                        "severity": "warning",
                        "leaf_name": "",
                        "message": f"review_extraction_quality failed or returned invalid JSON output; continuing with deterministic validation only. {exc}",
                        "recommendation": "Continue with deterministic validation.",
                    }
                ],
                "retry_guidance": "",
            }

    @staticmethod
    def _should_retry_pipeline_extraction_review(review: dict[str, Any] | None) -> bool:
        if not isinstance(review, dict):
            return False
        verdict = str(review.get("verdict") or "").lower()
        score = review.get("score")
        if verdict == "retry":
            return True
        if isinstance(score, (int, float)) and score < 75:
            return True
        return False

    @staticmethod
    def _format_pipeline_extraction_quality_review_error(review: dict[str, Any] | None) -> str:
        if not isinstance(review, dict):
            return "PIPELINE_EXTRACTION_QUALITY_REVIEW: review requested retry."
        diagnostics = review.get("diagnostics") if isinstance(review.get("diagnostics"), list) else []
        detail = "; ".join(
            f"{item.get('code')}: {item.get('message')} {item.get('recommendation')}"
            for item in diagnostics
            if isinstance(item, dict)
        )
        return (
            f"PIPELINE_EXTRACTION_QUALITY_REVIEW: score={review.get('score')} verdict={review.get('verdict')} "
            f"retry_guidance={review.get('retry_guidance')}. {detail}"
        ).strip()

    def _extract_subworkflow_specs(self, annotated_markdown: str) -> _WorkflowPipelineExtraction:
        normalized = annotated_markdown.replace("\r\n", "\n").replace("\r", "\n")
        block_re = re.compile(r"(?ms)^:::subworkflow\s+name=\"(?P<name>[^\"]+)\"\s*\n(?P<body>.*?)^:::\s*$")
        marker_re = re.compile(r"(?m)^:::subworkflow\b")
        specs: list[_WorkflowPipelineSubworkflowSpec] = []
        errors: list[str] = []
        names: set[str] = set()
        matches = list(block_re.finditer(normalized))
        if len(matches) != len(marker_re.findall(normalized)):
            errors.append("Nested or malformed :::subworkflow block found.")

        for match in matches:
            name = match.group("name").strip()
            if not re.match(r"^[a-z][a-z0-9_]*$", name):
                errors.append(f"Subworkflow name '{name}' must use snake_case.")
            if name in names:
                errors.append(f"Duplicate subworkflow name '{name}'.")
            names.add(name)
            specs.append(self._parse_subworkflow_block(name, match.group("body"), errors))

        if "## main workflow orchestration" not in normalized.lower():
            errors.append("Annotated markdown must include a '## Main workflow orchestration' section.")

        main_prompt = self._extract_main_workflow_prompt(normalized, specs)
        return _WorkflowPipelineExtraction(specs, main_prompt, errors)

    def _parse_subworkflow_block(self, name: str, body: str, errors: list[str]) -> _WorkflowPipelineSubworkflowSpec:
        if re.search(r"(?m)^:::subworkflow\b", body):
            errors.append(f"Subworkflow '{name}' contains a nested :::subworkflow block.")
        goal = ""
        extract_reason = ""
        inputs: dict[str, str] = {}
        outputs: dict[str, str] = {}
        content: list[str] = []
        section = ""
        for raw_line in body.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
            trimmed = raw_line.strip()
            if trimmed.startswith("goal:"):
                goal = trimmed[len("goal:") :].strip()
                section = ""
                continue
            if trimmed == "inputs:":
                section = "inputs"
                continue
            if trimmed == "outputs:":
                section = "outputs"
                continue
            if trimmed.startswith("extract_reason:"):
                extract_reason = trimmed[len("extract_reason:") :].strip()
                section = ""
                continue
            if trimmed.startswith("content:"):
                section = "content"
                inline = trimmed[len("content:") :].strip()
                if inline:
                    content.append(inline)
                continue
            if section in {"inputs", "outputs"}:
                if not trimmed:
                    continue
                if ":" not in trimmed:
                    errors.append(f"Subworkflow '{name}' has an invalid {section} line: '{trimmed}'.")
                    continue
                key, type_name = (part.strip() for part in trimmed.split(":", 1))
                if not key or not type_name:
                    errors.append(f"Subworkflow '{name}' has an untyped {section} entry: '{trimmed}'.")
                    continue
                if not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", key):
                    errors.append(f"Subworkflow '{name}' {section} entry '{key}' must be an identifier.")
                (inputs if section == "inputs" else outputs)[key] = self._normalize_workflow_schema_type(type_name)
                continue
            if section == "content":
                content.append(raw_line[2:] if raw_line.startswith("  ") else raw_line)

        content_text = "\n".join(content).strip()
        if not goal:
            errors.append(f"Subworkflow '{name}' is missing goal.")
        if not extract_reason:
            errors.append(f"Subworkflow '{name}' is missing extract_reason.")
        if not content_text:
            errors.append(f"Subworkflow '{name}' is missing content.")
        if re.search(r"\b(call|invoke|run)\s+(another\s+)?subworkflow\b", content_text, re.IGNORECASE):
            errors.append(f"Subworkflow '{name}' appears to call another subworkflow.")

        return _WorkflowPipelineSubworkflowSpec(
            name=name,
            goal=goal,
            inputs=inputs,
            outputs=outputs,
            extract_reason=extract_reason,
            content=content_text,
            generation_prompt=self._build_subworkflow_generation_prompt(name, goal, inputs, outputs, content_text),
        )

    @staticmethod
    def _extract_main_workflow_prompt(annotated_markdown: str, specs: list[_WorkflowPipelineSubworkflowSpec]) -> str:
        match = re.search(r"(?im)^##\s+Main workflow orchestration\b", annotated_markdown)
        if match:
            return annotated_markdown[match.start() :].strip()
        order = "No leaf subworkflows were extracted." if not specs else ", ".join(spec.name for spec in specs)
        return "Build a main workflow that calls these leaf subworkflows in order with local workflow.call: " + order

    @staticmethod
    def _normalize_workflow_schema_type(type_name: str) -> str:
        normalized = type_name.strip().lower()
        return normalized if normalized in {"string", "number", "integer", "boolean", "array", "object", "dictionary", "any"} else "any"

    @staticmethod
    def _build_subworkflow_generation_prompt(
        name: str,
        goal: str,
        inputs: dict[str, str],
        outputs: dict[str, str],
        content: str,
        planned_tools: list[_PipelinePlannedTool] | None = None,
        output_schemas: dict[str, Any] | None = None,
    ) -> str:
        planned_tools = planned_tools or []
        output_schemas = output_schemas or {}
        lines = [
            f"Generate exactly one leaf GnOuGo workflow named `{name}`.",
            f"Goal: {goal}",
            "",
            "Leaf workflow constraints:",
            "- Generate a complete YAML document with version, name, skill, and workflows.",
            f"- The document must contain exactly one workflow, preferably named `{name}`.",
            "- The workflow must be a leaf workflow.",
            "- Do not use workflow.call.",
            "- Do not use workflow.plan.",
            "- Do not depend on another subworkflow.",
            "- Treat the declared input/output contract as a draft when MCP tools require additional arguments.",
            "MCP input contract rules:",
            *WorkflowPlanExecutor._MCP_INPUT_CONTRACT_CHECKLIST,
            "- Workflow outputs must match their declared contract type exactly on every path.",
            "- If a step has an `if`, later unconditional steps must not reference that step directly. "
            "Either give the later step the same guard or create guaranteed branch outputs/default values first.",
            "- Function arguments are evaluated before the function runs. Do not hide unavailable step references inside "
            "`coalesce`, ternaries, or helper calls.",
            "- Every generated custom `function name(...)` declaration in a `functions:` block MUST be immediately "
            "preceded by JSDoc (`/** ... */`).",
            "- Function JSDoc MUST include one typed `@param {type} name - meaning` tag for every function parameter "
            "and one typed `@returns {type} - meaning` tag for the output.",
            "- For MCP schemas, required numeric/integer/boolean request fields must be literal YAML scalars when the "
            "schema or validator requires explicit values; do not use expressions, casts, empty strings, or `data.env.*` fallbacks.",
            "- Any schema with `type: object` MUST be strongly typed with a non-empty `properties` mapping. "
            "Never generate a bare `type: object` input, output, item, or nested property.",
            "- Use `required_properties: [field_name]` for required object property names; do not duplicate YAML keys.",
        ]
        if planned_tools:
            lines.extend(
                [
                    "",
                    "Planned MCP tools:",
                    *[
                        (
                            f"- {tool.server}/{tool.method} ({tool.kind}, {'required' if tool.required else 'optional'}): "
                            f"{tool.purpose or 'Use this capability when implementing the leaf.'}"
                        )
                        for tool in planned_tools
                    ],
                    "- Required planned MCP tools must appear as direct `mcp.call` steps with matching input.server, input.kind, and input.method or input.methods.",
                ]
            )
        if output_schemas:
            lines.extend(
                [
                    "",
                    "Structured output schemas:",
                    WorkflowPlanExecutor._serialize_yaml_value(output_schemas),
                    "- Leaf workflow outputs and skill outputs must match these schemas exactly.",
                ]
            )
        lines.extend(["", "Inputs:"])
        lines.extend([f"- {key}: {value}" for key, value in inputs.items()] or ["- none"])
        lines.append("Outputs:")
        lines.extend([f"- {key}: {value}" for key, value in outputs.items()] or ["- none"])
        lines.extend(["", "Content to implement:", content])
        return "\n".join(lines).strip()

    async def _generate_leaf_workflow_async(
        self,
        ctx: StepExecutionContext,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        spec: _WorkflowPipelineSubworkflowSpec,
    ) -> _GeneratedLeafWorkflow:
        max_attempts = self._get_pipeline_generation_max_attempts(pipeline_input)
        previous_error: str | None = None
        previous_yaml: str | None = None
        previous_prompt: str | None = None
        previous_repair_context: str | None = None
        previous_errors: list[str] = []
        last_error: Exception | None = None
        for attempt in range(1, max_attempts + 1):
            leaf_input = self._build_leaf_plan_input(
                pipeline_input,
                generator,
                spec,
                previous_error,
                previous_yaml,
                previous_prompt,
                previous_repair_context,
            )
            previous_prompt = leaf_input.get("generator", {}).get("instruction") if isinstance(leaf_input.get("generator"), dict) else spec.generation_prompt
            yaml_text: str | None = None
            try:
                result = await self._execute_single_plan_async(ctx, leaf_input)
                yaml_text = result.get("yaml") if isinstance(result, dict) else None
                if not isinstance(yaml_text, str) or not yaml_text.strip():
                    raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Leaf workflow '{spec.name}' generation did not return YAML.")
                return self._prepare_generated_leaf(spec, yaml_text)
            except Exception as exc:
                last_error = exc
                if attempt >= max_attempts:
                    break
                previous_yaml = yaml_text or self._try_extract_generated_yaml_from_exception(exc)
                previous_error = self._format_leaf_generation_error(spec.name, attempt, exc)
                previous_errors.append(previous_error)
                previous_errors = previous_errors[-8:]
                previous_repair_context = await self._build_pipeline_leaf_repair_context(ctx, pipeline_input, previous_yaml, exc)
                previous_error = self._merge_leaf_cumulative_repair_context(previous_errors, previous_repair_context)

        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"Leaf workflow '{spec.name}' failed after {max_attempts} generation attempt(s): {last_error or 'unknown error'}",
        )

    def _build_leaf_plan_input(
        self,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        spec: _WorkflowPipelineSubworkflowSpec,
        previous_error: str | None,
        previous_yaml: str | None,
        previous_prompt: str | None,
        previous_repair_context: str | None,
    ) -> dict[str, Any]:
        leaf_generator = copy.deepcopy(generator)
        leaf_generator.pop("mode", None)
        leaf_generator.pop("raw_prompt", None)
        leaf_generator["instruction"] = (
            spec.generation_prompt
            if not previous_error
            else self._build_leaf_repair_prompt(spec.generation_prompt, previous_prompt, previous_yaml, previous_error, previous_repair_context)
        )
        leaf_generator["context"] = ""
        leaf_generator["pipeline_leaf_name"] = spec.name
        leaf_input: dict[str, Any] = {
            "generator": leaf_generator,
            "policy": self._build_leaf_policy(pipeline_input.get("policy") if isinstance(pipeline_input.get("policy"), dict) else None),
            "validate": copy.deepcopy(pipeline_input.get("validate") if isinstance(pipeline_input.get("validate"), dict) else {}),
            "on_invalid": {"action": "fail", "max_attempts": 1},
        }
        leaf_input["validate"]["compile"] = True
        if isinstance(pipeline_input.get("limits"), dict):
            leaf_input["limits"] = copy.deepcopy(pipeline_input["limits"])
        return leaf_input

    def _build_leaf_repair_prompt(
        self,
        generation_prompt: str,
        previous_prompt: str | None,
        previous_yaml: str | None,
        previous_error: str,
        additional_repair_context: str | None,
    ) -> str:
        repair_context = "Previous generated YAML for this leaf workflow failed validation.\nRegenerate only this leaf workflow and fix the YAML below."
        if previous_prompt:
            repair_context += f"\n\n<previous_prompt>\n{previous_prompt.strip()}\n</previous_prompt>"
        if additional_repair_context and additional_repair_context.strip():
            repair_context += f"\n\nAdditional validation repair context:\n{additional_repair_context.strip()}"
        return self._build_reprompt(generation_prompt, "", {}, previous_yaml, Exception(previous_error), repair_context)

    @staticmethod
    def _format_leaf_generation_error(leaf_name: str, attempt: int, exc: Exception) -> str:
        return (
            f"Leaf workflow: {leaf_name}\n"
            f"Failed attempt: {attempt}\n"
            f"Error type: {type(exc).__name__}\n"
            f"Structured error: {WorkflowPlanExecutor._build_structured_plan_error(exc)}\n"
            f"Error message:\n{exc}"
        )

    @staticmethod
    def _merge_leaf_cumulative_repair_context(previous_errors: list[str], latest_repair_context: str | None = None) -> str:
        lines = [
            "Cumulative leaf retry requirements:",
            "- Preserve all fixes made for earlier validation failures; do not regress one MCP request or output while fixing another.",
            "- Re-check every mcp.call in the leaf against its discovered input_schema, not only the step named in the latest error.",
            "- If a required MCP request field is numeric/integer/boolean, emit an explicit YAML scalar of that type when the validator requires it.",
            "- If a required MCP request field is string/number/boolean, do not pass a nullable structured_output field into it; make the source non-null, add an exact non-null step guard, or skip the mcp.call.",
            "- Never satisfy missing MCP arguments with `data.env.*`, empty strings, fake values, casts, or string-to-number conversions.",
            "- Do not reference an `if`-guarded step from an unconditional later step unless a guaranteed value has first been produced on every path.",
            "- Workflow outputs must resolve to their declared type on every path.",
        ]
        if previous_errors:
            lines.extend(["", "All previous failed attempts for this leaf:"])
            for index, error in enumerate(previous_errors, start=1):
                lines.extend([f"<leaf_failure_{index}>", error, f"</leaf_failure_{index}>"])
        if latest_repair_context and latest_repair_context.strip():
            lines.extend(["", latest_repair_context.strip()])
        return "\n".join(lines).rstrip()

    @staticmethod
    def _try_extract_generated_yaml_from_exception(exc: Exception) -> str | None:
        current: BaseException | None = exc
        while current is not None:
            details = getattr(current, "details", None)
            if isinstance(details, dict):
                for key in ("generated_yaml", "invalid_yaml", "yaml"):
                    value = details.get(key)
                    if isinstance(value, str) and value.strip():
                        return value
            current = current.__cause__ or current.__context__
        return None

    async def _build_pipeline_leaf_repair_context(
        self,
        ctx: StepExecutionContext,
        pipeline_input: dict[str, Any],
        previous_yaml: str | None,
        exc: Exception,
    ) -> str | None:
        if not previous_yaml or not previous_yaml.strip():
            return None
        try:
            leaf_policy = self._build_leaf_policy(pipeline_input.get("policy") if isinstance(pipeline_input.get("policy"), dict) else None)
            mcp_contracts: list[McpToolOutputContract] = []
            if "mcp.call" in previous_yaml:
                mcp_contracts = await self._collect_mcp_tool_contracts(ctx, self._get_configured_mcp_server_metadata(ctx))
            return self._build_minimal_repair_context(ctx, leaf_policy, previous_yaml, exc, mcp_contracts)
        except Exception:
            return None

    def _prepare_generated_leaf(self, spec: _WorkflowPipelineSubworkflowSpec, yaml_text: str) -> _GeneratedLeafWorkflow:
        doc = WorkflowParser.parse(yaml_text)
        if len(doc.workflows) != 1:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Leaf workflow '{spec.name}' must generate exactly one workflow.")
        workflow_name = next(iter(doc.workflows))
        self._enforce_strong_object_schemas(spec.name, doc)
        self._enforce_strong_array_output_schemas(spec.name, spec, workflow_name, doc)
        self._enforce_required_planned_tools_used(spec, doc)
        self._enforce_leaf_action_quality(spec, doc)
        self._enforce_leaf_public_output_contracts(spec, doc)
        for step in self._enumerate_steps(doc.workflows[workflow_name].steps):
            if step.type in {"workflow.call", "workflow.plan"}:
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, f"Leaf workflow '{spec.name}' must not contain step type '{step.type}'.")
        parsed = yaml.safe_load(yaml_text)
        workflow_node = copy.deepcopy(parsed.get("workflows", {}).get(workflow_name)) if isinstance(parsed, dict) else None
        if not isinstance(workflow_node, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Leaf workflow '{spec.name}' did not contain a valid workflow mapping.")
        document_functions = parsed.get("functions") if isinstance(parsed, dict) else None
        if isinstance(document_functions, str) and document_functions.strip():
            workflow_functions = workflow_node.get("functions")
            workflow_node["functions"] = (
                document_functions.rstrip()
                if not isinstance(workflow_functions, str) or not workflow_functions.strip()
                else document_functions.rstrip() + "\n\n" + workflow_functions.lstrip()
            )
        return _GeneratedLeafWorkflow(spec.name, workflow_name, doc, yaml_text, workflow_node, spec)

    def _enforce_required_planned_tools_used(self, spec: _WorkflowPipelineSubworkflowSpec, doc: WorkflowDocument) -> None:
        required_tools = [tool for tool in spec.planned_tools if tool.required]
        if not required_tools:
            return
        missing = [tool for tool in required_tools if not self._workflow_contains_planned_mcp_tool_call(doc, tool)]
        if missing:
            rendered = ", ".join(f"{tool.server}/{tool.method} ({tool.kind})" for tool in missing)
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                f"Leaf workflow '{spec.name}' did not use required planned MCP tool(s): {rendered}. "
                "Add explicit direct mcp.call step(s) with matching input.server, input.kind, and literal input.method or input.methods.",
            )

    def _workflow_contains_planned_mcp_tool_call(self, doc: WorkflowDocument, planned_tool: _PipelinePlannedTool) -> bool:
        for workflow in doc.workflows.values():
            for step in self._enumerate_steps(workflow.steps):
                if step.type != "mcp.call" or not isinstance(step.input, dict):
                    continue
                server = step.input.get("server")
                kind = str(step.input.get("kind", "tool"))
                if server != planned_tool.server or kind != planned_tool.kind:
                    continue
                method = step.input.get("method")
                if isinstance(method, str) and method == planned_tool.method:
                    return True
                methods = step.input.get("methods")
                if isinstance(methods, list) and planned_tool.method in methods:
                    return True
        return False

    def _enforce_leaf_action_quality(self, spec: _WorkflowPipelineSubworkflowSpec, doc: WorkflowDocument) -> None:
        if spec.work_kind != "external_work" and spec.contract_role != "external_action":
            return
        steps = [step for workflow in doc.workflows.values() for step in self._enumerate_steps(workflow.steps)]
        external_steps = [step for step in steps if step.type in {"mcp.call", "llm.call", "human.input", "workflow.execute"}]
        if external_steps:
            return
        text = " ".join([spec.goal, spec.extract_reason, spec.content]).lower()
        if any(step.type == "emit" for step in steps) and any(word in text for word in {"clone", "cleanup", "write", "delete", "fetch", "external"}):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"PIPELINE_LEAF_FAKE_ACTION_EMIT: Leaf workflow '{spec.name}' emits instructions instead of performing external work.")
        output_defs = [output for workflow in doc.workflows.values() for output in (workflow.outputs or {}).values()]
        if any(str(getattr(output, "expr", "")).lower() in {"true", "${true}"} or getattr(output, "type", "").lower() == "boolean" for output in output_defs):
            if any(word in text for word in {"cleanup", "delete", "remove"}):
                raise WorkflowRuntimeException(
                    ErrorCodes.TEMPLATE_PLAN,
                    f"PIPELINE_LEAF_SUCCESS_OUTPUT_WITHOUT_ACTION: Leaf workflow '{spec.name}' reports success without performing the external action.",
                )

    def _enforce_leaf_public_output_contracts(self, spec: _WorkflowPipelineSubworkflowSpec, doc: WorkflowDocument) -> None:
        if not spec.output_schemas and not spec.work_kind and not spec.contract_role:
            return
        diagnostics: list[dict[str, Any]] = []
        if doc.skill and doc.skill.outputs:
            for output_name, output in doc.skill.outputs.items():
                collect_weak_output_schema_diagnostics(output, f"skill.outputs.{output_name}", diagnostics, allow_skill_scalar_type_shorthand=True)
        for workflow_name, workflow in doc.workflows.items():
            for output_name, output in (workflow.outputs or {}).items():
                collect_weak_output_schema_diagnostics(output, f"workflows.{workflow_name}.outputs.{output_name}", diagnostics, allow_skill_scalar_type_shorthand=False)
        if diagnostics:
            messages = "; ".join(f"{item['location']}: {item['message']}" for item in diagnostics)
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Leaf workflow '{spec.name}' uses weak public output schemas: {messages}")

    def _run_standard_plan_validation_sequence(
        self,
        doc: WorkflowDocument,
        policy: dict[str, Any],
        limits: dict[str, Any],
        validate: dict[str, Any],
        mcp_contracts: list[McpToolOutputContract],
        mcp_server_metadata: list[McpServerMetadata],
    ) -> None:
        self._enforce_plan_policy(doc, policy, limits)
        if bool(validate.get("compile", True)):
            normalization_count = self._validate_generated_workflow_for_plan(doc, mcp_contracts, mcp_server_metadata)
            if normalization_count:
                doc.raw_yaml = self._dump_workflow_yaml(doc)
        elif bool(validate.get("dry_run", False)):
            self._validate_mcp_discovery_coverage(doc, mcp_contracts, mcp_server_metadata)

    @staticmethod
    def _normalize_pipeline_main_policy(input_obj: dict[str, Any]) -> None:
        support_step_types = {
            "workflow.call",
            "set",
            "sequence",
            "switch",
            "parallel",
            "loop.sequential",
            "loop.parallel",
        }
        policy = input_obj.get("policy")
        if not isinstance(policy, dict):
            policy = {}
            input_obj["policy"] = policy
        denied = policy.get("denied_step_types")
        if isinstance(denied, list):
            policy["denied_step_types"] = [item for item in denied if str(item) not in support_step_types]
        allowed = policy.get("allowed_step_types")
        if isinstance(allowed, list):
            for step_type in support_step_types:
                if step_type not in allowed:
                    allowed.append(step_type)

    @staticmethod
    def _build_leaf_policy(source_policy: dict[str, Any] | None) -> dict[str, Any]:
        policy = copy.deepcopy(source_policy or {})
        allowed = policy.get("allowed_step_types")
        if isinstance(allowed, list):
            policy["allowed_step_types"] = [item for item in allowed if str(item) not in {"workflow.call", "workflow.plan"}]
        denied = policy.get("denied_step_types")
        if not isinstance(denied, list):
            denied = []
            policy["denied_step_types"] = denied
        for step_type in ("workflow.call", "workflow.plan"):
            if step_type not in denied:
                denied.append(step_type)
        policy["allow_remote_workflow_refs"] = False
        return policy

    def _enforce_strong_object_schemas(self, leaf_name: str, doc: WorkflowDocument) -> None:
        errors: list[str] = []
        if doc.skill and doc.skill.inputs:
            for name, definition in doc.skill.inputs.items():
                self._validate_strong_object_schema(definition, f"skill.inputs.{name}", errors)
        if doc.skill and doc.skill.outputs:
            for name, definition in doc.skill.outputs.items():
                self._validate_strong_object_schema(definition, f"skill.outputs.{name}", errors)
        for workflow_name, workflow in doc.workflows.items():
            for name, definition in (workflow.inputs or {}).items():
                self._validate_strong_object_schema(definition, f"workflows.{workflow_name}.inputs.{name}", errors)
            for name, definition in (workflow.outputs or {}).items():
                self._validate_strong_object_schema(definition, f"workflows.{workflow_name}.outputs.{name}", errors)
        if errors:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Leaf workflow '{leaf_name}' uses weak object schemas: {'; '.join(errors)}")

    def _validate_strong_object_schema(self, definition: Any, path: str, errors: list[str]) -> None:
        if getattr(definition, "type", "").lower() == "object" and not getattr(definition, "properties", None):
            errors.append(f"{path} has type object without properties")
        if getattr(definition, "items", None) is not None:
            self._validate_strong_object_schema(definition.items, path + ".items", errors)
        for name, child in (getattr(definition, "properties", None) or {}).items():
            self._validate_strong_object_schema(child, f"{path}.properties.{name}", errors)
        if getattr(definition, "additional_properties", None) is not None:
            self._validate_strong_object_schema(definition.additional_properties, path + ".additional_properties", errors)

    def _enforce_strong_array_output_schemas(
        self,
        leaf_name: str,
        spec: _WorkflowPipelineSubworkflowSpec,
        workflow_name: str,
        doc: WorkflowDocument,
    ) -> None:
        errors: list[str] = []
        if doc.skill and doc.skill.outputs:
            for name, definition in doc.skill.outputs.items():
                self._validate_strong_array_output_schema(definition, f"skill.outputs.{name}", errors)

        workflow = doc.workflows.get(workflow_name)
        if workflow and workflow.outputs:
            for name, definition in workflow.outputs.items():
                self._validate_strong_array_output_schema(definition, f"workflows.{workflow_name}.outputs.{name}", errors)

            for name, type_name in spec.outputs.items():
                if self._normalize_workflow_schema_type(type_name) != "array":
                    continue
                output = workflow.outputs.get(name)
                if output is None:
                    errors.append(f"workflows.{workflow_name}.outputs.{name} is missing but was declared as an array output in the extracted leaf contract")
                    continue
                if self._normalize_workflow_schema_type(getattr(output, "type", "any")) != "array":
                    errors.append(
                        f"workflows.{workflow_name}.outputs.{name} was declared as an array output in the extracted leaf contract "
                        "but the generated workflow output is not typed as array"
                    )
                    continue
                if getattr(output, "items", None) is None:
                    errors.append(f"workflows.{workflow_name}.outputs.{name} has type array without items")

        if errors:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Leaf workflow '{leaf_name}' uses weak array output schemas: {'; '.join(errors)}")

    def _validate_strong_array_output_schema(self, definition: Any, path: str, errors: list[str]) -> None:
        type_name = self._normalize_workflow_schema_type(getattr(definition, "type", "any"))
        if type_name == "array":
            items = getattr(definition, "items", None)
            if items is None:
                errors.append(f"{path} has type array without items")
            else:
                item_type = self._normalize_workflow_schema_type(getattr(items, "type", "any"))
                if item_type == "any":
                    errors.append(f"{path}.items has type any; choose a concrete item schema")
                self._validate_strong_array_output_schema(items, path + ".items", errors)

        for name, child in (getattr(definition, "properties", None) or {}).items():
            self._validate_strong_array_output_schema(child, f"{path}.properties.{name}", errors)
        if getattr(definition, "additional_properties", None) is not None:
            self._validate_strong_array_output_schema(definition.additional_properties, path + ".additional_properties", errors)

    @staticmethod
    def _get_pipeline_generation_max_attempts(input_obj: dict[str, Any]) -> int:
        on_invalid = input_obj.get("on_invalid") if isinstance(input_obj.get("on_invalid"), dict) else {}
        return max(1, int(on_invalid.get("max_attempts", 3) or 3))

    def _build_main_assembly_prompt(
        self,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        normalized_markdown: str,
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
        configured_main_inputs: dict[str, Any],
        generated_leaf_inputs: dict[str, Any],
    ) -> str:
        parts = [
            "You are assembling the parent `main` workflow graph for a GnOuGo.Flow pipeline.",
            "Return ONLY one YAML mapping with `document` and `graph` keys. Do not return version, entrypoint, workflows, a full `main` workflow, or leaf workflow definitions.",
            "",
            "Hard rules:",
            "- Return a compact orchestration graph. The runtime will render the real `main` workflow and graft validated leaf workflows before final validation.",
            "- Graph call nodes must use `leaf: <leaf_name>` and `args`; do not write raw workflow.call refs.",
            "- Non-call support nodes may use normal step `type` and `input` when the main orchestration needs derived values, guards, switches, loops, or parallel branches.",
            "- The main workflow must never use `workflow.plan`, and graph nodes must not inline leaf logic.",
            "- Leaf workflows must never call other workflows.",
            "- Preserve the orchestration algorithm from the normalized prompt and the Main workflow orchestration section.",
            "- Use conditionals, switches, loops, or parallel branches when the orchestration requires them.",
            "- For container support nodes (`sequence`, `switch`, `parallel`, loops), nested graph nodes are allowed in `steps`, `branches[].steps`, `cases[].steps`, and `default`.",
            "- Pass leaf arguments from declared `data.inputs.<name>`, earlier step outputs, loop variables, derived values, or constants.",
            "- Every `data.inputs.<name>` reference MUST have an identically named declaration in `graph.inputs` or `document.skill.inputs`.",
            "- Leaf input names are call arguments, not automatically public main inputs.",
            "- `generated_leaf_contracts_yaml` is authoritative for leaf workflow names, call arguments, and available outputs.",
            "- If `leaf_input_candidates_yaml` or `leaf_subworkflow_specs_json` disagree with `generated_leaf_contracts_yaml`, follow `generated_leaf_contracts_yaml`.",
            "- Map public user input names to differently named leaf arguments when their meanings match.",
            "- Do not expose loop variables, intermediate values, paths, identifiers, flags, or leaf-only implementation details as public inputs unless the user explicitly requested them.",
            "- Use `set` support nodes for data shaping in the main graph: renaming fields, building objects/arrays, constants, and safe type conversions.",
            "- Keep exact JSON values intact when passing arrays, objects, numbers, or booleans. Do not stringify a structured leaf output unless a downstream leaf explicitly wants a string.",
            "- If a leaf call is inside a switch, loop, parallel branch, or conditional path, do not reference that leaf call step from outside that container/path. Put dependent work in the same path, or expose the container step itself as the output.",
        ]
        if configured_main_inputs:
            parts.append("- `authoritative_main_inputs_yaml` is exact: preserve every name and schema and do not add or remove inputs.")
        else:
            parts.append("- Infer public main inputs from the user's normalized request; do not expose leaf-only implementation details.")
        parts.extend(
            [
                "",
                self._prompt_section("configured_document_name", self._resolve_configured_pipeline_document_name(pipeline_input, generator) or ""),
                self._prompt_section("configured_skill_yaml", self._serialize_yaml_value(self._resolve_configured_skill(pipeline_input, generator) or {})),
                self._prompt_section("normalized_markdown", normalized_markdown),
                self._prompt_section("main_workflow_orchestration", extraction.main_workflow_prompt),
                self._prompt_section("authoritative_main_inputs_yaml", self._serialize_yaml_value(configured_main_inputs)),
                self._prompt_section("leaf_input_candidates_yaml", self._serialize_yaml_value(generated_leaf_inputs)),
                self._prompt_section("generated_leaf_contracts_yaml", self._serialize_yaml_value(self._build_generated_leaf_contracts(leaves))),
                self._prompt_section("leaf_subworkflow_specs_json", json.dumps(self._build_extraction_json(extraction), ensure_ascii=False, indent=2)),
                self._prompt_section("generated_leaf_workflows_yaml", "\n---\n".join(leaf.yaml_text for leaf in leaves)),
                "",
                "Output shape example:",
                "document:",
                "  name: example_pipeline",
                "  skill:",
                "    description: Process the user's query.",
                "    tags: [example, pipeline]",
                "    inputs:",
                "      user_query: string",
                "    outputs:",
                "      result: string",
                "graph:",
                "  inputs:",
                "    user_query: string",
                "  steps:",
                "    - id: call_example_leaf",
                "      leaf: example_leaf",
                "      args:",
                "        query: ${data.inputs.user_query}",
                "  outputs:",
                "    result: ${data.steps.call_example_leaf.outputs.result}",
                "",
                "Main graph boundaries:",
                "- Keep business/tool/LLM work inside leaf workflows. The main graph should only orchestrate, derive values, branch, loop, and call leaves.",
                "- If a value is required by a generated leaf input contract, pass it in the leaf args or derive it in an earlier support step.",
                "- Do not add MCP, LLM, template, human-input, workflow.plan, or raw workflow.call support nodes to the main graph.",
            ]
        )
        return "\n".join(parts)

    @staticmethod
    def _build_main_assembly_repair_prompt(base_prompt: str, previous_response: str | None, structured_error: str) -> str:
        parts = [
            base_prompt.rstrip(),
            "",
            "The previous main workflow assembly failed final validation.",
            "Return a complete corrected `document` and `graph` YAML mapping that still follows every rule above.",
            "Fix the reported error without changing the user's public contract or orchestration intent.",
        ]
        if previous_response:
            parts.extend(["<invalid_main_assembly_yaml>", WorkflowPlanExecutor._strip_markdown_code_fence(previous_response), "</invalid_main_assembly_yaml>"])
        parts.extend(["<main_assembly_validation_error>", structured_error, "</main_assembly_validation_error>"])
        return "\n".join(parts)

    def _parse_generated_main_assembly(self, text: str) -> _GeneratedMainAssembly:
        parsed = yaml.safe_load(self._strip_markdown_code_fence(textwrap.dedent(text))) or {}
        if not isinstance(parsed, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline main assembly response must be a YAML mapping.")
        document = parsed.get("document") if isinstance(parsed.get("document"), dict) else {}
        if isinstance(parsed.get("graph"), dict):
            skill = copy.deepcopy(document.get("skill")) if isinstance(document.get("skill"), dict) else None
            return _GeneratedMainAssembly(self._build_main_workflow_node_from_graph(parsed["graph"]), document.get("name"), skill)
        if isinstance(parsed.get("main"), dict):
            skill = copy.deepcopy(document.get("skill")) if isinstance(document.get("skill"), dict) else None
            return _GeneratedMainAssembly(copy.deepcopy(parsed["main"]), document.get("name"), skill)
        workflows = parsed.get("workflows")
        if isinstance(workflows, dict) and isinstance(workflows.get("main"), dict):
            skill = copy.deepcopy(parsed.get("skill")) if isinstance(parsed.get("skill"), dict) else None
            return _GeneratedMainAssembly(copy.deepcopy(workflows["main"]), parsed.get("name"), skill)
        skill = copy.deepcopy(parsed.get("skill")) if isinstance(parsed.get("skill"), dict) else None
        return _GeneratedMainAssembly(copy.deepcopy(parsed), parsed.get("name"), skill)

    def _build_main_workflow_node_from_graph(self, graph: dict[str, Any]) -> dict[str, Any]:
        main: dict[str, Any] = {}
        if isinstance(graph.get("inputs"), dict):
            main["inputs"] = copy.deepcopy(graph["inputs"])
        source_steps = graph.get("steps") if isinstance(graph.get("steps"), list) else graph.get("nodes")
        if not isinstance(source_steps, list):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph must include steps or nodes.")
        main["steps"] = self._render_graph_step_sequence(source_steps)
        if isinstance(graph.get("outputs"), dict):
            main["outputs"] = copy.deepcopy(graph["outputs"])
        return main

    def _render_graph_step_sequence(self, source_steps: list[Any]) -> list[dict[str, Any]]:
        rendered: list[dict[str, Any]] = []
        for source_step in source_steps:
            if not isinstance(source_step, dict):
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph steps must be mappings.")
            rendered.append(self._render_graph_step(source_step))
        return rendered

    def _render_graph_step(self, graph_step: dict[str, Any]) -> dict[str, Any]:
        leaf_name = graph_step.get("leaf") or graph_step.get("workflow")
        if isinstance(leaf_name, str) and leaf_name.strip():
            return self._render_graph_leaf_call_step(graph_step, leaf_name.strip())

        step_type = graph_step.get("type")
        if not isinstance(step_type, str) or not step_type.strip():
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph step must include either leaf or type.")
        if step_type == "workflow.plan":
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, "Pipeline orchestration graph must not contain workflow.plan.")
        if step_type == "workflow.call":
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, "Pipeline orchestration graph call nodes must use leaf and args, not raw workflow.call.")

        rendered = copy.deepcopy(graph_step)
        if isinstance(rendered.get("steps"), list):
            rendered["steps"] = self._render_graph_step_sequence(rendered["steps"])
        if isinstance(rendered.get("branches"), list):
            rendered["branches"] = self._render_graph_branch_sequence(rendered["branches"])
        if isinstance(rendered.get("cases"), list):
            rendered["cases"] = self._render_graph_case_sequence(rendered["cases"])
        if isinstance(rendered.get("default"), list):
            rendered["default"] = self._render_graph_step_sequence(rendered["default"])
        return rendered

    @staticmethod
    def _render_graph_leaf_call_step(graph_step: dict[str, Any], leaf_name: str) -> dict[str, Any]:
        step: dict[str, Any] = {
            "id": graph_step.get("id") if isinstance(graph_step.get("id"), str) and graph_step.get("id").strip() else f"call_{leaf_name}",
            "type": "workflow.call",
        }
        for common_field in ("if", "retry", "on_error", "output"):
            if common_field in graph_step:
                step[common_field] = copy.deepcopy(graph_step[common_field])
        step["input"] = {
            "ref": {"kind": "local", "name": leaf_name},
            "args": copy.deepcopy(graph_step.get("args") if isinstance(graph_step.get("args"), dict) else {}),
        }
        return step

    def _render_graph_branch_sequence(self, source_branches: list[Any]) -> list[dict[str, Any]]:
        rendered: list[dict[str, Any]] = []
        for source_branch in source_branches:
            if not isinstance(source_branch, dict):
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph branches must be mappings.")
            branch = copy.deepcopy(source_branch)
            if isinstance(branch.get("steps"), list):
                branch["steps"] = self._render_graph_step_sequence(branch["steps"])
            rendered.append(branch)
        return rendered

    def _render_graph_case_sequence(self, source_cases: list[Any]) -> list[dict[str, Any]]:
        rendered: list[dict[str, Any]] = []
        for source_case in source_cases:
            if not isinstance(source_case, dict):
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline orchestration graph cases must be mappings.")
            case = copy.deepcopy(source_case)
            if isinstance(case.get("steps"), list):
                case["steps"] = self._render_graph_step_sequence(case["steps"])
            rendered.append(case)
        return rendered

    def _compose_pipeline_workflow_yaml(
        self,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
        assembly: _GeneratedMainAssembly,
        main_inputs: dict[str, Any],
    ) -> str:
        document_name = self._resolve_configured_pipeline_document_name(pipeline_input, generator) or assembly.document_name or "generated-pipeline-workflow"
        main_node = copy.deepcopy(assembly.main_workflow_node)
        main_node["inputs"] = copy.deepcopy(main_inputs)
        root = {
            "version": 1,
            "name": document_name,
            "skill": self._build_pipeline_skill_node(document_name, pipeline_input, generator, extraction, main_inputs, assembly.skill_node),
            "entrypoint": "main",
            "workflows": {"main": main_node},
        }
        for leaf in leaves:
            root["workflows"][leaf.name] = copy.deepcopy(leaf.workflow_node)
        return yaml.dump(root, Dumper=_NoAliasDumper, sort_keys=False, allow_unicode=False).strip()

    def _build_pipeline_skill_node(
        self,
        document_name: str,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        extraction: _WorkflowPipelineExtraction,
        main_inputs: dict[str, Any],
        generated_skill: dict[str, Any] | None,
    ) -> dict[str, Any]:
        configured_skill = self._resolve_configured_skill(pipeline_input, generator) or {}
        description = (
            configured_skill.get("description")
            or pipeline_input.get("description")
            or generator.get("description")
            or (generated_skill or {}).get("description")
            or self._build_generated_pipeline_skill_description(document_name, pipeline_input, generator, extraction)
        )
        tags = configured_skill.get("tags") or (generated_skill or {}).get("tags") or ["generated", "pipeline"]
        outputs = {}
        for source in (generator.get("outputs"), configured_skill.get("outputs"), pipeline_input.get("outputs"), (generated_skill or {}).get("outputs")):
            if isinstance(source, dict):
                outputs.update(copy.deepcopy(source))
        if not outputs:
            for spec in extraction.subworkflows:
                outputs[f"{spec.name}_outputs"] = {"type": "object", "description": f"Outputs from the {spec.name} leaf workflow."}
        return {"description": description, "tags": tags, "inputs": copy.deepcopy(main_inputs), "outputs": outputs}

    @staticmethod
    def _resolve_configured_skill(pipeline_input: dict[str, Any], generator: dict[str, Any]) -> dict[str, Any] | None:
        skill = pipeline_input.get("skill") if isinstance(pipeline_input.get("skill"), dict) else generator.get("skill")
        return copy.deepcopy(skill) if isinstance(skill, dict) else None

    @staticmethod
    def _resolve_configured_pipeline_document_name(pipeline_input: dict[str, Any], generator: dict[str, Any]) -> str | None:
        for source in (pipeline_input, generator):
            for key in ("name", "workflow_name", "document_name"):
                value = source.get(key)
                if isinstance(value, str) and value.strip():
                    return value.strip()
        return None

    @staticmethod
    def _build_generated_pipeline_skill_description(
        document_name: str,
        pipeline_input: dict[str, Any],
        generator: dict[str, Any],
        extraction: _WorkflowPipelineExtraction,
    ) -> str:
        source = (
            pipeline_input.get("raw_prompt")
            or generator.get("raw_prompt")
            or generator.get("instruction")
            or extraction.main_workflow_prompt
            or document_name
        )
        first_line = next((line.strip() for line in str(source).splitlines() if line.strip() and not line.strip().startswith("#")), "")
        return first_line[:177] + "..." if len(first_line) > 180 else first_line or f"Generated pipeline workflow for {document_name}."

    @staticmethod
    def _build_configured_main_input_contract(pipeline_input: dict[str, Any], generator: dict[str, Any]) -> dict[str, Any]:
        inputs: dict[str, Any] = {}
        for source in (
            (generator.get("skill") if isinstance(generator.get("skill"), dict) else {}).get("inputs"),
            generator.get("inputs"),
            (pipeline_input.get("skill") if isinstance(pipeline_input.get("skill"), dict) else {}).get("inputs"),
            pipeline_input.get("inputs"),
        ):
            if isinstance(source, dict):
                inputs.update(copy.deepcopy(source))
        return inputs

    @staticmethod
    def _build_generated_leaf_contracts(leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        contracts: dict[str, Any] = {}
        for leaf in leaves:
            inputs = leaf.workflow_node.get("inputs")
            outputs = leaf.workflow_node.get("outputs")
            contracts[leaf.name] = {
                "workflow": leaf.workflow_name,
                "inputs": copy.deepcopy(inputs) if isinstance(inputs, dict) else {},
                "outputs": copy.deepcopy(outputs) if isinstance(outputs, dict) else {},
            }
        return contracts

    @staticmethod
    def _build_generated_main_input_contract(leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        inputs: dict[str, Any] = {}
        available_outputs: set[str] = set()
        for leaf in leaves:
            leaf_inputs = leaf.workflow_node.get("inputs") if isinstance(leaf.workflow_node.get("inputs"), dict) else {}
            for name, schema in leaf_inputs.items():
                if name in available_outputs:
                    continue
                if name not in inputs:
                    inputs[name] = copy.deepcopy(schema)
                elif inputs[name] != schema:
                    inputs[name] = "any"
            leaf_outputs = leaf.workflow_node.get("outputs") if isinstance(leaf.workflow_node.get("outputs"), dict) else {}
            available_outputs.update(leaf_outputs.keys())
        return inputs

    def _resolve_main_input_contract(
        self,
        configured_inputs: dict[str, Any],
        assembly: _GeneratedMainAssembly,
        generated_leaf_inputs: dict[str, Any],
    ) -> dict[str, Any]:
        if configured_inputs:
            return copy.deepcopy(configured_inputs)
        inputs = assembly.main_workflow_node.get("inputs")
        if isinstance(inputs, dict) and inputs:
            return copy.deepcopy(inputs)
        generated_skill_inputs = assembly.skill_node.get("inputs") if isinstance(assembly.skill_node, dict) else None
        if isinstance(generated_skill_inputs, dict) and generated_skill_inputs:
            return copy.deepcopy(generated_skill_inputs)
        return copy.deepcopy(generated_leaf_inputs)

    @staticmethod
    def _ensure_main_workflow_outputs(main_node: dict[str, Any], specs: list[_WorkflowPipelineSubworkflowSpec]) -> None:
        if isinstance(main_node.get("outputs"), dict):
            return
        main_node["outputs"] = WorkflowPlanExecutor._build_default_main_outputs(main_node, specs)

    @staticmethod
    def _build_default_main_outputs(main_node: dict[str, Any], specs: list[_WorkflowPipelineSubworkflowSpec]) -> dict[str, str]:
        outputs: dict[str, str] = {}
        top_level_steps = main_node.get("steps")
        if isinstance(top_level_steps, list):
            for step_id, leaf_name in WorkflowPlanExecutor._enumerate_top_level_workflow_calls(top_level_steps):
                key = WorkflowPlanExecutor._build_unique_key(outputs, f"{leaf_name}_outputs")
                outputs[key] = f"${{data.steps.{step_id}.outputs}}"
            if outputs:
                return outputs

            for step in top_level_steps:
                if not isinstance(step, dict):
                    continue
                step_id = step.get("id")
                if not isinstance(step_id, str) or not step_id.strip():
                    continue
                key = WorkflowPlanExecutor._build_unique_key(outputs, f"{step_id}_output")
                outputs[key] = f"${{data.steps.{step_id}}}"
            if outputs:
                return outputs

        for spec in specs:
            key = WorkflowPlanExecutor._build_unique_key(outputs, f"{spec.name}_outputs")
            outputs[key] = f"${{data.steps.call_{spec.name}.outputs}}"
        return outputs

    @staticmethod
    def _enumerate_top_level_workflow_calls(steps: list[Any]) -> list[tuple[str, str]]:
        calls: list[tuple[str, str]] = []
        for step in steps:
            if not isinstance(step, dict) or step.get("type") != "workflow.call":
                continue
            step_id = step.get("id")
            input_obj = step.get("input") if isinstance(step.get("input"), dict) else {}
            ref = input_obj.get("ref") if isinstance(input_obj, dict) and isinstance(input_obj.get("ref"), dict) else {}
            leaf_name = ref.get("name") if isinstance(ref, dict) else None
            if isinstance(step_id, str) and step_id.strip() and isinstance(leaf_name, str) and leaf_name.strip():
                calls.append((step_id, leaf_name))
        return calls

    @staticmethod
    def _build_unique_key(mapping: dict[str, Any], requested_key: str) -> str:
        if requested_key not in mapping:
            return requested_key
        index = 2
        while f"{requested_key}_{index}" in mapping:
            index += 1
        return f"{requested_key}_{index}"

    @staticmethod
    def _validate_declared_main_input_references(main_node: dict[str, Any], main_inputs: dict[str, Any]) -> None:
        dumped = yaml.dump(main_node, Dumper=_NoAliasDumper, sort_keys=False, allow_unicode=False)
        undeclared = sorted(set(re.findall(r"\bdata\.inputs\.([A-Za-z_][A-Za-z0-9_]*)", dumped)) - set(main_inputs.keys()))
        if undeclared:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Pipeline main workflow references undeclared inputs: " + ", ".join(undeclared))

    def _enforce_pipeline_workflow_hierarchy(self, doc: WorkflowDocument, leaf_names: set[str]) -> None:
        for workflow_name, workflow in doc.workflows.items():
            for step in self._enumerate_steps(workflow.steps):
                if workflow_name != "main" and step.type in {"workflow.call", "workflow.plan"}:
                    raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, f"Leaf workflow '{workflow_name}' must not contain step type '{step.type}'.")
                if workflow_name == "main" and step.type == "workflow.call" and isinstance(step.input, dict):
                    ref = step.input.get("ref")
                    if not isinstance(ref, dict) or str(ref.get("kind", "local")) != "local" or ref.get("name") not in leaf_names:
                        raise WorkflowRuntimeException(
                            ErrorCodes.TEMPLATE_POLICY,
                            f"Pipeline main workflow step '{step.id}' must call a generated local leaf workflow.",
                        )

    def _validate_pipeline_leaf_call_arguments(self, doc: WorkflowDocument, leaves: list[_GeneratedLeafWorkflow]) -> None:
        main = doc.workflows.get("main")
        if main is None:
            return

        required_inputs_by_leaf = {
            leaf.name: [
                input_name
                for input_name, schema in self._build_leaf_input_schema_map(leaf).items()
                if self._is_required_leaf_input(schema)
            ]
            for leaf in leaves
        }

        for step in self._enumerate_steps(main.steps):
            if step.type != "workflow.call" or not isinstance(step.input, dict):
                continue
            ref = step.input.get("ref")
            target_name = ref.get("name") if isinstance(ref, dict) else None
            if not isinstance(target_name, str) or target_name not in required_inputs_by_leaf:
                continue
            args = step.input.get("args") if isinstance(step.input.get("args"), dict) else {}
            missing = sorted(name for name in required_inputs_by_leaf[target_name] if name not in args)
            if missing:
                raise WorkflowRuntimeException(
                    ErrorCodes.TEMPLATE_PLAN,
                    f"Pipeline main workflow call '{step.id}' to leaf '{target_name}' is missing required leaf argument(s): {', '.join(missing)}",
                )

    def _validate_pipeline_main_graph_boundaries(self, doc: WorkflowDocument) -> None:
        main = doc.workflows.get("main")
        if main is None:
            return
        forbidden = {"mcp.call", "llm.call", "template.render", "human.input", "workflow.plan"}
        for step in self._enumerate_steps(main.steps):
            if step.type in forbidden:
                raise WorkflowRuntimeException(
                    ErrorCodes.TEMPLATE_POLICY,
                    f"Pipeline main workflow must not contain step type '{step.type}'. Keep business/tool/LLM work inside leaf workflows.",
                )

    def _validate_pipeline_main_leaf_output_contracts(self, doc: WorkflowDocument, leaves: list[_GeneratedLeafWorkflow]) -> None:
        main = doc.workflows.get("main")
        if main is None:
            return
        leaf_by_name = {leaf.name: leaf for leaf in leaves}
        for step in self._enumerate_steps(main.steps):
            if step.type != "workflow.call" or not isinstance(step.input, dict):
                continue
            ref = step.input.get("ref")
            leaf_name = ref.get("name") if isinstance(ref, dict) else None
            leaf = leaf_by_name.get(leaf_name)
            if leaf is None:
                continue
            outputs = leaf.workflow_node.get("outputs") if isinstance(leaf.workflow_node.get("outputs"), dict) else {}
            for output_name, output_schema in outputs.items():
                if isinstance(output_schema, dict) and is_weak_yaml_output_schema(output_schema):
                    raise WorkflowRuntimeException(
                        ErrorCodes.TEMPLATE_PLAN,
                        f"WEAK_OUTPUT_SCHEMA: Pipeline leaf '{leaf_name}' output '{output_name}' is too weak for main workflow assembly.",
                    )

    @staticmethod
    def _validate_pipeline_main_dataflow_quality(doc: WorkflowDocument) -> None:
        validate_external_artifact_readiness(doc)

    @staticmethod
    def _build_leaf_input_schema_map(leaf: _GeneratedLeafWorkflow) -> dict[str, Any]:
        inputs = leaf.workflow_node.get("inputs")
        return copy.deepcopy(inputs) if isinstance(inputs, dict) else {}

    @staticmethod
    def _is_required_leaf_input(schema: Any) -> bool:
        if not isinstance(schema, dict):
            return True
        if schema.get("required") is False:
            return False
        if "default" in schema:
            return False
        return True

    @staticmethod
    def _build_extraction_json(extraction: _WorkflowPipelineExtraction) -> dict[str, Any]:
        return {
            "subworkflows": [
                {
                    "name": spec.name,
                    "goal": spec.goal,
                    "description": spec.description,
                    "work_kind": spec.work_kind,
                    "contract_role": spec.contract_role,
                    "concrete_outcome": spec.concrete_outcome,
                    "inputs": spec.inputs,
                    "outputs": spec.outputs,
                    "input_schemas": copy.deepcopy(spec.input_schemas),
                    "output_schemas": copy.deepcopy(spec.output_schemas),
                    "extract_reason": spec.extract_reason,
                    "content": spec.content,
                    "planned_tools": [
                        {
                            "server": tool.server,
                            "kind": tool.kind,
                            "method": tool.method,
                            "required": tool.required,
                            "purpose": tool.purpose,
                            "consumes": list(tool.consumes),
                            "produces": list(tool.produces),
                        }
                        for tool in spec.planned_tools
                    ],
                    "required_capabilities": WorkflowPlanExecutor._required_capabilities_json(spec),
                    "extraction_score": None,
                }
                for spec in extraction.subworkflows
            ],
            "main_workflow_prompt": extraction.main_workflow_prompt,
            "validation": WorkflowPlanExecutor._build_validation_json(extraction.validation_errors),
            "validation_errors": extraction.validation_errors,
            "root_causes": copy.deepcopy(extraction.root_causes),
            "quality_review": copy.deepcopy(extraction.quality_review),
            "quality_warnings": [],
        }

    @staticmethod
    def _build_validation_json(errors: list[str]) -> dict[str, Any]:
        return {"ok": len(errors) == 0, "errors": list(errors), "error_count": len(errors)}

    def _build_pipeline_quality_report(
        self,
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
        main_retry_count: int,
        final_doc: WorkflowDocument,
    ) -> dict[str, Any]:
        main = final_doc.workflows.get("main")
        main_steps = list(self._enumerate_steps(main.steps)) if main is not None else []
        total_step_count = sum(1 for workflow in final_doc.workflows.values() for _ in self._enumerate_steps(workflow.steps))
        main_dataflow_diagnostics = analyze_external_artifact_readiness(final_doc)
        root_causes = list(copy.deepcopy(extraction.root_causes))
        if main_dataflow_diagnostics:
            root_causes.extend(build_main_dataflow_quality_details(main_dataflow_diagnostics).get("root_causes", []))
        repair_count = main_retry_count
        extraction_quality_reviewed = isinstance(extraction.quality_review, dict) and extraction.quality_review.get("verdict") != "warning"
        warnings = [
            {"code": "PIPELINE_EXTRACTION_VALIDATION_ERROR", "message": error}
            for error in extraction.validation_errors
        ]
        if isinstance(extraction.quality_review, dict):
            for diagnostic in extraction.quality_review.get("diagnostics", []) if isinstance(extraction.quality_review.get("diagnostics"), list) else []:
                if isinstance(diagnostic, dict) and str(diagnostic.get("severity") or "").lower() == "warning":
                    warnings.append(
                        {
                            "code": diagnostic.get("code") or "PIPELINE_EXTRACTION_QUALITY_WARNING",
                            "leaf": diagnostic.get("leaf_name") or "",
                            "message": diagnostic.get("message") or "",
                            "recommendation": diagnostic.get("recommendation") or "",
                        }
                    )
        skill_outputs = self._build_output_schema_map(final_doc.skill.outputs) if final_doc.skill and final_doc.skill.outputs else {}
        main_outputs = self._build_output_schema_map(main.outputs, final_doc.skill.outputs if final_doc.skill else None) if main and main.outputs else {}
        planned_tool_count = sum(len(spec.planned_tools) for spec in extraction.subworkflows)
        required_planned_tool_count = sum(1 for spec in extraction.subworkflows for tool in spec.planned_tools if tool.required)
        return {
            "status": "passed" if not main_dataflow_diagnostics and not extraction.validation_errors else "warning",
            "summary": {
                "workflow_count": len(final_doc.workflows),
                "leaf_count": len(leaves),
                "leaf_subworkflow_count": len(leaves),
                "leaf_blueprint_count": len(leaves),
                "main_step_count": len(main_steps),
                "total_step_count": total_step_count,
                "external_work_leaf_count": sum(1 for spec in extraction.subworkflows if spec.work_kind == "external_work"),
                "deterministic_shaping_leaf_count": sum(1 for spec in extraction.subworkflows if spec.work_kind == "deterministic_shaping"),
                "orchestration_leaf_count": sum(1 for spec in extraction.subworkflows if spec.work_kind == "orchestration"),
                "unknown_work_kind_leaf_count": sum(1 for spec in extraction.subworkflows if not spec.work_kind),
                "planned_tool_count": planned_tool_count,
                "required_planned_tool_count": required_planned_tool_count,
                "skill_output_count": len(skill_outputs),
                "main_output_count": len(main_outputs),
                "main_retry_count": main_retry_count,
                "repair_count": repair_count,
                "leaf_contract_repair_count": 0,
                "warning_count": len(warnings),
                "root_cause_count": len(root_causes),
                "extraction_quality_score": (extraction.quality_review or {}).get("score") if isinstance(extraction.quality_review, dict) else None,
            },
            "checks": {
                "extraction_validated": True,
                "leaf_intent_validated": True,
                "leaf_contracts_validated": True,
                "structured_extraction_validated": any(spec.output_schemas or spec.planned_tools for spec in extraction.subworkflows),
                "planned_tools_validated": any(spec.planned_tools for spec in extraction.subworkflows),
                "extraction_quality_reviewed": extraction_quality_reviewed,
                "main_dataflow_validated": True,
                "strong_output_schemas_validated": True,
                "workflow_hierarchy_validated": True,
            },
            "extraction": {
                "main_workflow_prompt": extraction.main_workflow_prompt,
                "validation": self._build_validation_json(extraction.validation_errors),
                "quality_review": copy.deepcopy(extraction.quality_review),
                "validation_errors": list(extraction.validation_errors),
                "root_causes": root_causes,
            },
            "leaves": self._build_pipeline_quality_leaves(extraction, leaves),
            "contracts": {
                "skill_outputs": skill_outputs,
                "main_outputs": main_outputs,
                "leaf_outputs": self._build_pipeline_quality_leaf_outputs(leaves),
            },
            "root_causes": root_causes,
            "repairs": [],
            "events": [],
            "warnings": warnings,
            "mcp_context": {},
        }

    def _build_pipeline_inspection(
        self,
        normalized_markdown: str,
        annotated_markdown: str,
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
        main_retry_count: int,
        final_doc: WorkflowDocument,
        pipeline_mcp_doc: str | None,
        pipeline_mcp_tool_contracts: list[McpToolOutputContract],
    ) -> dict[str, Any]:
        main = final_doc.workflows.get("main")
        root_causes = list(copy.deepcopy(extraction.root_causes))
        return {
            "summary": {
                "leaf_count": len(leaves),
                "leaf_blueprint_count": len(leaves),
                "main_retry_count": main_retry_count,
                "repair_count": main_retry_count,
                "root_cause_count": len(extraction.root_causes),
                "main_step_count": len(main.steps) if main is not None else 0,
                "workflow_count": len(final_doc.workflows),
            },
            "mcp_context": self._build_pipeline_mcp_context_json(pipeline_mcp_doc, pipeline_mcp_tool_contracts),
            "normalized_prompt": normalized_markdown,
            "annotated_markdown": annotated_markdown,
            "extraction_quality_review": copy.deepcopy(extraction.quality_review),
            "leaf_manifest": self._build_generated_leaf_manifest_json(leaves, extraction),
            "generated_leaf_blueprints": self._build_generated_leaf_blueprints_json(leaves),
            "generated_leaf_contracts": self._build_generated_leaf_contracts_json(leaves),
            "final_main_graph": (
                {"missing": True}
                if main is None
                else self._build_workflow_graph_inspection_json("main", main, final_doc.skill.outputs if final_doc.skill else None)
            ),
            "repair_history": [],
            "root_causes": root_causes,
        }

    @staticmethod
    def _build_pipeline_mcp_context_json(
        pipeline_mcp_doc: str | None,
        pipeline_mcp_tool_contracts: list[McpToolOutputContract],
    ) -> dict[str, Any]:
        server_names = sorted({contract.server_name for contract in pipeline_mcp_tool_contracts})
        tool_names = sorted({f"{contract.server_name}/{contract.tool_name}" for contract in pipeline_mcp_tool_contracts})
        servers = [
            {
                "name": server_name,
                "discovered": True,
                "tool_count": sum(1 for contract in pipeline_mcp_tool_contracts if contract.server_name == server_name),
                "prompt_count": 0,
                "tools": sorted(contract.tool_name for contract in pipeline_mcp_tool_contracts if contract.server_name == server_name),
                "prompts": [],
            }
            for server_name in server_names
        ]
        return {
            "available": bool(server_names),
            "selected_server_count": len(server_names),
            "selected_tool_count": len(tool_names),
            "selected_prompt_count": 0,
            "server_names": server_names,
            "tool_names": tool_names,
            "prompt_names": [],
            "servers": servers,
            "has_documentation": bool(pipeline_mcp_doc and pipeline_mcp_doc.strip()),
        }

    def _build_pipeline_quality_leaves(
        self,
        extraction: _WorkflowPipelineExtraction,
        leaves: list[_GeneratedLeafWorkflow],
    ) -> list[dict[str, Any]]:
        leaves_by_name = {leaf.name: leaf for leaf in leaves}
        result: list[dict[str, Any]] = []
        for spec in extraction.subworkflows:
            leaf = leaves_by_name.get(spec.name)
            item: dict[str, Any] = {
                "name": spec.name,
                "goal": spec.goal,
                "description": spec.description,
                "work_kind": spec.work_kind,
                "contract_role": spec.contract_role,
                "concrete_outcome": spec.concrete_outcome,
                "extract_reason": spec.extract_reason,
                "extraction_score": None,
                "planned_tools": self._planned_tools_json(spec.planned_tools),
                "required_capabilities": self._required_capabilities_json(spec),
                "required_planned_tool_count": sum(1 for tool in spec.planned_tools if tool.required),
                "declared_input_schemas": copy.deepcopy(spec.input_schemas),
                "declared_output_schemas": copy.deepcopy(spec.output_schemas),
                "generated": leaf is not None,
            }
            if leaf is not None:
                workflow = leaf.document.workflows.get(leaf.workflow_name)
                steps = list(self._enumerate_steps(workflow.steps)) if workflow else []
                item.update(
                    {
                        "generated_workflow_name": leaf.workflow_name,
                        "step_count": len(steps),
                        "action_step_count": sum(1 for step in steps if self._is_executable_action_step_type(step.type)),
                        "blueprint": self._build_pipeline_leaf_blueprint_json(leaf),
                        "input_contracts": self._build_leaf_input_contracts(leaf),
                        "output_contracts": self._build_leaf_output_contracts(leaf),
                    }
                )
            result.append(item)
        return result

    @staticmethod
    def _is_executable_action_step_type(step_type: str) -> bool:
        return step_type not in {"set", "emit", "switch", "parallel", "loop.sequential", "loop.parallel"}

    def _build_pipeline_quality_leaf_outputs(self, leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        return {
            leaf.name: {
                "generated_workflow_name": leaf.workflow_name,
                "outputs": self._build_leaf_output_contracts(leaf),
            }
            for leaf in leaves
        }

    def _build_generated_leaf_manifest_json(
        self,
        leaves: list[_GeneratedLeafWorkflow],
        extraction: _WorkflowPipelineExtraction,
    ) -> dict[str, Any]:
        specs_by_name = {spec.name: spec for spec in extraction.subworkflows}
        leaf_items: list[dict[str, Any]] = []
        for leaf in leaves:
            spec = specs_by_name.get(leaf.name)
            leaf_items.append(
                {
                    "name": leaf.name,
                    "workflow": leaf.name,
                    "generated_workflow": leaf.workflow_name,
                    "goal": spec.goal if spec else "",
                    "description": spec.description if spec else "",
                    "work_kind": spec.work_kind if spec else "",
                    "contract_role": spec.contract_role if spec else "",
                    "concrete_outcome": spec.concrete_outcome if spec else "",
                    "extraction_score": None,
                    "extract_reason": spec.extract_reason if spec else "",
                    "planned_tools": self._planned_tools_json(spec.planned_tools) if spec else [],
                    "required_capabilities": self._required_capabilities_json(spec) if spec else [],
                    "blueprint": self._build_pipeline_leaf_blueprint_json(leaf),
                    "inputs": self._build_leaf_input_contracts(leaf),
                    "outputs": self._build_leaf_output_contracts(leaf),
                }
            )
        return {"leaves": leaf_items, "main_workflow_prompt": extraction.main_workflow_prompt}

    def _build_generated_leaf_blueprints_json(self, leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        return {leaf.name: self._build_pipeline_leaf_blueprint_json(leaf) for leaf in leaves}

    def _build_generated_leaf_contracts_json(self, leaves: list[_GeneratedLeafWorkflow]) -> dict[str, Any]:
        return {
            leaf.name: {
                "workflow": leaf.name,
                "generated_workflow": leaf.workflow_name,
                "inputs": self._build_leaf_input_contracts(leaf),
                "outputs": self._build_leaf_output_contracts(leaf),
            }
            for leaf in leaves
        }

    def _build_pipeline_leaf_blueprint_json(self, leaf: _GeneratedLeafWorkflow) -> dict[str, Any]:
        workflow = leaf.document.workflows.get(leaf.workflow_name)
        steps = workflow.steps if workflow is not None else []
        return {
            "leaf": leaf.name,
            "workflow_name": leaf.workflow_name,
            "summary": (leaf.spec.goal if leaf.spec else leaf.name),
            "steps": [self._build_step_inspection_json(step) for step in steps],
            "outputs": self._build_leaf_output_contracts(leaf),
        }

    def _build_workflow_graph_inspection_json(
        self,
        workflow_name: str,
        workflow: WorkflowDef,
        skill_outputs: dict[str, OutputDef] | None,
    ) -> dict[str, Any]:
        return {
            "workflow": workflow_name,
            "has_functions": bool(workflow.functions and workflow.functions.strip()),
            "inputs": self._build_input_schema_map(workflow.inputs or {}),
            "steps": [self._build_step_inspection_json(step) for step in workflow.steps],
            "outputs": self._build_workflow_output_inspection_json(workflow.outputs or {}, skill_outputs),
        }

    def _build_step_inspection_json(self, step: StepDef) -> dict[str, Any]:
        obj: dict[str, Any] = {"id": step.id, "type": step.type}
        if step.if_:
            obj["if"] = step.if_
        if step.output:
            obj["output"] = step.output
        if step.type == "workflow.call" and isinstance(step.input, dict):
            ref = step.input.get("ref") if isinstance(step.input.get("ref"), dict) else {}
            leaf_name = ref.get("name") if isinstance(ref, dict) else None
            if isinstance(leaf_name, str):
                obj["leaf"] = leaf_name
            args = step.input.get("args")
            if isinstance(args, dict):
                obj["args"] = copy.deepcopy(args)
        elif step.input is not None:
            obj["input"] = copy.deepcopy(step.input)
        if step.steps:
            obj["steps"] = [self._build_step_inspection_json(child) for child in step.steps]
        if step.default:
            obj["default"] = [self._build_step_inspection_json(child) for child in step.default]
        if step.branches:
            obj["branches"] = [
                {"index": index, "steps": [self._build_step_inspection_json(child) for child in branch.steps]}
                for index, branch in enumerate(step.branches)
            ]
        if step.cases:
            cases: list[dict[str, Any]] = []
            for case in step.cases:
                case_obj: dict[str, Any] = {"steps": [self._build_step_inspection_json(child) for child in case.steps]}
                if case.value:
                    case_obj["value"] = case.value
                if case.when:
                    case_obj["when"] = case.when
                cases.append(case_obj)
            obj["cases"] = cases
        return obj

    def _build_workflow_output_inspection_json(
        self,
        outputs: dict[str, OutputDef],
        skill_outputs: dict[str, OutputDef] | None,
    ) -> dict[str, Any]:
        schemas = self._build_output_schema_map(outputs, skill_outputs)
        return {
            name: {"expr": output.expr, "schema": copy.deepcopy(schemas.get(name) or self._output_def_to_contract_node(output))}
            for name, output in outputs.items()
        }

    def _build_leaf_input_contracts(self, leaf: _GeneratedLeafWorkflow) -> dict[str, Any]:
        workflow = leaf.document.workflows.get(leaf.workflow_name)
        if workflow and workflow.inputs:
            return self._build_input_schema_map(workflow.inputs)
        inputs = leaf.workflow_node.get("inputs") if isinstance(leaf.workflow_node.get("inputs"), dict) else {}
        return copy.deepcopy(inputs)

    def _build_leaf_output_contracts(self, leaf: _GeneratedLeafWorkflow) -> dict[str, Any]:
        workflow = leaf.document.workflows.get(leaf.workflow_name)
        if workflow and workflow.outputs:
            return self._build_output_schema_map(workflow.outputs, leaf.document.skill.outputs if leaf.document.skill else None)
        outputs = leaf.workflow_node.get("outputs") if isinstance(leaf.workflow_node.get("outputs"), dict) else {}
        return copy.deepcopy(outputs)

    @staticmethod
    def _build_input_schema_map(inputs: dict[str, InputDef]) -> dict[str, Any]:
        return {name: WorkflowPlanExecutor._input_def_to_contract_node(definition) for name, definition in inputs.items()}

    @staticmethod
    def _build_output_schema_map(
        outputs: dict[str, OutputDef],
        skill_outputs: dict[str, OutputDef] | None = None,
    ) -> dict[str, Any]:
        schemas: dict[str, Any] = {}
        for name, definition in outputs.items():
            contract = definition
            if (
                WorkflowPlanExecutor._is_opaque_output_schema(definition)
                and skill_outputs is not None
                and name in skill_outputs
                and not WorkflowPlanExecutor._is_opaque_output_schema(skill_outputs[name])
            ):
                contract = skill_outputs[name]
            schemas[name] = WorkflowPlanExecutor._output_def_to_contract_node(contract)
        return schemas

    @staticmethod
    def _input_def_to_contract_node(definition: InputDef) -> Any:
        node = WorkflowPlanExecutor._type_def_to_contract_node(definition)
        if isinstance(node, dict) and not definition.required:
            node["required"] = False
        elif not definition.required:
            node = {"type": str(node), "required": False}
        return node

    @staticmethod
    def _output_def_to_contract_node(definition: OutputDef) -> Any:
        return WorkflowPlanExecutor._type_def_to_contract_node(definition)

    @staticmethod
    def _type_def_to_contract_node(definition: InputDef | OutputDef) -> Any:
        type_name = WorkflowPlanExecutor._normalize_workflow_schema_type(getattr(definition, "type", "any"))
        has_children = any(
            [
                getattr(definition, "description", None),
                getattr(definition, "items", None) is not None,
                getattr(definition, "properties", None),
                getattr(definition, "additional_properties", None) is not None,
                getattr(definition, "required_properties", None),
            ]
        )
        if type_name in {"string", "number", "integer", "boolean"} and not has_children:
            return type_name
        node: dict[str, Any] = {"type": type_name}
        description = getattr(definition, "description", None)
        if isinstance(description, str) and description.strip():
            node["description"] = description
        items = getattr(definition, "items", None)
        if items is not None:
            node["items"] = WorkflowPlanExecutor._type_def_to_contract_node(items)
        properties = getattr(definition, "properties", None)
        if properties:
            node["properties"] = {
                name: WorkflowPlanExecutor._type_def_to_contract_node(child)
                for name, child in properties.items()
            }
        additional_properties = getattr(definition, "additional_properties", None)
        if additional_properties is not None:
            node["additional_properties"] = WorkflowPlanExecutor._type_def_to_contract_node(additional_properties)
        required_properties = getattr(definition, "required_properties", None)
        if required_properties:
            node["required_properties"] = list(required_properties)
        return node

    @staticmethod
    def _is_opaque_output_schema(definition: OutputDef) -> bool:
        return (
            WorkflowPlanExecutor._normalize_workflow_schema_type(definition.type) == "any"
            and not definition.description
            and definition.items is None
            and definition.properties is None
            and definition.additional_properties is None
            and not definition.required_properties
        )

    @staticmethod
    def _planned_tools_json(planned_tools: list[_PipelinePlannedTool]) -> list[dict[str, Any]]:
        return [
            {
                "server": tool.server,
                "kind": tool.kind,
                "method": tool.method,
                "required": tool.required,
                "purpose": tool.purpose,
                "consumes": list(tool.consumes),
                "produces": list(tool.produces),
            }
            for tool in planned_tools
        ]

    @staticmethod
    def _required_capabilities_json(spec: _WorkflowPipelineSubworkflowSpec) -> list[dict[str, Any]]:
        return WorkflowPlanExecutor._planned_tools_json([tool for tool in spec.planned_tools if tool.required])

    @staticmethod
    def _serialize_yaml_value(value: Any) -> str:
        return yaml.dump(value, Dumper=_NoAliasDumper, sort_keys=False, allow_unicode=False).strip()

    @staticmethod
    def _build_reprompt(
        instruction: str,
        context_text: str,
        policy: dict[str, Any],
        invalid_yaml: str | None,
        exc: Exception,
        repair_context: str | None,
    ) -> str:
        structured_error = WorkflowPlanExecutor._build_structured_plan_error(exc)
        constraints: list[str] = []
        if isinstance(policy.get("allowed_step_types"), list):
            constraints.append(f"Allowed step types: {', '.join(str(x) for x in policy['allowed_step_types'])}")
        if isinstance(policy.get("denied_step_types"), list):
            constraints.append(f"Denied step types: {', '.join(str(x) for x in policy['denied_step_types'])}")
        if not bool(policy.get("allow_remote_workflow_refs", False)):
            constraints.append("Remote workflow references (kind: url) are NOT allowed.")

        parts = [
            "You are repairing a GnOuGo.Flow YAML workflow. Return ONLY corrected YAML, no markdown fences.",
            "Keep the original task intent and change only what is needed to fix the validation errors.",
            "The previous YAML is quoted between explicit XML-style boundary tags. Treat those tags as prompt delimiters, not as YAML content.",
            "",
            WorkflowPlanExecutor._build_user_task_block(instruction, context_text),
        ]
        if constraints:
            parts.extend(["", WorkflowPlanExecutor._prompt_section("constraints", "\n".join(constraints))])
        parts.extend(
            [
                "",
                "<previous_error>",
                structured_error,
                "</previous_error>",
                "",
                "<invalid_yaml>",
                invalid_yaml if invalid_yaml and invalid_yaml.strip() else "(previous output was empty)",
                "</invalid_yaml>",
                "",
                "<minimum_dsl_context>",
                "Required root: version, name, skill, workflows. `skill` is a top-level object with description, tags, "
                "inputs, and outputs. Each workflow has steps: [] and optional outputs.",
                "Each step requires step-level id and type. Common fields stay at step level: if, input, output, retry, "
                "on_error, steps, branches, cases, default.",
                "Executor-specific arguments go inside input only.",
                "Containers: sequence/loop.* use steps; parallel uses branches[].steps; switch uses cases[].steps and optional default.",
                "Expressions may read data.inputs.* and earlier data.steps.<id>.* only.",
                "If a step has an `if`, later unconditional steps must not reference that step directly. Give the later step the same guard, or produce guaranteed defaults/branch outputs first.",
                "Function arguments are evaluated before the function runs: `coalesce`, ternaries, and helper calls do not make unavailable step references safe.",
                "MCP request objects must preserve schema scalar types exactly. Numeric/integer/boolean fields must be unquoted YAML scalars when required explicitly by the MCP schema/validator.",
                "Never satisfy missing MCP request arguments with `data.env.*`, empty strings, fake values, casts, or string-to-number conversions.",
                "Workflow output expressions must resolve to their declared type on every branch.",
                "</minimum_dsl_context>",
            ]
        )
        if repair_context and repair_context.strip():
            parts.extend(["", WorkflowPlanExecutor._prompt_section("relevant_repair_context", repair_context)])
        parts.extend(["", "Fix the issues above and generate a corrected YAML."])
        return "\n".join(parts)

    @staticmethod
    def _build_user_task_block(instruction: str, context_text: str | None) -> str:
        parts = ["<task>", "<user_prompt>", instruction, "</user_prompt>"]
        if context_text and context_text.strip():
            parts.extend(["<user_context>", context_text, "</user_context>"])
        parts.append("</task>")
        return "\n".join(parts)

    @staticmethod
    def _prompt_section(tag_name: str, content: str | None) -> str:
        body = (content or "").rstrip()
        return f"<{tag_name}>\n{body}\n</{tag_name}>"

    @staticmethod
    def _remove_markdown_fence_lines(value: str) -> str:
        if not value or "```" not in value:
            return value
        normalized = value.replace("\r\n", "\n").replace("\r", "\n")
        return "\n".join(line for line in normalized.split("\n") if not line.lstrip().startswith("```"))

    @staticmethod
    def _get_configured_mcp_server_metadata(ctx: StepExecutionContext) -> list[McpServerMetadata]:
        factory = ctx.engine.mcp_client_factory
        if factory is None:
            return []
        return list(getattr(factory, "server_metadata", []) or [])

    @staticmethod
    def _extract_required_mcp_server_names(
        instruction: str,
        context_text: str | None,
        configured_servers: list[McpServerMetadata],
    ) -> set[str]:
        by_lower = {str(meta.name).lower(): str(meta.name) for meta in configured_servers if meta.name}
        if not by_lower:
            return set()

        candidates: set[str] = set()
        combined = "\n".join(part for part in (instruction, context_text or "") if part)
        for match in re.findall(r"(?im)^\s*server\s*:\s*['\"]?([^'\"\r\n#]+)", combined):
            candidates.add(match.strip().strip(",] "))
        for match in re.findall(r"(?im)^\s*servers\s*:\s*\[([^\]\r\n]+)\]", combined):
            for item in match.split(","):
                candidates.add(item.strip().strip("'\" "))

        required: set[str] = set()
        for candidate in candidates:
            configured_name = by_lower.get(candidate.lower())
            if configured_name:
                required.add(configured_name)
        return required

    @staticmethod
    def _include_required_mcp_servers(
        selected_servers: list[McpServerMetadata] | None,
        configured_servers: list[McpServerMetadata],
        required_server_names: set[str],
    ) -> list[McpServerMetadata] | None:
        if selected_servers is None or not required_server_names:
            return selected_servers

        selected = list(selected_servers)
        selected_names = {str(meta.name).lower() for meta in selected if meta.name}
        configured_by_lower = {str(meta.name).lower(): meta for meta in configured_servers if meta.name}
        for server_name in sorted(required_server_names):
            key = server_name.lower()
            if key not in selected_names and key in configured_by_lower:
                selected.append(configured_by_lower[key])
                selected_names.add(key)
        return selected

    async def _collect_mcp_tool_contracts(
        self,
        ctx: StepExecutionContext,
        server_metadata: list[McpServerMetadata],
    ) -> list[McpToolOutputContract]:
        factory = ctx.engine.mcp_client_factory
        if factory is None or not server_metadata:
            return []

        contracts: list[McpToolOutputContract] = []
        for meta in server_metadata:
            name = getattr(meta, "name", None) or str(meta)
            try:
                _session, tools, _prompts, _resources = await self._discover_mcp_capabilities_with_retry(factory, str(name), ctx.engine.mcp_cache)
            except Exception:
                continue

            for tool in tools:
                tool_name = self._get_mcp_field(tool, "name")
                if not tool_name:
                    continue
                contracts.append(
                    McpToolOutputContract(
                        server_name=str(name),
                        tool_name=str(tool_name),
                        description=self._get_mcp_field(tool, "description"),
                        input_schema=copy.deepcopy(self._get_mcp_field(tool, "input_schema") or self._get_mcp_field(tool, "inputSchema")),
                        output_schema=copy.deepcopy(self._get_mcp_field(tool, "output_schema") or self._get_mcp_field(tool, "outputSchema")),
                        example_response=copy.deepcopy(self._get_mcp_field(tool, "example_response") or self._get_mcp_field(tool, "exampleResponse")),
                    )
                )
        return contracts

    @staticmethod
    async def _discover_mcp_capabilities_with_retry(factory: Any, server_name: str, cache: Any = None) -> tuple[Any, list[Any], list[Any], list[Any]]:
        cached_tools = get_cached_tools(cache, server_name)
        cached_prompts = get_cached_prompts(cache, server_name)
        cached_resources = get_cached_resources(cache, server_name)
        if cached_tools is not None and cached_prompts is not None and cached_resources is not None:
            return None, list(cached_tools), list(cached_prompts), list(cached_resources)

        last_error: Exception | None = None
        for attempt in range(1, _MCP_DISCOVERY_MAX_ATTEMPTS + 1):
            try:
                session = await factory.get_client_async(server_name)
                if cached_tools is None:
                    tools = list(await session.list_tools_async())
                    cache_tools(cache, server_name, tools)
                else:
                    tools = list(cached_tools)

                if cached_prompts is None:
                    prompts = list(await session.list_prompts_async())
                    cache_prompts(cache, server_name, prompts)
                else:
                    prompts = list(cached_prompts)

                if cached_resources is None:
                    try:
                        resources = list(await session.list_resources_async())
                    except Exception:
                        resources = []
                    cache_resources(cache, server_name, resources)
                else:
                    resources = list(cached_resources)
                return session, tools, prompts, resources
            except Exception as exc:
                last_error = exc
                if attempt >= _MCP_DISCOVERY_MAX_ATTEMPTS:
                    break
                await asyncio.sleep(_MCP_DISCOVERY_RETRY_BASE_DELAY_SECONDS * attempt)
        raise last_error or RuntimeError(f"MCP discovery failed for server '{server_name}'")

    async def _build_repair_context_with_mcp_docs(
        self,
        ctx: StepExecutionContext,
        policy: dict[str, Any],
        invalid_yaml: str | None,
        exc: Exception,
        forced_mcp_server_names: set[str],
        mcp_tool_contracts: list[McpToolOutputContract] | None = None,
    ) -> str:
        context = self._build_minimal_repair_context(ctx, policy, invalid_yaml, exc, mcp_tool_contracts)
        missing_server_name = self._try_extract_missing_mcp_server_name(str(exc))
        if not missing_server_name:
            return context

        forced_mcp_server_names.add(missing_server_name)
        configured_servers = self._get_configured_mcp_server_metadata(ctx)
        missing_server = self._find_mcp_server_metadata(configured_servers, missing_server_name)
        if missing_server is None:
            available = ", ".join(meta.name for meta in configured_servers if meta.name) or "(none)"
            return f"{context}\n\nMCP server '{missing_server_name}' is not configured. Available MCP servers: {available}".rstrip()

        mcp_doc = await self._build_mcp_documentation(ctx, [missing_server], [])
        return f"{context}\n\nMCP server context required by the failed workflow:\n{mcp_doc}".rstrip()

    @staticmethod
    def _find_mcp_server_metadata(
        configured_servers: list[McpServerMetadata],
        server_name: str,
    ) -> McpServerMetadata | None:
        for meta in configured_servers:
            if meta.name and meta.name.lower() == server_name.lower():
                return meta
        return None

    @staticmethod
    def _try_extract_missing_mcp_server_name(error_message: str) -> str | None:
        match = re.search(r"MCP server '([^']+)' not found", error_message, flags=re.IGNORECASE)
        return match.group(1) if match else None

    @staticmethod
    def _build_minimal_repair_context(
        ctx: StepExecutionContext,
        policy: dict[str, Any],
        invalid_yaml: str | None,
        exc: Exception,
        mcp_tool_contracts: list[McpToolOutputContract] | None = None,
    ) -> str:
        selected_types = WorkflowPlanExecutor._extract_repair_step_types(ctx, invalid_yaml, str(exc))
        if not selected_types:
            selected_types = WorkflowPlanExecutor._extract_known_step_types_from_yaml(ctx, invalid_yaml)

        allowed_raw = policy.get("allowed_step_types")
        allowed_types = {str(x) for x in allowed_raw} if isinstance(allowed_raw, list) else None
        if allowed_types is not None:
            selected_types &= allowed_types

        snippet_map = ctx.engine.registry.get_dsl_snippet_map(None)
        available_types = sorted(allowed_types or set(snippet_map.keys()))
        parts = ["", "Available step type names:", ", ".join(available_types)]

        snippets = ctx.engine.registry.get_dsl_snippets(selected_types) if selected_types else []
        if snippets:
            parts.extend(["", "DSL snippets for failed/referenced step types:", WorkflowPlanExecutor._remove_markdown_fence_lines("\n".join(snippets))])

        mcp_repair_context = WorkflowPlanExecutor._build_minimal_mcp_repair_context(
            ctx,
            invalid_yaml,
            selected_types,
            mcp_tool_contracts,
        )
        if mcp_repair_context:
            parts.extend(["", "MCP docs for failed/referenced calls:", mcp_repair_context])

        return "\n".join(parts).rstrip()

    @staticmethod
    def _build_minimal_mcp_repair_context(
        ctx: StepExecutionContext,
        invalid_yaml: str | None,
        selected_types: set[str],
        mcp_tool_contracts: list[McpToolOutputContract] | None,
    ) -> str | None:
        if "mcp.call" not in selected_types or not invalid_yaml or not invalid_yaml.strip():
            return None

        contracts = list(mcp_tool_contracts or [])
        if not contracts:
            return None

        try:
            doc = WorkflowParser.parse(invalid_yaml)
        except Exception:
            return None

        calls: list[tuple[str, str, list[str], Any]] = []
        for workflow in doc.workflows.values():
            for step in WorkflowPlanExecutor._enumerate_steps(workflow.steps):
                if step.type != "mcp.call" or not isinstance(step.input, dict):
                    continue
                server = step.input.get("server")
                if not isinstance(server, str) or not server.strip() or "${" in server:
                    continue
                methods: list[str] = []
                method = step.input.get("method")
                if isinstance(method, str) and method.strip() and "${" not in method:
                    methods.append(method.strip())
                raw_methods = step.input.get("methods")
                if isinstance(raw_methods, list):
                    for item in raw_methods:
                        if isinstance(item, str) and item.strip() and "${" not in item:
                            methods.append(item.strip())
                calls.append((step.id, server.strip(), methods, step.input.get("request")))

        if not calls:
            return None

        configured_metadata = {
            str(meta.name): meta
            for meta in WorkflowPlanExecutor._get_configured_mcp_server_metadata(ctx)
            if getattr(meta, "name", None)
        }
        contracts_by_server: dict[str, list[McpToolOutputContract]] = {}
        for contract in contracts:
            contracts_by_server.setdefault(contract.server_name, []).append(contract)

        available_servers = sorted(set(configured_metadata) | set(contracts_by_server))
        lines = [
            "Use exact MCP server and method names. Tool arguments must be nested under `input.request`.",
            "Correct direct tool call shape:",
            "  type: mcp.call",
            "  input: { server: <exact-server>, kind: tool, method: <exact-tool>, request: { ... } }",
            "For every listed input_schema_json, copy all required request properties into input.request with the exact schema name and scalar type.",
            "If a required numeric/integer/boolean MCP property is missing, add an explicit YAML scalar value; do not use an expression string, cast, empty value, fake value, or data.env fallback.",
            "When repairing one MCP call, re-check every MCP call in the YAML so earlier schema fixes are preserved.",
            f"Available MCP servers: {', '.join(available_servers) if available_servers else '(none)'}",
        ]

        seen: set[tuple[str, str, tuple[str, ...]]] = set()
        for step_id, server_name, methods, request in calls:
            key = (step_id, server_name, tuple(methods))
            if key in seen:
                continue
            seen.add(key)

            lines.append("")
            lines.append(f"- Step `{step_id}` references MCP server `{server_name}`.")
            server_description = getattr(configured_metadata.get(server_name), "description", None)
            if server_description:
                lines.append(f"  Server description: {server_description}")

            server_contracts = contracts_by_server.get(server_name, [])
            if not server_contracts:
                lines.append("  This server was not found in the discovered MCP tool catalog.")
                continue

            available_tools = [contract.tool_name for contract in server_contracts]
            lines.append(f"  Available tools on `{server_name}`: {', '.join(available_tools)}")

            unknown_methods = [method for method in methods if method not in available_tools]
            if unknown_methods:
                lines.append(f"  Unknown requested method(s): {', '.join(unknown_methods)}")
                selected_contracts = server_contracts
            elif methods:
                selected_contracts = [contract for contract in server_contracts if contract.tool_name in methods]
            else:
                lines.append("  No literal method was selected; use one exact tool name when the request schema is known.")
                selected_contracts = server_contracts

            if request is not None:
                lines.append("  invalid_request_yaml:")
                lines.extend("    " + line for line in WorkflowPlanExecutor._serialize_yaml_value(request).splitlines())

            for contract in selected_contracts[:12]:
                description = f": {contract.description}" if contract.description else ""
                lines.append(f"  - {contract.tool_name}{description}")
                if contract.input_schema is not None:
                    lines.append("    input_schema_json: " + WorkflowPlanExecutor._dump_json(contract.input_schema))
                if contract.output_schema is not None:
                    lines.append("    output_schema_json: " + WorkflowPlanExecutor._dump_json(contract.output_schema))
                if contract.example_response is not None:
                    lines.append("    example_response_json: " + WorkflowPlanExecutor._dump_json(contract.example_response))
            if len(selected_contracts) > 12:
                lines.append(f"  ... {len(selected_contracts) - 12} additional tool(s) omitted from repair context.")

        return "\n".join(lines).rstrip()

    @staticmethod
    def _extract_repair_step_types(ctx: StepExecutionContext, invalid_yaml: str | None, error_message: str) -> set[str]:
        lookup = WorkflowPlanExecutor._build_step_repair_lookup(invalid_yaml)
        selected: set[str] = set()

        for step_id in WorkflowPlanExecutor._extract_error_step_ids(error_message):
            info = lookup.get(step_id)
            if not info:
                continue
            step_type, ancestors = info
            if ctx.engine.registry.get(step_type) is not None:
                selected.add(step_type)
            for ancestor_type in ancestors:
                if ctx.engine.registry.get(ancestor_type) is not None:
                    selected.add(ancestor_type)

        for step_type in WorkflowPlanExecutor._extract_quoted_step_types(error_message):
            if ctx.engine.registry.get(step_type) is not None:
                selected.add(step_type)

        return selected

    @staticmethod
    def _extract_known_step_types_from_yaml(ctx: StepExecutionContext, invalid_yaml: str | None) -> set[str]:
        selected: set[str] = set()
        for step_type, _ancestors in WorkflowPlanExecutor._build_step_repair_lookup(invalid_yaml).values():
            if ctx.engine.registry.get(step_type) is not None:
                selected.add(step_type)
        return selected

    @staticmethod
    def _build_step_repair_lookup(invalid_yaml: str | None) -> dict[str, tuple[str, tuple[str, ...]]]:
        lookup: dict[str, tuple[str, tuple[str, ...]]] = {}
        if not invalid_yaml or not invalid_yaml.strip():
            return lookup
        try:
            doc = WorkflowParser.parse(invalid_yaml)
        except Exception:
            return lookup

        def visit(steps: list[StepDef] | None, ancestors: tuple[str, ...]) -> None:
            for step in steps or []:
                lookup[step.id] = (step.type, ancestors)
                child_ancestors = ancestors + (step.type,)
                visit(step.steps, child_ancestors)
                for branch in step.branches or []:
                    visit(branch.steps, child_ancestors)
                for case in step.cases or []:
                    visit(case.steps, child_ancestors)
                visit(step.default, child_ancestors)

        for workflow in doc.workflows.values():
            visit(workflow.steps, ())
        return lookup

    @staticmethod
    def _extract_error_step_ids(error_message: str) -> set[str]:
        ids: set[str] = set()
        ids.update(re.findall(r"step '([^']+)'", error_message, flags=re.IGNORECASE))
        ids.update(re.findall(r'"step":"([^"]+)"', error_message, flags=re.IGNORECASE))
        ids.update(re.findall(r"\bdata\.steps\.([A-Za-z_][A-Za-z0-9_-]*)", error_message, flags=re.IGNORECASE))
        return ids

    @staticmethod
    def _extract_quoted_step_types(error_message: str) -> set[str]:
        step_types: set[str] = set()
        step_types.update(re.findall(r"Step type '([^']+)'", error_message, flags=re.IGNORECASE))
        step_types.update(re.findall(r"type '([^']+)'", error_message, flags=re.IGNORECASE))
        return step_types

    @staticmethod
    def _build_structured_plan_error(exc: Exception) -> str:
        return build_structured_plan_error(exc)

    async def _build_planning_prompt(
        self,
        ctx: StepExecutionContext,
        instruction: str,
        context_text: str,
        policy: dict[str, Any],
        limits: dict[str, Any],
        generator: dict[str, Any],
        plan_reasoning: str | None = None,
        mcp_tool_contracts: list[McpToolOutputContract] | None = None,
        forced_mcp_server_names: set[str] | None = None,
    ) -> str:
        allowed_types = set(policy.get("allowed_step_types") or []) or None
        configured_mcp_servers = self._get_configured_mcp_server_metadata(ctx)
        candidate_mcp_servers = await self._maybe_prefilter_mcp_server_metadata(ctx, generator, instruction, context_text, plan_reasoning)
        required_mcp_server_names = set(forced_mcp_server_names or set())
        required_mcp_server_names.update(self._extract_required_mcp_server_names(instruction, context_text, configured_mcp_servers))
        candidate_mcp_servers = self._include_required_mcp_servers(candidate_mcp_servers, configured_mcp_servers, required_mcp_server_names)
        mcp_doc = await self._build_mcp_documentation(ctx, candidate_mcp_servers, mcp_tool_contracts)
        mcp_doc = await self._maybe_prefilter_mcp_documentation(ctx, generator, instruction, context_text, mcp_doc, plan_reasoning)
        steps_doc = self._remove_markdown_fence_lines("\n\n".join(ctx.engine.registry.get_dsl_snippets(allowed_types)))
        exc_doc = self._build_step_exceptions_doc(ctx.engine.registry, allowed_types)
        constraints_lines: list[str] = []
        if isinstance(policy.get("allowed_step_types"), list):
            constraints_lines.append(f"Allowed step types: {', '.join(str(x) for x in policy['allowed_step_types'])}")
        if isinstance(policy.get("denied_step_types"), list):
            constraints_lines.append(f"Denied step types: {', '.join(str(x) for x in policy['denied_step_types'])}")
        if not bool(policy.get("allow_remote_workflow_refs", False)):
            constraints_lines.append("Remote workflow references (kind: url) are NOT allowed.")
        if limits.get("max_steps_total") is not None:
            constraints_lines.append(f"Maximum total steps: {limits.get('max_steps_total')}")

        return (
            "Generate a valid GnOuGo.Flow YAML document (version: 1).\n"
            "You are a GnOuGo.Flow YAML workflow generator. Return ONLY valid YAML, no explanation or markdown fences.\n\n"
            "<dsl_reference>\n"
            "Use GnOuGo.Flow DSL v1. Root document must contain `version: 1`, `name`, `skill`, and `workflows` map.\n"
            "Step fields: id, type, if, input, output, retry, on_error, steps, branches, cases, expr, default, item_var, index_var.\n"
            "Retry fields: max, backoff_ms, backoff_mult, jitter_ms.\n"
            "on_error cases: if, action (continue|stop), set_output.\n"
            "</dsl_reference>\n\n"
            "<required_root_yaml_shape>\n"
            "The generated YAML MUST include all required root keys exactly once: version, name, skill, workflows.\n"
            "Root key requirements:\n"
            "- version: non-empty string or number 1\n"
            "- name: non-empty string\n"
            "- skill: required object with description, tags, inputs, and outputs for routing and argument extraction\n"
            "- workflows: non-empty object\n"
            "Each workflow entry under workflows MUST define a steps array.\n"
            "Minimal valid skeleton:\n"
            "version: 1\n"
            "name: generated-workflow\n"
            "skill:\n"
            "  description: Describe when this generated workflow should be used.\n"
            "  tags: [generated]\n"
            "  inputs: {}\n"
            "  outputs: {}\n"
            "workflows:\n"
            "  main:\n"
            "    steps: []\n"
            "</required_root_yaml_shape>\n\n"
            "<generation_validation_checklist>\n"
            "Before returning YAML, self-check these rules and fix the YAML silently:\n"
            "- Use only exact step types listed in <available_step_types>. Do not invent aliases or legacy names.\n"
            "- Every step has a unique non-empty `id` and a non-empty `type`.\n"
            "- Put common step fields (`id`, `type`, `if`, `input`, `output`, `retry`, `on_error`) at the step level, not inside `input`.\n"
            "- Put executor-specific arguments inside `input` only. For example `llm.call.input.prompt`, "
            "`mcp.call.input.server`, `template.render.input.template`.\n"
            "- Container mappings must use their documented shape: `sequence`/`loop.*` use step-level `steps`; "
            "`parallel` uses step-level `branches[].steps`; `switch` uses step-level `cases[].steps` "
            "and optional step-level `default`.\n"
            "- Do not reference future steps. Expressions may read `data.inputs.*` and outputs from earlier `data.steps.<id>.*` only.\n"
            "- Do not reference `data.steps.<id>.*` produced only inside `switch` cases, "
            "`if`-guarded steps, or loop bodies from later steps unless you first map every possible "
            "path to a guaranteed value.\n"
            "- Function arguments are evaluated before the function runs: "
            "`coalesce(data.steps.branch_a.value, data.steps.branch_b.value)` is invalid if either "
            "branch step may not exist.\n"
            "- Every generated custom `function name(...)` declaration in a `functions:` block MUST be immediately "
            "preceded by JSDoc (`/** ... */`).\n"
            "- Function JSDoc MUST include one typed `@param {type} name - meaning` tag for every function parameter.\n"
            "- Function JSDoc MUST include a typed `@returns {type} - meaning` tag for the function output, "
            "including `{void}` when it intentionally returns nothing.\n"
            "- After a `switch`, prefer writing a common result object via the same workflow-level "
            "output alias in every case/default branch, or add one guaranteed normalization step "
            "that receives the whole switch/branch context and emits a stable schema.\n"
            "- Use documented output shapes exactly: `template.render` text is `data.steps.<id>.text`; "
            "`llm.call` text is `data.steps.<id>.text` and structured JSON is `data.steps.<id>.json`; "
            "`mcp.call` single-tool response is `data.steps.<id>.response`.\n"
            "- Do not assume nested fields inside opaque MCP `response` unless the tool schema/description explicitly "
            "documents them; pass the whole response onward when uncertain.\n"
            "- NEVER invent properties under `data.steps.<id>.response`. Access `response.<field>` only when an "
            "`output_schema` or `example_response` explicitly documents that field.\n"
            "- If an MCP response is opaque, use `json(data.steps.<id>.response)` to pass the whole response to another step.\n"
            "- If precise fields are needed from an opaque response, add an `llm.call` normalization step with "
            "`structured_output`, then read fields from `data.steps.<normalizer>.json`.\n"
            "MCP input contract rules:\n"
            f"{self._format_mcp_input_contract_checklist()}\n"
            "- When a field expects a string containing JSON, use a YAML literal block (`|`) or single quotes; "
            "do not put unescaped JSON inside a double-quoted YAML string.\n"
            "- Workflow `outputs` should use either the short expression form or the long form with `expr` and `type`.\n"
            "</generation_validation_checklist>\n\n"
            f"{self._build_user_task_block(instruction, context_text)}\n\n"
            "<available_step_types>\n"
            f"{steps_doc}\n"
            "</available_step_types>\n\n"
            "<available_mcp_servers>\n"
            f"{mcp_doc}\n"
            "</available_mcp_servers>\n\n"
            "<mcp_output_access>\n"
            "Preferred MCP planning pattern: when tool names and input schemas are listed above, use `mcp.call` directly "
            "with explicit `server`, `kind`, `method`, and `request`.\n"
            "Required MCP planning pattern: discover candidate servers with `mcp.list`, then use mcp.call with prompt + model (+ optional temperature) "
            "when exact tool names or arguments are not known.\n"
            "For LLM-assisted MCP calls, put the natural-language instruction in input.prompt and pass discovered `tools`/`prompts`.\n"
            "When building `mcp.call.input.request`, preserve JSON schema scalar types exactly: "
            "numbers/integers/booleans must be unquoted YAML scalars, while strings may be quoted.\n"
            "If a string field must contain JSON text, prefer a YAML literal block (`|`) so nested quotes remain valid YAML.\n"
            'mcp.call single-tool output shape: `{ status: "ok"|"error", response: tool-specific JSON }`\n'
            "Access status via `data.steps.<id>.status` and full result via `data.steps.<id>.response`.\n"
            "Do not assume any field inside `response` unless MCP docs explicitly define it through `output_schema` or `example_response`.\n"
            "If the response is opaque, use `json(data.steps.<id>.response)` or add an `llm.call` normalization step with `structured_output`.\n"
            "When `response` is an array, access items directly (`response[0]...`) or through `response.content[0]...` compatibility alias.\n"
            "For batch output: `{ status, results: [{ method, status, response }] }`.\n"
            'For LLM-assisted output: `{ status, selection_mode: "llm", text, tool_calls, results, json? }`.\n'
            "</mcp_output_access>\n\n"
            "<llm_model_parameters>\n"
            "The runtime owns model metadata (token limits, pricing, and capabilities) "
            "and removes unsupported optional request parameters before provider calls.\n"
            "Prefer omitting provider/model when runtime defaults should apply. "
            "Do NOT add `temperature` or `reasoning` by habit; include them only for explicit overrides.\n"
            "If a generated workflow includes unsupported optional LLM parameters, the runtime may omit them automatically based on model capabilities.\n"
            "</llm_model_parameters>\n\n"
            "<error_handling_and_retries>\n"
            "Use retry only for transient errors explicitly marked retryable.\n"
            "Retries run before on_error. on_error runs after retries are exhausted (or immediately for non-retryable errors).\n"
            "Inside on_error.cases[].if, context exposes error.code, error.message, error.retryable, step.id, step.type.\n"
            "</error_handling_and_retries>\n\n"
            "<step_exceptions_by_type>\n"
            f"{self._remove_markdown_fence_lines(exc_doc)}\n"
            "</step_exceptions_by_type>\n\n"
            "<constraints>\n"
            f"{chr(10).join(constraints_lines) if constraints_lines else '(none)'}\n"
            "</constraints>\n"
        )

    @classmethod
    def _format_mcp_input_contract_checklist(cls) -> str:
        return "\n".join(cls._MCP_INPUT_CONTRACT_CHECKLIST)

    @staticmethod
    def _should_prefilter(generator: dict[str, Any]) -> bool:
        prefilter = generator.get("prefilter")
        return prefilter is None or isinstance(prefilter, dict) or bool(prefilter)

    @staticmethod
    def _prefilter_target(generator: dict[str, Any]) -> tuple[str | None, str | None, float | None]:
        prefilter = generator.get("prefilter")
        provider = None
        model = None
        temperature = None
        if isinstance(prefilter, dict):
            provider = prefilter.get("provider")
            model = prefilter.get("model")
            if prefilter.get("temperature") is not None:
                temperature = float(prefilter["temperature"])
        model = model or generator.get("model")
        provider = provider or generator.get("provider")
        return provider, model, temperature

    @staticmethod
    def _add_prefilter_usage_event(
        ctx: StepExecutionContext,
        usage: Any,
        model: str | None,
        provider: str | None,
        phase: str,
        event_name: str,
    ) -> None:
        if not isinstance(usage, dict):
            return

        attrs: list[tuple[str, Any]] = [
            ("gnougo-flow.plan.phase", phase),
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.system", provider or "unknown"),
        ]
        if model:
            attrs.append(("gen_ai.request.model", model))

        input_tokens = usage.get("prompt_tokens", usage.get("input_tokens"))
        output_tokens = usage.get("completion_tokens", usage.get("output_tokens"))
        total_tokens = usage.get("total_tokens")
        if input_tokens is not None:
            attrs.append(("gen_ai.usage.input_tokens", int(input_tokens)))
        if output_tokens is not None:
            attrs.append(("gen_ai.usage.output_tokens", int(output_tokens)))
        if total_tokens is not None:
            attrs.append(("gen_ai.usage.total_tokens", int(total_tokens)))

        ctx.add_telemetry_event(event_name, attrs)

    @staticmethod
    def _add_usage_attributes(span: Any, usage: Any) -> None:
        if not isinstance(usage, dict):
            return

        input_tokens = usage.get("prompt_tokens", usage.get("input_tokens"))
        output_tokens = usage.get("completion_tokens", usage.get("output_tokens"))
        total_tokens = usage.get("total_tokens")
        if input_tokens is not None:
            span.set_attribute("gen_ai.usage.input_tokens", int(input_tokens))
        if output_tokens is not None:
            span.set_attribute("gen_ai.usage.output_tokens", int(output_tokens))
        if total_tokens is not None:
            span.set_attribute("gen_ai.usage.total_tokens", int(total_tokens))

    async def _maybe_prefilter_mcp_server_metadata(
        self,
        ctx: StepExecutionContext,
        generator: dict[str, Any],
        instruction: str,
        context_text: str,
        plan_reasoning: str | None = None,
    ) -> list[McpServerMetadata] | None:
        if not self._should_prefilter(generator):
            return None

        factory = ctx.engine.mcp_client_factory
        llm_client = ctx.engine.llm_client
        if factory is None or llm_client is None:
            return None

        server_meta = list(getattr(factory, "server_metadata", []) or [])
        if not server_meta:
            return None

        provider, model, temperature = self._prefilter_target(generator)
        if not model:
            return None

        catalog = "\n".join(f"- {getattr(meta, 'name', str(meta))}: {getattr(meta, 'description', None) or '(no description)'}" for meta in server_meta)
        prompt = (
            "You are an MCP server-selection assistant. Given a task description and a catalog of MCP server "
            "descriptions, select only the servers likely to contain relevant capabilities.\n\n"
            "Return ONLY a JSON object matching this shape: "
            '{"servers":[{"name":"server-name","reason":"short relevance reason"}]}.\n\n'
            "Rules:\n"
            "- Use only exact server names from the catalog.\n"
            "- Base the decision only on server descriptions, not on imagined tools.\n"
            "- Include every plausibly relevant server; exclude clearly unrelated servers.\n"
            '- If no server is relevant, return {"servers": []}.\n\n'
            f"<server_catalog>\n{catalog}\n</server_catalog>\n\n"
            f"{self._build_user_task_block(instruction, context_text)}"
        )

        prefilter_span = ctx.begin_telemetry_span(
            "workflow.plan.mcp_server_prefilter",
            "mcp_server_prefilter",
            [
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.system", provider or "unknown"),
                ("gen_ai.request.model", model),
                ("mcp.servers_total", len(server_meta)),
            ],
        )
        try:
            prefilter_span.__enter__()
            ctx.add_telemetry_event(
                "gnougo-flow.step.thinking",
                [
                    (
                        "gnougo-flow.thinking.message",
                        f"Pre-filtering MCP server descriptions with {model} ({len(server_meta)} server(s))…",
                    ),
                    ("gnougo-flow.thinking.level", "thinking"),
                ],
            )
            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.servers.start",
                [
                    ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.system", provider or "unknown"),
                    ("gen_ai.request.model", model),
                    ("mcp.servers_total", len(server_meta)),
                ],
            )
            if ctx.limits.log_step_content:
                prefilter_span.add_event(
                    "gen_ai.content.prompt",
                    [
                        ("gen_ai.prompt", prompt),
                        ("prompt.role", "user"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ],
                )
                ctx.add_telemetry_event(
                    "gen_ai.content.prompt",
                    [
                        ("gen_ai.prompt", prompt),
                        ("prompt.role", "user"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ],
                )

            response = await ctx.engine.call_llm_async(
                LLMRequest(
                    provider=provider,
                    model=str(model),
                    prompt=prompt,
                    temperature=temperature,
                    structured_output_schema={
                        "type": "object",
                        "properties": {
                            "servers": {
                                "type": "array",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "name": {"type": "string"},
                                        "reason": {"type": "string"},
                                    },
                                    "required": ["name", "reason"],
                                    "additionalProperties": False,
                                },
                            }
                        },
                        "required": ["servers"],
                        "additionalProperties": False,
                    },
                    structured_output_strict=True,
                    reasoning=plan_reasoning,
                )
            )

            if ctx.limits.log_step_content and response.text:
                prefilter_span.add_event(
                    "gen_ai.content.completion",
                    [
                        ("gen_ai.completion", response.text),
                        ("completion.role", "assistant"),
                        ("completion.finish_reason", "stop"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ],
                )
                ctx.add_telemetry_event(
                    "gen_ai.content.completion",
                    [
                        ("gen_ai.completion", response.text),
                        ("completion.role", "assistant"),
                        ("completion.finish_reason", "stop"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ],
                )
            self._add_usage_attributes(prefilter_span, response.usage)
            self._add_prefilter_usage_event(ctx, response.usage, str(model), provider, "mcp_server_prefilter", "gnougo-flow.plan.prefilter.servers.usage")

            payload = response.json_payload
            if not isinstance(payload, dict) and response.text:
                payload = json.loads(self._strip_markdown_code_fence(response.text))
            if not isinstance(payload, dict) or not isinstance(payload.get("servers"), list):
                raise ValueError("MCP server prefilter response did not contain a servers array")

            by_name = {str(getattr(meta, "name", "")).lower(): meta for meta in server_meta}
            selected: list[McpServerMetadata] = []
            seen: set[str] = set()
            for item in payload["servers"]:
                name = item.get("name") if isinstance(item, dict) else item
                if not isinstance(name, str):
                    continue
                key = name.lower()
                if key in by_name and key not in seen:
                    selected.append(by_name[key])
                    seen.add(key)

            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.servers.result",
                [
                    ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.request.model", model),
                    ("mcp.servers_total", len(server_meta)),
                    ("mcp.servers_selected", len(selected)),
                    ("mcp.server.names", ",".join(str(meta.name) for meta in selected)),
                ],
            )
            prefilter_span.set_attribute("mcp.servers_selected", len(selected))
            prefilter_span.set_attribute("mcp.server.names", ",".join(str(meta.name) for meta in selected))
            ctx.add_telemetry_event(
                "gnougo-flow.step.thinking",
                [
                    (
                        "gnougo-flow.thinking.message",
                        f"MCP server pre-filter: {len(selected)}/{len(server_meta)} server(s) selected before discovery.",
                    ),
                    ("gnougo-flow.thinking.level", "info"),
                ],
            )
            prefilter_span.end()
            return selected
        except Exception as exc:
            prefilter_span.fail(exc)
            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.servers.fallback",
                [
                    ("gnougo-flow.plan.phase", "mcp_server_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.request.model", model),
                    ("error.type", type(exc).__name__),
                    ("mcp.servers_total", len(server_meta)),
                ],
            )
            ctx.add_telemetry_event(
                "gnougo-flow.step.thinking",
                [
                    (
                        "gnougo-flow.thinking.message",
                        f"MCP server pre-filter failed ({exc}), discovering all {len(server_meta)} server(s).",
                    ),
                    ("gnougo-flow.thinking.level", "info"),
                ],
            )
            prefilter_span.end()
            return server_meta

    @staticmethod
    def _strip_markdown_code_fence(text: str) -> str:
        candidate = text.strip()
        if not candidate.startswith("```"):
            return candidate

        lines = candidate.splitlines()
        if not lines:
            return candidate

        # Keep only fenced content and ignore optional language marker on first line.
        if lines[0].startswith("```"):
            lines = lines[1:]
        if lines and lines[-1].strip().startswith("```"):
            lines = lines[:-1]
        return "\n".join(lines).strip()

    @staticmethod
    def _looks_like_workflow_body(node: Any) -> bool:
        return isinstance(node, dict) and (
            isinstance(node.get("steps"), list)
            or isinstance(node.get("inputs"), dict)
            or isinstance(node.get("outputs"), dict)
            or isinstance(node.get("functions"), str)
        )

    def _normalize_planned_yaml(self, yaml_text: str) -> str:
        try:
            parsed = yaml.safe_load(yaml_text)
        except Exception:
            return yaml_text
        if not isinstance(parsed, dict):
            return yaml_text

        if isinstance(parsed.get("workflows"), dict):
            return yaml_text

        normalized: dict[str, Any] | None = None
        if isinstance(parsed.get("steps"), list):
            normalized = {
                "version": int(parsed.get("version", 1) or 1),
                "name": parsed.get("name"),
                "meta": parsed.get("meta"),
                "skill": parsed.get("skill"),
                "entrypoint": "generated",
                "workflows": {
                    "generated": {
                        "inputs": parsed.get("inputs"),
                        "functions": parsed.get("functions"),
                        "steps": parsed.get("steps"),
                        "outputs": parsed.get("outputs"),
                    }
                },
            }
        elif self._looks_like_workflow_body(parsed):
            normalized = {
                "version": int(parsed.get("version", 1) or 1),
                "name": parsed.get("name"),
                "meta": parsed.get("meta"),
                "skill": parsed.get("skill"),
                "entrypoint": "generated",
                "workflows": {
                    "generated": {
                        "inputs": parsed.get("inputs"),
                        "functions": parsed.get("functions"),
                        "steps": parsed.get("steps") or [],
                        "outputs": parsed.get("outputs"),
                    }
                },
            }

        if normalized is None:
            for key, value in parsed.items():
                if self._looks_like_workflow_body(value):
                    normalized = {
                        "version": int(parsed.get("version", 1) or 1),
                        "name": parsed.get("name"),
                        "meta": parsed.get("meta"),
                        "skill": parsed.get("skill"),
                        "entrypoint": str(key),
                        "workflows": {str(key): value},
                    }
                    break

        if normalized is None:
            return yaml_text

        compact = {k: v for k, v in normalized.items() if v is not None}
        return yaml.safe_dump(compact, sort_keys=False, allow_unicode=False).strip()

    @staticmethod
    def _parse_and_validate_generated_workflow(yaml_text: str) -> WorkflowDocument:
        try:
            doc = WorkflowParser.parse(yaml_text)
        except Exception as exc:
            details = build_exception_details(
                infer_plan_error_code(str(exc)),
                "parse",
                str(exc),
                exc,
            )
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                f"Generated workflow parse failed: {exc} | repair diagnostics: {to_prompt_json(details)}",
                details=details,
            ) from exc

        if not doc.workflows:
            details = build_exception_details(
                "MISSING_ROOT_KEY_WORKFLOWS",
                "validation",
                "required root key 'workflows' must be a non-empty object.",
            )
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                "Validation failed: required root key 'workflows' must be a non-empty object. | repair diagnostics: "
                + to_prompt_json(details),
                details=details,
            )

        return doc

    @staticmethod
    def _validate_generated_workflow(doc: WorkflowDocument) -> None:
        errors = WorkflowValidator().validate(doc)
        if not errors:
            return
        details = build_validation_failure_details(errors, None, None)
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            "Generated workflow validation failed: "
            + WorkflowPlanExecutor._format_validation_errors(errors)
            + " | repair diagnostics: "
            + to_prompt_json(details),
            details=details,
        )

    @staticmethod
    def _validate_generated_workflow_for_plan(
        doc: WorkflowDocument,
        mcp_tool_contracts: list[McpToolOutputContract],
        mcp_server_metadata: list[McpServerMetadata] | None = None,
    ) -> int:
        errors = WorkflowValidator().validate(doc)
        semantic_exception: WorkflowSemanticValidationException | None = None
        compilation_exception: Exception | None = None
        normalization_count = 0
        mcp_coverage_exception: Exception | None = None

        try:
            normalization_count = normalize_mcp_call_input_requests(doc, mcp_tool_contracts)
            validate_workflow_semantics(doc, mcp_tool_contracts)
        except WorkflowSemanticValidationException as exc:
            semantic_exception = exc

        try:
            WorkflowPlanExecutor._validate_mcp_discovery_coverage(doc, mcp_tool_contracts, mcp_server_metadata or [])
        except Exception as exc:
            mcp_coverage_exception = exc

        if not any(WorkflowPlanExecutor._is_fatal_compiler_validation_error(error) for error in errors):
            try:
                WorkflowCompiler().compile(doc)
            except Exception as exc:
                compilation_exception = exc

        if not errors and semantic_exception is None and compilation_exception is None and mcp_coverage_exception is None:
            return normalization_count

        if not errors and semantic_exception is None and compilation_exception is None and isinstance(mcp_coverage_exception, WorkflowRuntimeException):
            raise mcp_coverage_exception

        diagnostics: list[str] = []
        if errors:
            diagnostics.append("workflow validation: " + WorkflowPlanExecutor._format_validation_errors(errors))
        if semantic_exception is not None:
            diagnostics.append("semantic validation: " + str(semantic_exception))
        if mcp_coverage_exception is not None:
            diagnostics.append("mcp discovery: " + str(mcp_coverage_exception))
        if compilation_exception is not None:
            diagnostics.append("compilation: " + str(compilation_exception))

        details = build_validation_failure_details(errors, semantic_exception, compilation_exception)
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            "Generated workflow validation failed: "
            + " | ".join(diagnostics)
            + " | repair diagnostics: "
            + to_prompt_json(details),
            details=details,
        )

    @staticmethod
    def _dump_workflow_yaml(doc: WorkflowDocument) -> str:
        data = doc.model_dump(by_alias=True, exclude_none=True, exclude={"raw_yaml"})
        return yaml.dump(data, Dumper=_NoAliasDumper, sort_keys=False, allow_unicode=False).strip()

    @staticmethod
    def _validate_mcp_discovery_coverage(
        doc: WorkflowDocument,
        mcp_tool_contracts: list[McpToolOutputContract],
        mcp_server_metadata: list[McpServerMetadata],
    ) -> None:
        tool_calls = [
            step
            for workflow in doc.workflows.values()
            for step in WorkflowPlanExecutor._enumerate_steps(workflow.steps)
            if step.type == "mcp.call"
            and isinstance(step.input, dict)
            and str(step.input.get("kind", "tool")).lower() != "prompt"
        ]
        if not tool_calls:
            return

        if not mcp_tool_contracts:
            WorkflowPlanExecutor._raise_mcp_discovery_coverage_error(
                "MCP_DISCOVERY_REQUIRED",
                "generated workflow contains mcp.call tool steps, but no MCP tool catalog was discovered. Validation is fail-closed.",
                "Run MCP discovery for this plan, remove mcp.call steps, or add an mcp.list discovery step before tool execution.",
            )

        known_servers = {contract.server_name for contract in mcp_tool_contracts}
        configured_servers = {meta.name for meta in mcp_server_metadata if getattr(meta, "name", None)}
        for step in tool_calls:
            server_name = step.input.get("server")
            if not isinstance(server_name, str) or not server_name.strip() or "${" in server_name:
                WorkflowPlanExecutor._raise_mcp_discovery_coverage_error(
                    "MCP_SERVER_DYNAMIC_UNVERIFIABLE",
                    f"mcp.call step '{step.id}' must use a literal discovered server name during workflow.plan validation.",
                    "Use an exact server name from the discovered MCP catalog in input.server.",
                )
            if server_name not in known_servers:
                if configured_servers and server_name in configured_servers:
                    WorkflowPlanExecutor._raise_mcp_discovery_coverage_error(
                        "MCP_TOOL_CATALOG_EMPTY",
                        f"MCP server '{server_name}' is referenced by step '{step.id}', but its discovered tool catalog is empty.",
                        "Remove the mcp.call or select a discovered server with tools.",
                    )
                WorkflowPlanExecutor._raise_mcp_discovery_coverage_error(
                    "MCP_SERVER_UNKNOWN",
                    f"mcp.call step '{step.id}' references server '{server_name}', which is absent from the discovered MCP catalog.",
                    "Change input.server to one of the discovered server names, or do not generate this mcp.call.",
                )

    @staticmethod
    def _raise_mcp_discovery_coverage_error(code: str, message: str, hint: str) -> None:
        details = build_mcp_discovery_coverage_details(code, message, hint)
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"{code}: {message} | repair diagnostics: {to_prompt_json(details)}",
            details=details,
        )

    @staticmethod
    def _enumerate_steps(steps: list[StepDef] | None) -> list[StepDef]:
        found: list[StepDef] = []
        for step in steps or []:
            found.append(step)
            found.extend(WorkflowPlanExecutor._enumerate_steps(step.steps))
            if step.branches:
                for branch in step.branches:
                    found.extend(WorkflowPlanExecutor._enumerate_steps(branch.steps))
            if step.cases:
                for case in step.cases:
                    found.extend(WorkflowPlanExecutor._enumerate_steps(case.steps))
            found.extend(WorkflowPlanExecutor._enumerate_steps(step.default))
        return found

    @staticmethod
    def _is_fatal_compiler_validation_error(error: ValidationError) -> bool:
        return error.code in {
            ErrorCodes.EXPR_PARSE,
            "DSL_VERSION",
            "NO_WORKFLOWS",
            ErrorCodes.WORKFLOW_CYCLE_DETECTED,
            "INVALID_ENTRYPOINT",
        }

    @staticmethod
    def _format_validation_errors(errors: list[ValidationError]) -> str:
        return format_validation_errors(errors)

    @staticmethod
    def _validate_generated_workflow_semantics(
        doc: WorkflowDocument,
        mcp_tool_contracts: list[McpToolOutputContract],
    ) -> None:
        try:
            validate_workflow_semantics(doc, mcp_tool_contracts)
        except WorkflowSemanticValidationException as exc:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, str(exc)) from exc

    async def _maybe_prefilter_mcp_documentation(
        self,
        ctx: StepExecutionContext,
        generator: dict[str, Any],
        instruction: str,
        context_text: str,
        mcp_doc: str,
        plan_reasoning: str | None = None,
    ) -> str:
        if not self._should_prefilter(generator):
            return mcp_doc

        llm_client = ctx.engine.llm_client
        if llm_client is None:
            return mcp_doc

        provider, model, temperature = self._prefilter_target(generator)
        if not model:
            return mcp_doc

        prompt = (
            "Select only MCP servers/capabilities relevant to the task.\n"
            'Return JSON object {"filtered":"..."} where filtered is a plain-text subset.\n\n'
            "When keeping a capability with input_schema, output_schema, or example_response, copy the complete "
            "`*_json` line and any continuation lines verbatim. Do not summarize, rewrite, or truncate schema blocks/descriptions.\n"
            "If preserving the selected schema lines exactly is uncertain, return the full relevant server section.\n\n"
            f"{self._build_user_task_block(instruction, context_text)}\n\n"
            f"<available_mcp>\n{mcp_doc}\n</available_mcp>\n"
        )

        prefilter_span = ctx.begin_telemetry_span(
            "workflow.plan.mcp_capability_prefilter",
            "mcp_capability_prefilter",
            [
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.system", provider or "unknown"),
                ("gen_ai.request.model", model),
                ("mcp.documentation.input_length", len(mcp_doc)),
            ],
        )
        try:
            prefilter_span.__enter__()
            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.capabilities.start",
                [
                    ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.system", provider or "unknown"),
                    ("gen_ai.request.model", model),
                ],
            )
            if ctx.limits.log_step_content:
                prefilter_span.add_event(
                    "gen_ai.content.prompt",
                    [
                        ("gen_ai.prompt", prompt),
                        ("prompt.role", "user"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ],
                )
                ctx.add_telemetry_event(
                    "gen_ai.content.prompt",
                    [
                        ("gen_ai.prompt", prompt),
                        ("prompt.role", "user"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ],
                )
            response = await ctx.engine.call_llm_async(
                LLMRequest(
                    provider=provider,
                    model=model,
                    prompt=prompt,
                    temperature=temperature,
                    structured_output_schema={
                        "type": "object",
                        "properties": {"filtered": {"type": "string"}},
                        "required": ["filtered"],
                        "additionalProperties": False,
                    },
                    structured_output_strict=True,
                    reasoning=plan_reasoning,
                )
            )
            if ctx.limits.log_step_content and response.text:
                prefilter_span.add_event(
                    "gen_ai.content.completion",
                    [
                        ("gen_ai.completion", response.text),
                        ("completion.role", "assistant"),
                        ("completion.finish_reason", "stop"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ],
                )
                ctx.add_telemetry_event(
                    "gen_ai.content.completion",
                    [
                        ("gen_ai.completion", response.text),
                        ("completion.role", "assistant"),
                        ("completion.finish_reason", "stop"),
                        ("gen_ai.operation.name", "chat"),
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ],
                )
            self._add_usage_attributes(prefilter_span, response.usage)
            self._add_prefilter_usage_event(
                ctx, response.usage, str(model), provider, "mcp_capability_prefilter", "gnougo-flow.plan.prefilter.capabilities.usage"
            )
            if isinstance(response.json_payload, dict) and isinstance(response.json_payload.get("filtered"), str):
                filtered = response.json_payload["filtered"]
                if not self._filtered_mcp_doc_preserves_schema_blocks(mcp_doc, filtered):
                    filtered = mcp_doc
                prefilter_span.set_attribute("mcp.documentation.filtered_length", len(filtered))
                ctx.add_telemetry_event(
                    "gnougo-flow.plan.prefilter.capabilities.result",
                    [
                        ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                        ("gen_ai.operation.name", "chat"),
                        ("gen_ai.request.model", model),
                        ("mcp.documentation.filtered_length", len(filtered)),
                    ],
                )
                prefilter_span.end()
                return filtered
            if response.text:
                parsed = json.loads(response.text)
                if isinstance(parsed, dict) and isinstance(parsed.get("filtered"), str):
                    filtered = parsed["filtered"]
                    if not self._filtered_mcp_doc_preserves_schema_blocks(mcp_doc, filtered):
                        filtered = mcp_doc
                    prefilter_span.set_attribute("mcp.documentation.filtered_length", len(filtered))
                    ctx.add_telemetry_event(
                        "gnougo-flow.plan.prefilter.capabilities.result",
                        [
                            ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                            ("gen_ai.operation.name", "chat"),
                            ("gen_ai.request.model", model),
                            ("mcp.documentation.filtered_length", len(filtered)),
                        ],
                    )
                    prefilter_span.end()
                    return filtered
        except Exception as exc:
            prefilter_span.fail(exc)
            ctx.add_telemetry_event(
                "gnougo-flow.plan.prefilter.capabilities.fallback",
                [
                    ("gnougo-flow.plan.phase", "mcp_capability_prefilter"),
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.request.model", model),
                    ("error.type", type(exc).__name__),
                ],
            )
            prefilter_span.end()
            return mcp_doc

        prefilter_span.end()
        return mcp_doc

    @staticmethod
    def _filtered_mcp_doc_preserves_schema_blocks(original: str, filtered: str) -> bool:
        if "_json:" not in original:
            return True
        if "_json:" not in filtered:
            return False
        return "…" not in filtered and "... schema truncated" not in filtered.lower()

    async def _build_mcp_documentation(
        self,
        ctx: StepExecutionContext,
        server_meta_override: list[McpServerMetadata] | None = None,
        mcp_tool_contracts: list[McpToolOutputContract] | None = None,
    ) -> str:
        factory = ctx.engine.mcp_client_factory
        if factory is None:
            return "No MCP client factory configured."

        server_meta = server_meta_override if server_meta_override is not None else (getattr(factory, "server_metadata", []) or [])
        if not server_meta:
            return "No MCP servers configured."

        with ctx.begin_telemetry_span("workflow.plan.mcp_discovery", "mcp_discovery", [("mcp.servers_total", len(server_meta))]) as discovery_span:
            sections: list[str] = []
            discovered_count = 0
            tools_total = 0
            prompts_total = 0
            resources_total = 0
            for meta in server_meta:
                name = getattr(meta, "name", None) or str(meta)
                desc = getattr(meta, "description", None) or ""
                section = [f"## Server: {name}", f"Description: {desc or '(none)'}"]
                try:
                    _session, tools, prompts, resources = await self._discover_mcp_capabilities_with_retry(factory, str(name), ctx.engine.mcp_cache)

                    discovered_count += 1
                    tools_total += len(tools)
                    prompts_total += len(prompts)
                    resources_total += len(resources)
                    section.append(f"Tools ({len(tools)}):")
                    for t in tools:
                        tool_name = self._get_mcp_field(t, "name")
                        tool_description = self._get_mcp_field(t, "description")
                        input_schema = self._get_mcp_field(t, "input_schema") or self._get_mcp_field(t, "inputSchema")
                        output_schema = self._get_mcp_field(t, "output_schema") or self._get_mcp_field(t, "outputSchema")
                        example_response = self._get_mcp_field(t, "example_response") or self._get_mcp_field(t, "exampleResponse")

                        section.append(f"- {tool_name}: {tool_description or '(no description)'}")
                        if input_schema is not None:
                            self._append_json_block(section, "  ", "input_schema", input_schema)
                        if output_schema is not None:
                            self._append_json_block(section, "  ", "output_schema", output_schema)
                        if example_response is not None:
                            self._append_json_block(section, "  ", "example_response", example_response)
                        if mcp_tool_contracts is not None and tool_name:
                            mcp_tool_contracts.append(
                                McpToolOutputContract(
                                    server_name=str(name),
                                    tool_name=str(tool_name),
                                    description=tool_description,
                                    input_schema=copy.deepcopy(input_schema),
                                    output_schema=copy.deepcopy(output_schema),
                                    example_response=copy.deepcopy(example_response),
                                )
                            )
                    section.append(f"Prompts ({len(prompts)}):")
                    for p in prompts:
                        section.append(f"- {p.name}: {p.description or '(no description)'}")
                    section.append(f"Resources ({len(resources)}):")
                    for r in resources:
                        section.append(f"- {r.name} ({r.uri}): {r.description or '(no description)'}")
                except Exception as exc:
                    section.append(f"- {name}: {desc or '(none)'}")
                    section.append("(tool discovery unavailable)")
                    section.append(f"Error while reading capabilities: {exc}")
                sections.append("\n".join(section))
            discovery_span.set_attribute("mcp.servers_discovered", discovered_count)
            discovery_span.set_attribute("mcp.tools_total", tools_total)
            discovery_span.set_attribute("mcp.prompts_total", prompts_total)
            discovery_span.set_attribute("mcp.resources_total", resources_total)
            return "\n\n".join(sections)

    @staticmethod
    def _get_mcp_field(value: Any, name: str) -> Any:
        if isinstance(value, dict):
            return value.get(name)
        return getattr(value, name, None)

    @staticmethod
    def _dump_json(value: Any) -> str:
        if hasattr(value, "model_dump"):
            value = value.model_dump(by_alias=False)
        return json.dumps(value, ensure_ascii=False, default=str, indent=2)

    @staticmethod
    def _append_json_block(lines: list[str], indent: str, label: str, value: Any) -> None:
        lines.append(f"{indent}{label}_json: {WorkflowPlanExecutor._dump_json(value)}")

    def _enforce_plan_policy(self, doc: WorkflowDocument, policy: dict[str, Any], limits: dict[str, Any]) -> None:
        allowed = set(policy.get("allowed_step_types") or [])
        denied = set(policy.get("denied_step_types") or [])
        max_steps_total = int(limits.get("max_steps_total", 0) or 0)
        allow_remote_refs = bool(policy.get("allow_remote_workflow_refs", False))

        def walk(steps: list[StepDef]) -> list[StepDef]:
            found: list[StepDef] = []
            for s in steps:
                found.append(s)
                if s.steps:
                    found.extend(walk(s.steps))
                if s.branches:
                    for b in s.branches:
                        found.extend(walk(b.steps))
                if s.cases:
                    for c in s.cases:
                        found.extend(walk(c.steps))
                if s.default:
                    found.extend(walk(s.default))
            return found

        all_steps: list[StepDef] = []
        for wf in doc.workflows.values():
            all_steps.extend(walk(wf.steps))

        all_step_types = [s.type for s in all_steps]

        if max_steps_total > 0 and len(all_step_types) > max_steps_total:
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_POLICY,
                f"Generated workflow exceeds max_steps_total ({len(all_step_types)} > {max_steps_total})",
            )

        for st in all_step_types:
            if denied and st in denied:
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, f"Step type '{st}' is denied by policy")
            if allowed and st not in allowed:
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, f"Step type '{st}' is not allowed by policy")

        if not allow_remote_refs:
            for step in all_steps:
                if step.type != "workflow.call" or not isinstance(step.input, dict):
                    continue
                ref = step.input.get("ref")
                if isinstance(ref, dict) and str(ref.get("kind", "local")) == "url":
                    raise WorkflowRuntimeException(
                        ErrorCodes.WORKFLOW_FETCH_POLICY,
                        "Remote workflow references are forbidden by policy (allow_remote_workflow_refs=false)",
                    )

    @staticmethod
    def _build_step_exceptions_doc(registry: StepExecutorRegistry, allowed_types: set[str] | None) -> str:
        catalogs = registry.get_step_exception_catalogs(allowed_types)
        if not catalogs:
            return "No task-specific exception catalog is available."

        lines = [
            "Common notes:",
            "- INPUT_VALIDATION usually means required fields are missing or malformed.",
            "- Only retryable error codes should normally use retry.",
        ]
        container_types = {
            "sequence": "runs child steps sequentially, so unhandled child failures can stop the container.",
            "parallel": "can fail from one failing branch, plus its own parallel-limit checks.",
            "loop.sequential": "can fail from one failing iteration, plus loop-limit checks.",
            "loop.parallel": "can fail from one failing parallel iteration, plus loop-limit checks.",
            "switch": "can fail from selected case/default child failures.",
            "workflow.call": "can fail from called workflow failures and call/fetch/policy errors.",
            "workflow.execute": "can fail from generated workflow failures and plan resolution errors.",
        }
        visible_containers = [c for c in container_types if allowed_types is None or c in allowed_types]
        if visible_containers:
            lines.extend(
                [
                    "",
                    "Container child-error propagation:",
                    "- These container steps can raise both their own errors and nested child-step errors.",
                ]
            )
            for container in visible_containers:
                lines.append(f"- {container}: {container_types[container]}")

        lines.extend(["", "Step-specific exceptions:"])
        for catalog in catalogs:
            lines.append(f"- {catalog.step_type}")
            for exc in sorted(catalog.exceptions, key=lambda e: (e.code, e.retryable)):
                lines.append(f"  - {exc.code} ({'retryable' if exc.retryable else 'non-retryable'}): {exc.description}")
        return "\n".join(lines).strip()
