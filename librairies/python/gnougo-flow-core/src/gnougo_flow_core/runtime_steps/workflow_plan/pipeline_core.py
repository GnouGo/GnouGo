from __future__ import annotations

from .shared import *  # noqa: F401,F403
from gnougo_flow_core.runtime import _extract_usage_telemetry


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
        preflight = await self._run_capability_preflight_async(
            ctx,
            input_obj,
            raw_prompt,
            provider,
            model,
            reasoning,
        )

        ctx.set_telemetry_attribute("gnougo-flow.plan.mode", "pipeline")
        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            [
                ("gnougo-flow.thinking.message", "Preparing workflow generation prompt through pipeline mode."),
                ("gnougo-flow.thinking.level", "thinking"),
            ],
        )

        use_structured_generation = await self._should_use_strict_planner_response(ctx, provider, model)
        # Lock one response mode for the complete pipeline run. A schema-valid response
        # observed during extraction must not make later leaves silently switch modes.
        generator["pipeline_use_structured_generation"] = use_structured_generation
        ctx.set_telemetry_attribute(
            "gnougo-flow.plan.pipeline.generation_response_mode",
            "structured" if use_structured_generation else "legacy_text",
        )
        normalized_markdown = await self._normalize_user_prompt(
            ctx, raw_prompt, provider, model, reasoning, use_structured_generation
        )
        locked_prompt = self._build_locked_capability_prompt(preflight)
        if locked_prompt:
            normalized_markdown += "\n\n" + locked_prompt
        use_structured_extraction = use_structured_generation
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
        self._attach_pipeline_capability_ownership(extraction, preflight)
        for spec in extraction.subworkflows:
            spec.generation_prompt = self._build_subworkflow_generation_prompt(
                spec.name,
                spec.goal,
                spec.inputs,
                spec.outputs,
                spec.content,
                spec.planned_tools,
                spec.output_schemas,
            )

        generated_leaves = list(
            await asyncio.gather(
                *(
                    self._generate_leaf_workflow_async(ctx, input_obj, generator, spec)
                    for spec in extraction.subworkflows
                )
            )
        )

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
            use_structured_generation,
        )
        max_attempts = self._get_pipeline_generation_max_attempts(input_obj)
        previous_response: str | None = None
        previous_error: str | None = None
        last_error: Exception | None = None
        final_yaml: str | None = None
        final_doc: WorkflowDocument | None = None
        main_retry_count = 0
        contract_fingerprint = self._planner_fingerprint("pipeline_main_assembly", base_prompt)
        base_candidate_fingerprint = ""
        diagnostic_fingerprint = ""
        diagnostic_codes: list[str] = []
        best_response: str | None = None
        best_error: str | None = None
        best_candidate_fingerprint: str | None = None
        best_diagnostic_codes: set[str] | None = None
        best_diagnostic_identities: set[str] | None = None
        best_diagnostic_fingerprint: str | None = None
        best_validation_progress = -1
        non_improving_responses = 0

        for attempt in range(1, max_attempts + 1):
            candidate_validation_progress = 0
            prompt = base_prompt if previous_error is None else self._build_main_assembly_repair_prompt(base_prompt, previous_response, previous_error)
            try:
                with ctx.begin_telemetry_span(
                    "workflow.plan.pipeline.main_assembly",
                    "main_assembly",
                    [
                        ("gen_ai.operation.name", "chat"),
                        ("gen_ai.system", provider or "unknown"),
                        ("gen_ai.request.model", model),
                        ("gnougo-flow.plan.attempt", attempt),
                    ],
                ) as span:
                    if use_structured_generation:
                        schema = self._build_workflow_generation_response_schema(
                            contract_fingerprint,
                            base_candidate_fingerprint,
                            diagnostic_fingerprint,
                            diagnostic_codes,
                            main_assembly=True,
                        )
                        response = await self._execute_strict_planner_response(
                            ctx,
                            "pipeline.main_assembly",
                            prompt,
                            provider,
                            model,
                            reasoning,
                            schema,
                            phase_attempt=attempt,
                            max_attempts=max_attempts,
                            contract_fingerprint=contract_fingerprint,
                            base_candidate_fingerprint=base_candidate_fingerprint,
                            diagnostic_fingerprint=diagnostic_fingerprint,
                            contract_epoch=1,
                        )
                        assembly_text = self._compose_structured_main_assembly_response(response)
                    else:
                        response = await ctx.engine.call_llm_async(
                            LLMRequest(
                                provider=provider,
                                model=model,
                                prompt=prompt,
                                reasoning=reasoning,
                                use_background_mode=True,
                            )
                        )
                        self._add_usage_attributes(
                            span, response.usage, model, provider, ctx.engine.llm_options
                        )
                        _extract_usage_telemetry(ctx, response.usage, model, provider)
                        assembly_text = response.text or ""
                previous_response = assembly_text
                assembly = self._parse_generated_main_assembly(assembly_text)
                candidate_validation_progress = 10
                main_inputs = self._resolve_main_input_contract(configured_main_inputs, assembly, generated_leaf_inputs)
                assembly.main_workflow_node["inputs"] = copy.deepcopy(main_inputs)
                self._ensure_main_workflow_outputs(assembly.main_workflow_node, extraction.subworkflows)
                self._validate_declared_main_input_references(assembly.main_workflow_node, main_inputs)
                candidate_validation_progress = 30

                candidate_yaml = self._compose_pipeline_workflow_yaml(input_obj, generator, extraction, generated_leaves, assembly, main_inputs)
                candidate_doc = self._parse_and_validate_generated_workflow(candidate_yaml)
                candidate_validation_progress = 50
                self._enforce_pipeline_workflow_hierarchy(candidate_doc, {leaf.name for leaf in generated_leaves})
                self._validate_pipeline_leaf_call_arguments(candidate_doc, generated_leaves)
                self._validate_pipeline_main_graph_boundaries(candidate_doc)
                self._validate_pipeline_main_leaf_output_contracts(candidate_doc, generated_leaves)
                self._validate_pipeline_main_dataflow_quality(candidate_doc)
                candidate_validation_progress = 80
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
                self._validate_locked_capabilities(candidate_doc, preflight)
                candidate_validation_progress = 100

                final_yaml = candidate_yaml
                final_doc = candidate_doc
                break
            except Exception as exc:
                if isinstance(exc, WorkflowRuntimeException) and exc.code == ErrorCodes.LLM_SCHEMA:
                    raise
                last_error = exc
                candidate_fingerprint = self._planner_fingerprint(previous_response)
                current_diagnostic_codes = set(self._planner_diagnostic_codes(exc))
                current_diagnostic_identities = self._planner_diagnostic_identities(exc)
                candidate_is_new = candidate_fingerprint != best_candidate_fingerprint
                diagnostics_decreased = best_diagnostic_identities is not None and self._is_strict_diagnostic_decrease(
                    current_diagnostic_identities, best_diagnostic_identities
                )
                candidate_improved = candidate_is_new and (
                    best_response is None
                    or candidate_validation_progress > best_validation_progress
                    or candidate_validation_progress == best_validation_progress
                    and diagnostics_decreased
                )
                if candidate_improved:
                    best_response = previous_response
                    best_error = self._build_structured_plan_error(exc)
                    best_candidate_fingerprint = candidate_fingerprint
                    best_diagnostic_codes = current_diagnostic_codes
                    best_diagnostic_identities = current_diagnostic_identities
                    best_diagnostic_fingerprint = self._normalize_plan_diagnostic_fingerprint(exc)
                    best_validation_progress = candidate_validation_progress
                    non_improving_responses = 0
                elif best_response is not None:
                    non_improving_responses += 1
                    previous_response = best_response
                    if non_improving_responses >= 2:
                        raise WorkflowRuntimeException(
                            ErrorCodes.WORKFLOW_PLAN_REPAIR_STALLED,
                            "workflow.plan main assembly stopped because two responses failed to improve the best validated candidate.",
                            details={
                                "attempt": attempt,
                                "candidate_fingerprint": candidate_fingerprint,
                                "best_candidate_fingerprint": best_candidate_fingerprint,
                                "validation_progress": candidate_validation_progress,
                                "best_validation_progress": best_validation_progress,
                                "stall_reason": "validation_progress_regression" if candidate_is_new else "candidate_unchanged",
                            },
                        ) from exc
                if attempt >= max_attempts:
                    break
                main_retry_count += 1
                previous_error = best_error or self._build_structured_plan_error(exc)
                base_candidate_fingerprint = best_candidate_fingerprint or candidate_fingerprint
                diagnostic_fingerprint = best_diagnostic_fingerprint or self._normalize_plan_diagnostic_fingerprint(exc)
                diagnostic_codes = sorted(best_diagnostic_codes or current_diagnostic_codes)

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
                "capability_preflight": self._capability_preflight_metadata(preflight),
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


    async def _normalize_user_prompt(
        self,
        ctx: StepExecutionContext,
        raw_prompt: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
        use_structured_output: bool,
    ) -> str:
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
        if not use_structured_output:
            return await self._execute_pipeline_llm_text_phase(ctx, "normalize_user_prompt", prompt, provider, model, reasoning)
        schema = self._build_normalization_response_schema()
        contract_fingerprint = self._planner_fingerprint(
            "normalize_user_prompt", self._PLANNER_RESPONSE_SCHEMA_VERSION
        )
        prompt += "\n\nReturn only the strict JSON response object. Put the complete normalized Markdown in `normalized_markdown`."
        response = await self._execute_strict_planner_response(
            ctx,
            "pipeline.normalize_user_prompt",
            prompt,
            provider,
            model,
            reasoning,
            schema,
            phase_attempt=1,
            max_attempts=1,
            contract_fingerprint=contract_fingerprint,
            base_candidate_fingerprint="",
            diagnostic_fingerprint="",
            contract_epoch=1,
        )
        return self._required_response_string(response, "normalized_markdown", "normalize_user_prompt").strip()
