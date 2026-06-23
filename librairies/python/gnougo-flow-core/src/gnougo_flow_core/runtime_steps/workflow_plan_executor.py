from __future__ import annotations

import asyncio
import copy
import json
import re
import textwrap
from dataclasses import dataclass

import yaml

from gnougo_flow_core.compilation import ValidationError, WorkflowValidator
from gnougo_flow_core.models import StepDef, WorkflowDocument
from gnougo_flow_core.runtime import *  # noqa: F401,F403
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
from gnougo_flow_core.workflow_plan_semantic_validator import (
    McpToolOutputContract,
    WorkflowSemanticValidationException,
    normalize_mcp_call_input_requests,
    validate_workflow_semantics,
)

_MCP_DISCOVERY_MAX_ATTEMPTS = 3
_MCP_DISCOVERY_RETRY_BASE_DELAY_SECONDS = 0.5


@dataclass(slots=True)
class _WorkflowPipelineSubworkflowSpec:
    name: str
    goal: str
    inputs: dict[str, str]
    outputs: dict[str, str]
    extract_reason: str
    content: str
    generation_prompt: str


@dataclass(slots=True)
class _WorkflowPipelineExtraction:
    subworkflows: list[_WorkflowPipelineSubworkflowSpec]
    main_workflow_prompt: str
    validation_errors: list[str]


@dataclass(slots=True)
class _GeneratedLeafWorkflow:
    name: str
    workflow_name: str
    document: WorkflowDocument
    yaml_text: str
    workflow_node: dict[str, Any]


@dataclass(slots=True)
class _GeneratedMainAssembly:
    main_workflow_node: dict[str, Any]
    document_name: str | None = None
    skill_node: dict[str, Any] | None = None


class _NoAliasDumper(yaml.SafeDumper):
    def ignore_aliases(self, data):
        return True


