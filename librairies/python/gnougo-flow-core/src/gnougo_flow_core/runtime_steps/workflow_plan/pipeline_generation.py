from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanPipelineGenerationMixin:
    @staticmethod
    def _normalize_workflow_schema_type(type_name: str) -> str:
        normalized = type_name.strip().lower()
        return normalized if normalized in {"string", "number", "integer", "boolean", "array", "object", "dictionary", "any"} else "any"


    @classmethod
    def _build_subworkflow_generation_prompt(
        cls,
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
            *cls._MCP_INPUT_CONTRACT_CHECKLIST,
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
                    cls._serialize_yaml_value(output_schemas),
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


    @classmethod
    def _format_leaf_generation_error(cls, leaf_name: str, attempt: int, exc: Exception) -> str:
        return (
            f"Leaf workflow: {leaf_name}\n"
            f"Failed attempt: {attempt}\n"
            f"Error type: {type(exc).__name__}\n"
            f"Structured error: {cls._build_structured_plan_error(exc)}\n"
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
