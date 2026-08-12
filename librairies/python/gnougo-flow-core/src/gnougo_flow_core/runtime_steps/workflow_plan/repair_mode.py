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
        repair_scope = repair.get("scope") if isinstance(repair.get("scope"), dict) else None
        scope_workflow = self._try_get_string(repair_scope.get("workflow")) if repair_scope else None
        scope_step_id = self._try_get_string(repair_scope.get("step_id")) if repair_scope else None
        if bool(scope_workflow) != bool(scope_step_id):
            raise WorkflowRuntimeException(
                ErrorCodes.INPUT_VALIDATION,
                "workflow.plan repair scope requires both workflow and step_id",
            )
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
        if scope_workflow and scope_step_id:
            repair_generator["instruction"] += (
                "\n\n<surgical_repair_scope>\n"
                f"workflow: {scope_workflow}\nstep_id: {scope_step_id}\n"
                "Change only the target step and direct consumers of its output. Preserve workflow topology, "
                "public contracts, step identities/types/order, branches, and unrelated expressions exactly.\n"
                "</surgical_repair_scope>"
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
        if scope_workflow and scope_step_id and isinstance(result, dict):
            repaired_yaml = result.get("yaml")
            if not isinstance(repaired_yaml, str):
                raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Scoped repair did not return YAML")
            self._validate_surgical_repair(existing_yaml, repaired_yaml, scope_workflow, scope_step_id)
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


    @staticmethod
    def _validate_surgical_repair(
        existing_yaml: str,
        repaired_yaml: str,
        workflow_name: str,
        step_id: str,
    ) -> None:
        try:
            before = yaml.safe_load(existing_yaml)
            after = yaml.safe_load(repaired_yaml)
        except Exception as exc:
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, f"Scoped repair YAML comparison failed: {exc}") from exc
        if not isinstance(before, dict) or not isinstance(after, dict):
            raise WorkflowRuntimeException(ErrorCodes.TEMPLATE_PLAN, "Scoped repair requires object YAML documents")

        errors: list[str] = []
        for key in ("version", "name", "skill", "entrypoint", "exports", "functions"):
            if before.get(key) != after.get(key):
                errors.append(f"root.{key} changed outside repair scope")
        before_workflows = before.get("workflows")
        after_workflows = after.get("workflows")
        if not isinstance(before_workflows, dict) or not isinstance(after_workflows, dict):
            errors.append("workflow topology changed")
        elif list(before_workflows) != list(after_workflows):
            errors.append("workflow identities/order changed")
        else:
            for name, before_workflow in before_workflows.items():
                after_workflow = after_workflows.get(name)
                if name != workflow_name:
                    if before_workflow != after_workflow:
                        errors.append(f"workflow '{name}' changed outside repair scope")
                    continue
                if not isinstance(before_workflow, dict) or not isinstance(after_workflow, dict):
                    errors.append(f"workflow '{name}' structure changed")
                    continue
                for contract_key in ("inputs", "outputs", "functions"):
                    if before_workflow.get(contract_key) != after_workflow.get(contract_key):
                        errors.append(f"workflow '{name}' {contract_key} contract changed")
                before_nodes = _flatten_repair_steps(before_workflow)
                after_nodes = _flatten_repair_steps(after_workflow)
                if [path for path, _ in before_nodes] != [path for path, _ in after_nodes]:
                    errors.append(f"workflow '{name}' step topology/order changed")
                    continue
                target_paths = [path for path, step in before_nodes if step.get("id") == step_id]
                if len(target_paths) != 1:
                    errors.append(f"repair target step '{step_id}' was not found exactly once")
                    continue
                target_path = target_paths[0]
                for (path, old_step), (_, new_step) in zip(before_nodes, after_nodes):
                    if old_step.get("id") != new_step.get("id") or old_step.get("type") != new_step.get("type"):
                        errors.append(f"step identity/type changed at {path}")
                        continue
                    is_target = path == target_path
                    is_direct_consumer = f"data.steps.{step_id}" in json.dumps(old_step, ensure_ascii=False)
                    if not is_target and not is_direct_consumer and old_step != new_step:
                        errors.append(f"unrelated step '{old_step.get('id')}' changed")

        if errors:
            raise WorkflowRuntimeException(
                ErrorCodes.TEMPLATE_PLAN,
                "Scoped workflow repair violated surgical constraints: " + "; ".join(errors),
                details={
                    "workflow": workflow_name,
                    "step_id": step_id,
                    "validation_errors": errors,
                },
            )


def _flatten_repair_steps(workflow: dict[str, Any]) -> list[tuple[str, dict[str, Any]]]:
    result: list[tuple[str, dict[str, Any]]] = []

    def walk(steps: Any, prefix: str) -> None:
        if not isinstance(steps, list):
            return
        for index, step in enumerate(steps):
            if not isinstance(step, dict):
                continue
            path = f"{prefix}[{index}]"
            result.append((path, step))
            walk(step.get("steps"), path + ".steps")
            branches = step.get("branches")
            if isinstance(branches, list):
                for branch_index, branch in enumerate(branches):
                    if isinstance(branch, dict):
                        walk(branch.get("steps"), f"{path}.branches[{branch_index}].steps")
            cases = step.get("cases")
            if isinstance(cases, list):
                for case_index, case in enumerate(cases):
                    if isinstance(case, dict):
                        walk(case.get("steps"), f"{path}.cases[{case_index}].steps")
            walk(step.get("default"), path + ".default")

    walk(workflow.get("steps"), "steps")
    walk(workflow.get("finally"), "finally")
    return result
