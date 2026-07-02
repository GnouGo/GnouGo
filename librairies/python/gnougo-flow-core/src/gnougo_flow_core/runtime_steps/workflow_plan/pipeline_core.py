from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanPipelineCoreMixin:
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
