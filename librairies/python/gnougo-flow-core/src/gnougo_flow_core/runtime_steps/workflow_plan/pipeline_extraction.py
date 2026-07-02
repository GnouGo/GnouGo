from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanPipelineExtractionMixin:
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


    @classmethod
    def _build_mark_extractable_blocks_repair_prompt(
        cls,
        normalized_markdown: str,
        previous_annotated_markdown: str | None,
        validation_errors: list[str],
        pipeline_mcp_servers_doc: str | None = None,
        use_structured_extraction: bool = False,
    ) -> str:
        parts = [
            cls._build_mark_extractable_blocks_prompt(
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
            parts.extend(["", cls._prompt_section("invalid_annotated_markdown", previous_annotated_markdown)])
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
