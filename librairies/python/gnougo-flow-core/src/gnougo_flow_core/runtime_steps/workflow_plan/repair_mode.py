from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanRepairModeMixin:
    async def _execute_repair_plan_async(self, ctx: StepExecutionContext, input_obj: dict[str, Any]) -> Any:
        repair = input_obj.get("repair") if isinstance(input_obj.get("repair"), dict) else None
        if repair is None:
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.plan repair mode requires 'repair'")

        existing_yaml = self._try_get_string(repair.get("existing_yaml"))
        if existing_yaml is None or not existing_yaml.strip():
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.plan repair mode requires 'repair.existing_yaml'")

        prompt = self._try_get_string(repair.get("prompt")) or ""
        failed_input = self._try_get_string(repair.get("failed_input")) or ""
        error = repair.get("error") if isinstance(repair.get("error"), dict) else None
        error_message = self._try_get_string(error.get("message")) if error is not None else ""
        error_message = error_message or ""

        if error is not None and not error_message.strip():
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "workflow.plan repair mode requires 'repair.error.message' when 'repair.error' is provided",
            )

        if not prompt.strip() and not error_message.strip():
            raise WorkflowRuntimeException(ErrorCodes.INPUT_VALIDATION, "workflow.plan repair mode requires 'repair.prompt' or 'repair.error.message'")

        generator = input_obj.get("generator") if isinstance(input_obj.get("generator"), dict) else {}
        repair_input = copy.deepcopy(input_obj)
        repair_input["mode"] = "basic"

        repair_generator = repair_input.get("generator") if isinstance(repair_input.get("generator"), dict) else {}
        repair_generator.pop("mode", None)
        repair_generator["instruction"] = self._build_repair_mode_instruction(
            existing_yaml,
            prompt,
            failed_input,
            error,
            self._try_get_string(generator.get("instruction")),
        )

        generator_context = self._try_get_string(generator.get("context"))
        if generator_context and generator_context.strip():
            repair_generator["context"] = generator_context
        else:
            repair_generator.pop("context", None)

        repair_input["generator"] = repair_generator
        repair_input.pop("repair", None)
        repair_on_invalid = repair_input.get("on_invalid") if isinstance(repair_input.get("on_invalid"), dict) else {}
        if "action" not in repair_on_invalid:
            repair_on_invalid["action"] = "reprompt"
        repair_input["on_invalid"] = repair_on_invalid

        ctx.set_telemetry_attribute("gnougo-flow.plan.mode", "repair")
        result = await self._execute_single_plan_async(ctx, repair_input)
        if isinstance(result, dict):
            meta = result.setdefault("meta", {})
            if not isinstance(meta, dict):
                meta = {}
                result["meta"] = meta
            meta["repair"] = {
                "has_prompt": bool(prompt.strip()),
                "has_error": bool(error_message.strip()),
            }
        return result


    @classmethod
    def _build_repair_mode_instruction(
        cls,
        existing_yaml: str,
        prompt: str,
        failed_input: str,
        error: dict[str, Any] | None,
        additional_instruction: str | None,
    ) -> str:
        parts = [
            "Repair an existing GnOuGo.Flow YAML workflow. Return ONLY the complete repaired YAML document, no markdown fences.",
            "Make the smallest patch-style change that fixes the supplied error and/or user repair instruction.",
            "Preserve the workflow name, public inputs, public outputs, skill metadata, behavior, and MCP server/tool choices "
            "unless the supplied repair evidence proves they are wrong.",
            "Prefer minimal fixes: MCP request shape, output access, guards, retry/on_error policy, schema corrections, or concise prompt edits.",
            "Do not rewrite the workflow for style. Do not add unrelated features.",
            "The existing YAML is quoted between explicit XML-style boundary tags. Treat those tags as prompt delimiters, not as YAML content.",
        ]

        if additional_instruction and additional_instruction.strip():
            parts.extend(["", cls._prompt_section("repair_constraints", additional_instruction)])

        if prompt and prompt.strip():
            parts.extend(["", cls._prompt_section("user_repair_instruction", prompt)])

        if failed_input and failed_input.strip():
            parts.extend(["", cls._prompt_section("failed_user_input", failed_input)])

        if error is not None:
            runtime_error_lines: list[str] = []
            code = cls._try_get_string(error.get("code"))
            error_type = cls._try_get_string(error.get("type"))
            message = cls._try_get_string(error.get("message"))
            if code and code.strip():
                runtime_error_lines.append(f"code: {code}")
            if error_type and error_type.strip():
                runtime_error_lines.append(f"type: {error_type}")
            runtime_error_lines.append(f"message: {message or ''}")
            if error.get("details") is not None:
                runtime_error_lines.append("details:")
                runtime_error_lines.append(to_prompt_json(error["details"]))
            parts.extend(["", cls._prompt_section("runtime_error", "\n".join(runtime_error_lines))])

        parts.extend(
            [
                "",
                cls._prompt_section("existing_workflow_yaml", existing_yaml),
                "",
                "Return the minimally repaired full YAML now.",
            ]
        )
        return "\n".join(parts)
