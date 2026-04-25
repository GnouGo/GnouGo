from __future__ import annotations

from gnougo_flow_core.models import StepDef, WorkflowDocument
from gnougo_flow_core.runtime import *  # noqa: F401,F403


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

        # Reasoning effort: workflow planning is heavy reasoning, default to "high" (max).
        # Authors can override via `generator.reasoning: auto|minimal|low|medium|high|max`.
        plan_reasoning_raw = generator.get("reasoning")
        plan_reasoning = (
            plan_reasoning_raw.strip()
            if isinstance(plan_reasoning_raw, str) and plan_reasoning_raw.strip()
            else "high"
        )

        policy = input_obj.get("policy") if isinstance(input_obj.get("policy"), dict) else {}
        limits = input_obj.get("limits") if isinstance(input_obj.get("limits"), dict) else {}
        validate = input_obj.get("validate") if isinstance(input_obj.get("validate"), dict) else {}
        on_invalid = input_obj.get("on_invalid") if isinstance(input_obj.get("on_invalid"), dict) else {}
        max_attempts = max(1, int(on_invalid.get("max_attempts", 3)))
        on_invalid_action = str(on_invalid.get("action", "fail"))

        base_prompt = await self._build_planning_prompt(
            ctx,
            instruction,
            str(generator.get("context", "")),
            policy,
            limits,
            generator,
            plan_reasoning,
        )
        prompt = base_prompt
        last_error: Exception | None = None
        last_yaml = ""

        for attempt in range(1, max_attempts + 1):
            response = await client.call_async(
                LLMRequest(provider=provider, model=model, prompt=prompt, reasoning=plan_reasoning)
            )
            yaml_text = self._strip_markdown_code_fence(textwrap.dedent(response.text).strip())
            yaml_text = self._normalize_planned_yaml(yaml_text)
            last_yaml = yaml_text

            try:
                doc = WorkflowParser.parse(yaml_text)
                self._enforce_plan_policy(doc, policy, limits)
                if bool(validate.get("compile", True)):
                    WorkflowCompiler().compile(doc)
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
            except Exception as exc:
                last_error = exc
                if on_invalid_action != "reprompt" or attempt >= max_attempts:
                    break
                prompt = (
                    f"{base_prompt}\n\n"
                    "The previous output was invalid. Fix it and return only valid YAML.\n"
                    f"Validation error: {exc}\n"
                    "[PREVIOUS ERROR]\n"
                    f"{exc}\n"
                    "Fix the issues above and generate a corrected YAML."
                )

        raise WorkflowRuntimeException(
            ErrorCodes.TEMPLATE_PLAN,
            f"Failed to generate valid workflow after {max_attempts} attempts: {last_error or 'unknown error'}",
        )

    async def _build_planning_prompt(
        self,
        ctx: StepExecutionContext,
        instruction: str,
        context_text: str,
        policy: dict[str, Any],
        limits: dict[str, Any],
        generator: dict[str, Any],
        plan_reasoning: str | None = None,
    ) -> str:
        allowed_types = set(policy.get("allowed_step_types") or []) or None
        mcp_doc = await self._build_mcp_documentation(ctx)
        mcp_doc = await self._maybe_prefilter_mcp_documentation(
            ctx, generator, instruction, mcp_doc, plan_reasoning
        )
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
            "Use GnOuGo.Flow DSL v1. Root document must contain `version: 1` and `workflows` map.\n"
            "Step fields: id, type, if, input, output, retry, on_error, steps, branches, cases, expr, default, item_var, index_var.\n"
            "Retry fields: max, backoff_ms, backoff_mult, jitter_ms.\n"
            "on_error cases: if, action (continue|stop), set_output.\n\n"
            "[TASK]\n"
            f"Instruction: {instruction}\n"
            f"Context: {context_text or '(none)'}\n\n"
            "[AVAILABLE STEP TYPES]\n"
            f"{steps_doc}\n\n"
            "[AVAILABLE MCP SERVERS]\n"
            f"{mcp_doc}\n\n"
            "[MCP OUTPUT ACCESS]\n"
            "mcp.call single-tool output shape: `{ status: \"ok\"|\"error\", response: <tool-specific JSON> }`\n"
            "Access status via `data.steps.<id>.status` and full result via `data.steps.<id>.response`.\n"
            "Do not assume any field inside `response` unless MCP docs explicitly define it.\n"
            "When `response` is an array, access items directly (`response[0]...`) or through `response.content[0]...` compatibility alias.\n"
            "For batch output: `{ status, results: [{ method, status, response }] }`.\n"
            "For LLM-assisted output: `{ status, selection_mode: \"llm\", text, tool_calls, results, json? }`.\n\n"
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
                        "entrypoint": str(key),
                        "workflows": {str(key): value},
                    }
                    break

        if normalized is None:
            return yaml_text

        compact = {k: v for k, v in normalized.items() if v is not None}
        return yaml.safe_dump(compact, sort_keys=False, allow_unicode=False).strip()

    async def _maybe_prefilter_mcp_documentation(
        self,
        ctx: StepExecutionContext,
        generator: dict[str, Any],
        instruction: str,
        mcp_doc: str,
        plan_reasoning: str | None = None,
    ) -> str:
        prefilter = generator.get("prefilter")
        should_prefilter = prefilter is None or isinstance(prefilter, dict) or bool(prefilter)
        if not should_prefilter:
            return mcp_doc

        llm_client = ctx.engine.llm_client
        if llm_client is None:
            return mcp_doc

        provider = None
        model = None
        if isinstance(prefilter, dict):
            provider = prefilter.get("provider")
            model = prefilter.get("model")
        model = model or generator.get("model")
        if not model:
            return mcp_doc

        prompt = (
            "Select only MCP servers/capabilities relevant to the task.\n"
            "Return JSON object {\"filtered\":\"...\"} where filtered is a markdown subset.\n\n"
            f"Task:\n{instruction}\n\n"
            f"Available MCP:\n{mcp_doc}\n"
        )

        try:
            response = await llm_client.call_async(
                LLMRequest(
                    provider=provider,
                    model=model,
                    prompt=prompt,
                    structured_output_schema={
                        "type": "object",
                        "properties": {"filtered": {"type": "string"}},
                        "required": ["filtered"],
                    },
                    structured_output_strict=True,
                    reasoning=plan_reasoning,
                )
            )
            if isinstance(response.json_payload, dict) and isinstance(response.json_payload.get("filtered"), str):
                return response.json_payload["filtered"]
            if response.text:
                parsed = json.loads(response.text)
                if isinstance(parsed, dict) and isinstance(parsed.get("filtered"), str):
                    return parsed["filtered"]
        except Exception:
            return mcp_doc

        return mcp_doc

    async def _build_mcp_documentation(self, ctx: StepExecutionContext) -> str:
        factory = ctx.engine.mcp_client_factory
        if factory is None:
            return "No MCP client factory configured."

        server_meta = getattr(factory, "server_metadata", []) or []
        if not server_meta:
            return "No MCP servers configured."

        sections: list[str] = []
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

                section.append(f"Tools ({len(tools)}):")
                for t in tools:
                    section.append(f"- {t.name}: {t.description or '(no description)'}")
                section.append(f"Prompts ({len(prompts)}):")
                for p in prompts:
                    section.append(f"- {p.name}: {p.description or '(no description)'}")
                section.append(f"Resources ({len(resources)}):")
                for r in resources:
                    section.append(f"- {r.name} ({r.uri}): {r.description or '(no description)'}")
            except Exception as exc:
                section.append(f"Error while reading capabilities: {exc}")
            sections.append("\n".join(section))
        return "\n\n".join(sections)

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
                        ErrorCodes.TEMPLATE_POLICY,
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
            lines.extend([
                "",
                "Container child-error propagation:",
                "- These container steps can raise both their own errors and nested child-step errors.",
            ])
            for container in visible_containers:
                lines.append(f"- {container}: {container_types[container]}")

        lines.extend(["", "Step-specific exceptions:"])
        for catalog in catalogs:
            lines.append(f"- {catalog.step_type}")
            for exc in sorted(catalog.exceptions, key=lambda e: (e.code, e.retryable)):
                lines.append(
                    f"  - {exc.code} ({'retryable' if exc.retryable else 'non-retryable'}): {exc.description}"
                )
        return "\n".join(lines).strip()

