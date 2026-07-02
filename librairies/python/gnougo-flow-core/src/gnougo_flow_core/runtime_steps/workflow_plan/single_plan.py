from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanSinglePlanMixin:
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


    @classmethod
    def _build_reprompt(
        cls,
        instruction: str,
        context_text: str,
        policy: dict[str, Any],
        invalid_yaml: str | None,
        exc: Exception,
        repair_context: str | None,
    ) -> str:
        structured_error = cls._build_structured_plan_error(exc)
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
            cls._build_user_task_block(instruction, context_text),
        ]
        if constraints:
            parts.extend(["", cls._prompt_section("constraints", "\n".join(constraints))])
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
            parts.extend(["", cls._prompt_section("relevant_repair_context", repair_context)])
        parts.extend(["", "Fix the issues above and generate a corrected YAML."])
        return "\n".join(parts)


    @staticmethod
    def _build_user_task_block(instruction: str, context_text: str | None) -> str:
        parts = ["<task>", "<user_prompt>", instruction, "</user_prompt>"]
        if context_text and context_text.strip():
            parts.extend(["<user_context>", context_text, "</user_context>"])
        parts.append("</task>")
        return "\n".join(parts)


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