class WorkflowPlanExecutor:
    step_type = "workflow.plan"
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
        if isinstance(mode, str) and mode.strip().lower() == "pipeline":
            return await self._execute_pipeline_async(ctx, copy.deepcopy(input_obj))

        return await self._execute_single_plan_async(ctx, input_obj)

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
        annotated_markdown = await self._mark_extractable_blocks(ctx, normalized_markdown, provider, model, reasoning)
        extraction = self._extract_subworkflow_specs(annotated_markdown)
        if extraction.validation_errors:
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                "workflow.plan pipeline extraction failed: " + "; ".join(extraction.validation_errors),
            )

        generated_leaves = [
            await self._generate_leaf_workflow_async(ctx, input_obj, generator, spec)
            for spec in extraction.subworkflows
        ]

        validate = input_obj.get("validate") if isinstance(input_obj.get("validate"), dict) else {}
        validation_mcp_server_metadata = self._get_configured_mcp_server_metadata(ctx)
        validation_mcp_tool_contracts: list[McpToolOutputContract] = []
        if (bool(validate.get("compile", True)) or bool(validate.get("dry_run", False))) and validation_mcp_server_metadata:
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
                previous_error = self._build_structured_plan_error(exc)

        if final_yaml is None or final_doc is None:
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                f"Pipeline main workflow assembly failed after {max_attempts} attempt(s): {last_error or 'unknown error'}",
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
        prompt = (
            "You annotate normalized automation Markdown for GnOuGo workflow generation.\n"
            "Return ONLY annotated Markdown. Do not wrap the result in code fences.\n\n"
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
            "Rules for subworkflow blocks:\n"
            "- The name must use snake_case.\n"
            "- Each block must describe exactly one responsibility.\n"
            "- Each block must be self-contained and detailed enough to generate a workflow later.\n"
            "- Each block must be a leaf workflow.\n"
            "- The block content must not mention calling another subworkflow.\n"
            "- Inputs and outputs must be explicit and typed.\n\n"
            "At the end of the Markdown, add:\n\n"
            "## Main workflow orchestration\n\n"
            "In that section, explain how the main workflow calls the leaf subworkflows in order.\n"
            "The architecture must have only one hierarchy level: only the main workflow can call leaf workflows.\n\n"
            f"<normalized_markdown>\n{normalized_markdown}\n</normalized_markdown>"
        )
        return await self._execute_pipeline_llm_text_phase(ctx, "mark_extractable_blocks", prompt, provider, model, reasoning)

    async def _execute_pipeline_llm_text_phase(
        self,
        ctx: StepExecutionContext,
        phase: str,
        prompt: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
    ) -> str:
        with ctx.begin_telemetry_span(
            f"workflow.plan.pipeline.{phase}",
            phase,
            [
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.system", provider or "unknown"),
                ("gen_ai.request.model", model),
                ("gen_ai.request.background", True),
            ],
        ) as span:
            response = await ctx.engine.call_llm_async(
                LLMRequest(provider=provider, model=model, prompt=prompt, reasoning=reasoning, use_background_mode=True)
            )
            self._add_usage_attributes(span, response.usage)
        text = self._strip_markdown_code_fence(textwrap.dedent(response.text or "")).strip()
        if not text:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"workflow.plan pipeline phase '{phase}' returned empty text.")
        return text

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
    ) -> str:
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
            "- For MCP schemas, required numeric/integer/boolean request fields must be literal YAML scalars when the "
            "schema or validator requires explicit values; do not use expressions, casts, empty strings, or `data.env.*` fallbacks.",
            "- Any schema with `type: object` MUST be strongly typed with a non-empty `properties` mapping. "
            "Never generate a bare `type: object` input, output, item, or nested property.",
            "- Use `required_properties: [field_name]` for required object property names; do not duplicate YAML keys.",
            "",
            "Inputs:",
        ]
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
        previous_errors: list[str] = []
        last_error: Exception | None = None
        for attempt in range(1, max_attempts + 1):
            leaf_input = self._build_leaf_plan_input(pipeline_input, generator, spec, previous_error, previous_yaml, previous_prompt)
            previous_prompt = leaf_input.get("generator", {}).get("instruction") if isinstance(leaf_input.get("generator"), dict) else spec.generation_prompt
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
                previous_yaml = self._try_extract_generated_yaml_from_exception(exc)
                previous_error = self._format_leaf_generation_error(spec.name, attempt, exc)
                previous_errors.append(previous_error)
                previous_errors = previous_errors[-8:]
                previous_error = self._merge_leaf_cumulative_repair_context(previous_errors)

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
    ) -> dict[str, Any]:
        leaf_generator = copy.deepcopy(generator)
        leaf_generator.pop("mode", None)
        leaf_generator.pop("raw_prompt", None)
        leaf_generator["instruction"] = (
            spec.generation_prompt
            if not previous_error
            else self._build_leaf_repair_prompt(spec.generation_prompt, previous_prompt, previous_yaml, previous_error)
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

    def _build_leaf_repair_prompt(self, generation_prompt: str, previous_prompt: str | None, previous_yaml: str | None, previous_error: str) -> str:
        repair_context = "Previous generated YAML for this leaf workflow failed validation.\nRegenerate only this leaf workflow and fix the YAML below."
        if previous_prompt:
            repair_context += f"\n\n<previous_prompt>\n{previous_prompt.strip()}\n</previous_prompt>"
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
    def _merge_leaf_cumulative_repair_context(previous_errors: list[str]) -> str:
        lines = [
            "Cumulative leaf retry requirements:",
            "- Preserve all fixes made for earlier validation failures; do not regress one MCP request or output while fixing another.",
            "- Re-check every mcp.call in the leaf against its discovered input_schema, not only the step named in the latest error.",
            "- If a required MCP request field is numeric/integer/boolean, emit an explicit YAML scalar of that type when the validator requires it.",
            "- Never satisfy missing MCP arguments with `data.env.*`, empty strings, fake values, casts, or string-to-number conversions.",
            "- Do not reference an `if`-guarded step from an unconditional later step unless a guaranteed value has first been produced on every path.",
            "- Workflow outputs must resolve to their declared type on every path.",
        ]
        if previous_errors:
            lines.extend(["", "All previous failed attempts for this leaf:"])
            for index, error in enumerate(previous_errors, start=1):
                lines.extend([f"<leaf_failure_{index}>", error, f"</leaf_failure_{index}>"])
        return "\n".join(lines).rstrip()

    @staticmethod
    def _try_extract_generated_yaml_from_exception(exc: Exception) -> str | None:
        message = str(exc)
        marker = "generated_yaml"
        if marker not in message:
            return None
        return message

    def _prepare_generated_leaf(self, spec: _WorkflowPipelineSubworkflowSpec, yaml_text: str) -> _GeneratedLeafWorkflow:
        doc = WorkflowParser.parse(yaml_text)
        if len(doc.workflows) != 1:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Leaf workflow '{spec.name}' must generate exactly one workflow.")
        workflow_name = next(iter(doc.workflows))
        self._enforce_strong_object_schemas(spec.name, doc)
        for step in self._enumerate_steps(doc.workflows[workflow_name].steps):
            if step.type in {"workflow.call", "workflow.plan"}:
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_POLICY, f"Leaf workflow '{spec.name}' must not contain step type '{step.type}'.")
        parsed = yaml.safe_load(yaml_text)
        workflow_node = copy.deepcopy(parsed.get("workflows", {}).get(workflow_name)) if isinstance(parsed, dict) else None
        if not isinstance(workflow_node, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Leaf workflow '{spec.name}' did not contain a valid workflow mapping.")
        return _GeneratedLeafWorkflow(spec.name, workflow_name, doc, yaml_text, workflow_node)

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
        policy = input_obj.get("policy")
        if not isinstance(policy, dict):
            policy = {}
            input_obj["policy"] = policy
        denied = policy.get("denied_step_types")
        if isinstance(denied, list):
            policy["denied_step_types"] = [item for item in denied if str(item) != "workflow.call"]
        allowed = policy.get("allowed_step_types")
        if isinstance(allowed, list) and "workflow.call" not in allowed:
            allowed.append("workflow.call")

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
            "You are assembling the parent `main` workflow for a GnOuGo.Flow pipeline.",
            "Return ONLY one YAML mapping with `document` and `main` keys. Do not return version, entrypoint, workflows, or leaf workflow definitions.",
            "",
            "Hard rules:",
            "- The main workflow may call leaf workflows using local `workflow.call` only.",
            "- The main workflow must never use `workflow.plan`.",
            "- Leaf workflows must never call other workflows.",
            "- Preserve the orchestration algorithm from the normalized prompt and the Main workflow orchestration section.",
            "- Do not inline leaf logic. Call the leaf workflow that owns each responsibility.",
            "- Every `data.inputs.<name>` reference MUST have an identically named declaration in `main.inputs`.",
            "- Leaf input names are call arguments, not automatically public main inputs.",
            "- `generated_leaf_contracts_yaml` is authoritative for leaf workflow names, call arguments, and available outputs.",
            "- If `leaf_input_candidates_yaml` or `leaf_subworkflow_specs_json` disagree with `generated_leaf_contracts_yaml`, follow `generated_leaf_contracts_yaml`.",
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
                "main:",
                "  inputs:",
                "    user_query: string",
                "  steps:",
                "    - id: call_example_leaf",
                "      type: workflow.call",
                "      input:",
                "        ref:",
                "          kind: local",
                "          name: example_leaf",
                "        args:",
                "          query: ${data.inputs.user_query}",
                "  outputs:",
                "    result: ${data.steps.call_example_leaf.outputs.result}",
            ]
        )
        return "\n".join(parts)

    @staticmethod
    def _build_main_assembly_repair_prompt(base_prompt: str, previous_response: str | None, structured_error: str) -> str:
        parts = [
            base_prompt.rstrip(),
            "",
            "The previous main workflow assembly failed final validation.",
            "Return a complete corrected `document` and `main` YAML mapping that still follows every rule above.",
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
        if isinstance(parsed.get("main"), dict):
            skill = copy.deepcopy(document.get("skill")) if isinstance(document.get("skill"), dict) else None
            return _GeneratedMainAssembly(copy.deepcopy(parsed["main"]), document.get("name"), skill)
        workflows = parsed.get("workflows")
        if isinstance(workflows, dict) and isinstance(workflows.get("main"), dict):
            skill = copy.deepcopy(parsed.get("skill")) if isinstance(parsed.get("skill"), dict) else None
            return _GeneratedMainAssembly(copy.deepcopy(workflows["main"]), parsed.get("name"), skill)
        skill = copy.deepcopy(parsed.get("skill")) if isinstance(parsed.get("skill"), dict) else None
        return _GeneratedMainAssembly(copy.deepcopy(parsed), parsed.get("name"), skill)

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
        main_node["outputs"] = {f"{spec.name}_outputs": f"${{data.steps.call_{spec.name}.outputs}}" for spec in specs}

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

    @staticmethod
    def _build_extraction_json(extraction: _WorkflowPipelineExtraction) -> dict[str, Any]:
        return {
            "subworkflows": [
                {
                    "name": spec.name,
                    "goal": spec.goal,
                    "inputs": spec.inputs,
                    "outputs": spec.outputs,
                    "extract_reason": spec.extract_reason,
                    "content": spec.content,
                }
                for spec in extraction.subworkflows
            ],
            "main_workflow_prompt": extraction.main_workflow_prompt,
            "validation_errors": extraction.validation_errors,
        }

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
                session, tools, _prompts, _resources = await self._discover_mcp_capabilities_with_retry(factory, str(name))
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
    async def _discover_mcp_capabilities_with_retry(factory: Any, server_name: str) -> tuple[Any, list[Any], list[Any], list[Any]]:
        last_error: Exception | None = None
        for attempt in range(1, _MCP_DISCOVERY_MAX_ATTEMPTS + 1):
            try:
                session = await factory.get_client_async(server_name)
                tools = list(await session.list_tools_async())
                prompts = list(await session.list_prompts_async())
                try:
                    resources = list(await session.list_resources_async())
                except Exception:
                    resources = []
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
                    _session, tools, prompts, resources = await self._discover_mcp_capabilities_with_retry(factory, str(name))

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
