from __future__ import annotations

import copy
import json
import re

from gnougo_flow_core.compilation import ValidationError, WorkflowValidator
from gnougo_flow_core.models import StepDef, WorkflowDocument
from gnougo_flow_core.runtime import *  # noqa: F401,F403
from gnougo_flow_core.workflow_plan_dry_run_validator import validate_workflow_plan_dry_run
from gnougo_flow_core.workflow_plan_semantic_validator import (
    McpToolOutputContract,
    WorkflowSemanticValidationException,
    normalize_mcp_call_input_requests,
    validate_workflow_semantics,
)


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

    async def execute_async(self, ctx: StepExecutionContext) -> Any:
        client = ctx.engine.llm_client
        if client is None:
            raise WorkflowRuntimeException(ErrorCodes.LLM_NETWORK, "No LLM client configured")

        input_obj = ctx.engine.get_resolved_input(ctx)
        if not isinstance(input_obj, dict):
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.plan input must be object")

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

        mcp_tool_contracts: list[McpToolOutputContract] = []
        context_text = str(generator.get("context", ""))
        base_prompt = await self._build_planning_prompt(
            ctx,
            instruction,
            context_text,
            policy,
            limits,
            generator,
            plan_reasoning,
            mcp_tool_contracts,
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
                    doc = WorkflowParser.parse(yaml_text)
                    validation_span.set_attribute("gnougo-flow.plan.workflow_count", len(doc.workflows))
                    self._enforce_plan_policy(doc, policy, limits)
                    if bool(validate.get("compile", True)):
                        normalization_count = self._validate_generated_workflow_for_plan(doc, mcp_tool_contracts)
                        if normalization_count > 0:
                            yaml_text = self._dump_workflow_yaml(doc)
                    if bool(validate.get("dry_run", False)):
                        validation_span.set_attribute("gnougo-flow.plan.dry_run", True)
                        await validate_workflow_plan_dry_run(doc, mcp_tool_contracts)
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
                last_repair_context = self._build_minimal_repair_context(ctx, policy, last_invalid_yaml, exc)
            except Exception as exc:
                last_error = exc
                if on_invalid_action != "reprompt" or attempt >= max_attempts:
                    break
                last_invalid_yaml = self._strip_markdown_code_fence(textwrap.dedent(response.text).strip())
                last_repair_context = self._build_minimal_repair_context(ctx, policy, last_invalid_yaml, exc)

        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"Failed to generate valid workflow after {max_attempts} attempts: {last_error or 'unknown error'}",
        )

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
            "",
            "[TASK]",
            f"Instruction: {instruction}",
        ]
        if context_text.strip():
            parts.append(f"Context: {context_text}")
        if constraints:
            parts.extend(["", "[CONSTRAINTS]", "\n".join(constraints)])
        parts.extend(
            [
                "",
                "[INVALID YAML]",
                invalid_yaml if invalid_yaml and invalid_yaml.strip() else "(previous output was empty)",
                "",
                "[PREVIOUS ERROR]",
                structured_error,
                "",
                "[MINIMUM DSL CONTEXT]",
                "Required root: version, name, skill, workflows. `skill` is a top-level object with description, tags, inputs, and outputs. Each workflow has steps: [] and optional outputs.",
                "Each step requires step-level id and type. Common fields stay at step level: if, input, output, retry, on_error, steps, branches, cases, default.",
                "Executor-specific arguments go inside input only.",
                "Containers: sequence/loop.* use steps; parallel uses branches[].steps; switch uses cases[].steps and optional default.",
                "Expressions may read data.inputs.* and earlier data.steps.<id>.* only.",
            ]
        )
        if repair_context and repair_context.strip():
            parts.append(repair_context)
        parts.extend(["", "Fix the issues above and generate a corrected YAML."])
        return "\n".join(parts)

    @staticmethod
    def _build_minimal_repair_context(
        ctx: StepExecutionContext,
        policy: dict[str, Any],
        invalid_yaml: str | None,
        exc: Exception,
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
            parts.extend(["", "DSL snippets for failed/referenced step types:", "\n".join(snippets)])

        return "\n".join(parts).rstrip()

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
        message = str(exc).strip()
        lower = message.lower()

        error_code = "VALIDATION_ERROR"
        if "missing required field 'workflows'" in lower:
            error_code = "MISSING_ROOT_KEY_WORKFLOWS"
        elif "missing required field 'version'" in lower:
            error_code = "MISSING_ROOT_KEY_VERSION"
        elif "missing required field 'name'" in lower:
            error_code = "MISSING_ROOT_KEY_NAME"
        elif "skill_required" in lower or "top-level 'skill'" in lower:
            error_code = "MISSING_ROOT_KEY_SKILL"
        elif "step_type_unknown" in lower:
            error_code = "UNKNOWN_STEP_TYPE"
        elif "missing_steps" in lower or "missing_branches" in lower or "missing_cases" in lower:
            error_code = "INVALID_CONTAINER_SHAPE"
        elif "step_reference_not_available" in lower or "step_reference_unknown" in lower or "semantic_mapping_error" in lower:
            error_code = "SEMANTIC_MAPPING_ERROR"
        elif "opaque_response_deep_access" in lower:
            error_code = "OPAQUE_RESPONSE_DEEP_ACCESS"
        elif "step_output_property_unknown" in lower:
            error_code = "STEP_OUTPUT_PROPERTY_UNKNOWN"
        elif "yaml" in lower:
            error_code = "YAML_PARSE_ERROR"
        elif "not allowed by policy" in lower or "denied by policy" in lower:
            error_code = "POLICY_ERROR"
        elif "exceeds limit" in lower:
            error_code = "LIMIT_ERROR"

        return f"code={error_code}; message={message}"

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
    ) -> str:
        allowed_types = set(policy.get("allowed_step_types") or []) or None
        candidate_mcp_servers = await self._maybe_prefilter_mcp_server_metadata(ctx, generator, instruction, context_text, plan_reasoning)
        mcp_doc = await self._build_mcp_documentation(ctx, candidate_mcp_servers, mcp_tool_contracts)
        mcp_doc = await self._maybe_prefilter_mcp_documentation(ctx, generator, instruction, mcp_doc, plan_reasoning)
        steps_doc = "\n\n".join(ctx.engine.registry.get_dsl_snippets(allowed_types))
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
            "[DSL REFERENCE]\n"
            "Use GnOuGo.Flow DSL v1. Root document must contain `version: 1`, `name`, `skill`, and `workflows` map.\n"
            "Step fields: id, type, if, input, output, retry, on_error, steps, branches, cases, expr, default, item_var, index_var.\n"
            "Retry fields: max, backoff_ms, backoff_mult, jitter_ms.\n"
            "on_error cases: if, action (continue|stop), set_output.\n\n"
            "[REQUIRED ROOT YAML SHAPE]\n"
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
            "    steps: []\n\n"
            "[GENERATION VALIDATION CHECKLIST]\n"
            "Before returning YAML, self-check these rules and fix the YAML silently:\n"
            "- Use only exact step types listed in [AVAILABLE STEP TYPES]. Do not invent aliases or legacy names.\n"
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
            "- When a field expects a string containing JSON, use a YAML literal block (`|`) or single quotes; "
            "do not put unescaped JSON inside a double-quoted YAML string.\n"
            "- Workflow `outputs` should use either the short expression form or the long form with `expr` and `type`.\n\n"
            "[TASK]\n"
            f"Instruction: {instruction}\n"
            f"Context: {context_text or '(none)'}\n\n"
            "[AVAILABLE STEP TYPES]\n"
            f"{steps_doc}\n\n"
            "[AVAILABLE MCP SERVERS]\n"
            f"{mcp_doc}\n\n"
            "[MCP OUTPUT ACCESS]\n"
            "Preferred MCP planning pattern: when tool names and input schemas are listed above, use `mcp.call` directly "
            "with explicit `server`, `kind`, `method`, and `request`.\n"
            "Required MCP planning pattern: discover candidate servers with `mcp.list`, then use mcp.call with prompt + model (+ optional temperature) "
            "when exact tool names or arguments are not known.\n"
            "For LLM-assisted MCP calls, put the natural-language instruction in input.prompt and pass discovered `tools`/`prompts`.\n"
            "When building `mcp.call.input.request`, preserve JSON schema scalar types exactly: "
            "numbers/integers/booleans must be unquoted YAML scalars, while strings may be quoted.\n"
            "If a string field must contain JSON text, prefer a YAML literal block (`|`) so nested quotes remain valid YAML.\n"
            'mcp.call single-tool output shape: `{ status: "ok"|"error", response: <tool-specific JSON> }`\n'
            "Access status via `data.steps.<id>.status` and full result via `data.steps.<id>.response`.\n"
            "Do not assume any field inside `response` unless MCP docs explicitly define it through `output_schema` or `example_response`.\n"
            "If the response is opaque, use `json(data.steps.<id>.response)` or add an `llm.call` normalization step with `structured_output`.\n"
            "When `response` is an array, access items directly (`response[0]...`) or through `response.content[0]...` compatibility alias.\n"
            "For batch output: `{ status, results: [{ method, status, response }] }`.\n"
            'For LLM-assisted output: `{ status, selection_mode: "llm", text, tool_calls, results, json? }`.\n\n'
            "[LLM MODEL PARAMETERS]\n"
            "The runtime owns model metadata (token limits, pricing, and capabilities) "
            "and removes unsupported optional request parameters before provider calls.\n"
            "Prefer omitting provider/model when runtime defaults should apply. "
            "Do NOT add `temperature` or `reasoning` by habit; include them only for explicit overrides.\n"
            "If a generated workflow includes unsupported optional LLM parameters, the runtime may omit them automatically based on model capabilities.\n\n"
            "[ERROR HANDLING AND RETRIES]\n"
            "Use retry only for transient errors explicitly marked retryable.\n"
            "Retries run before on_error. on_error runs after retries are exhausted (or immediately for non-retryable errors).\n"
            "Inside on_error.cases[].if, context exposes error.code, error.message, error.retryable, step.id, step.type.\n\n"
            "[STEP EXCEPTIONS BY TYPE]\n"
            f"{exc_doc}\n\n"
            "[CONSTRAINTS]\n"
            f"{chr(10).join(constraints_lines) if constraints_lines else '(none)'}\n"
        )

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
            f"[SERVER CATALOG]\n{catalog}\n\n"
            f"[TASK]\nInstruction: {instruction}\n"
            f"{'Context: ' + context_text if context_text else ''}"
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
    def _validate_generated_workflow(doc: WorkflowDocument) -> None:
        errors = WorkflowValidator().validate(doc)
        if not errors:
            return
        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            "Generated workflow validation failed: " + WorkflowPlanExecutor._format_validation_errors(errors),
        )

    @staticmethod
    def _validate_generated_workflow_for_plan(
        doc: WorkflowDocument,
        mcp_tool_contracts: list[McpToolOutputContract],
    ) -> int:
        errors = WorkflowValidator().validate(doc)
        semantic_exception: WorkflowSemanticValidationException | None = None
        compilation_exception: Exception | None = None
        normalization_count = 0

        try:
            normalization_count = normalize_mcp_call_input_requests(doc, mcp_tool_contracts)
            validate_workflow_semantics(doc, mcp_tool_contracts)
        except WorkflowSemanticValidationException as exc:
            semantic_exception = exc

        if not any(WorkflowPlanExecutor._is_fatal_compiler_validation_error(error) for error in errors):
            try:
                WorkflowCompiler().compile(doc)
            except Exception as exc:
                compilation_exception = exc

        if not errors and semantic_exception is None and compilation_exception is None:
            return normalization_count

        diagnostics: list[str] = []
        if errors:
            diagnostics.append("workflow validation: " + WorkflowPlanExecutor._format_validation_errors(errors))
        if semantic_exception is not None:
            diagnostics.append("semantic validation: " + str(semantic_exception))
        if compilation_exception is not None:
            diagnostics.append("compilation: " + str(compilation_exception))

        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            "Generated workflow validation failed: " + " | ".join(diagnostics),
        )

    @staticmethod
    def _dump_workflow_yaml(doc: WorkflowDocument) -> str:
        data = doc.model_dump(by_alias=True, exclude_none=True, exclude={"raw_yaml"})
        return yaml.safe_dump(data, sort_keys=False, allow_unicode=False).strip()

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
        formatted: list[str] = []
        for error in errors:
            location_parts: list[str] = []
            if error.workflow_name:
                location_parts.append(f"workflow '{error.workflow_name}'")
            if error.step_id:
                location_parts.append(f"step '{error.step_id}'")
            if error.field:
                location_parts.append(f"field '{error.field}'")
            prefix = f"[{', '.join(location_parts)}] " if location_parts else ""
            formatted.append(f"{prefix}{error.code}: {error.message}")
        return "; ".join(formatted)

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
            'Return JSON object {"filtered":"..."} where filtered is a markdown subset.\n\n'
            f"Task:\n{instruction}\n\n"
            f"Available MCP:\n{mcp_doc}\n"
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
                    session = await factory.get_client_async(name)
                    tools = await session.list_tools_async()
                    prompts = await session.list_prompts_async()
                    resources: list[Any] = []
                    try:
                        resources = await session.list_resources_async()
                    except Exception:
                        resources = []

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
                            section.append(f"  input_schema: {self._dump_json(input_schema)}")
                        if output_schema is not None:
                            section.append(f"  output_schema: {self._dump_json(output_schema)}")
                        if example_response is not None:
                            section.append(f"  example_response: {self._dump_json(example_response)}")
                        if mcp_tool_contracts is not None and tool_name:
                            mcp_tool_contracts.append(
                                McpToolOutputContract(
                                    server_name=str(name),
                                    tool_name=str(tool_name),
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
        return json.dumps(value, ensure_ascii=False, default=str, separators=(",", ":"))

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
