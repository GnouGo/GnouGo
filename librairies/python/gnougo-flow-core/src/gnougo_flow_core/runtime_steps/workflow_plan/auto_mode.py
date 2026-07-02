from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanAutoModeMixin:
    async def _classify_plan_mode_async(self, ctx: StepExecutionContext, input_obj: dict[str, Any]) -> _WorkflowPlanModeSelection:
        generator = input_obj.get("generator") if isinstance(input_obj.get("generator"), dict) else {}
        provider, model = ctx.engine.resolve_llm_target(generator.get("provider"), generator.get("model"))
        model = model or "gpt-4"
        reasoning_raw = generator.get("reasoning")
        reasoning = reasoning_raw.strip() if isinstance(reasoning_raw, str) and reasoning_raw.strip() else "low"
        prompt = self._build_auto_mode_classification_prompt(input_obj)

        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            [
                ("gnougo-flow.thinking.message", "Classifying workflow planning complexity for auto mode."),
                ("gnougo-flow.thinking.level", "thinking"),
            ],
        )

        try:
            with ctx.begin_telemetry_span(
                "workflow.plan.classify_mode",
                "classification",
                [
                    ("gen_ai.operation.name", "chat"),
                    ("gen_ai.system", provider or "unknown"),
                    ("gen_ai.request.model", model),
                    ("gnougo-flow.plan.mode.requested", "auto"),
                    ("gnougo-flow.plan.auto.threshold", self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD),
                ],
            ) as span:
                if ctx.limits.log_step_content:
                    span.add_event(
                        "gen_ai.content.prompt",
                        [
                            ("gen_ai.prompt", prompt),
                            ("prompt.role", "user"),
                            ("gnougo-flow.plan.phase", "classification"),
                        ],
                    )
                response = await ctx.engine.call_llm_async(
                    LLMRequest(
                        provider=provider,
                        model=model,
                        prompt=prompt,
                        reasoning=reasoning,
                        structured_output_strict=True,
                        structured_output_schema={
                            "type": "object",
                            "additionalProperties": False,
                            "required": ["mode", "cyclomatic_complexity", "branch_count", "confidence", "reason"],
                            "properties": {
                                "mode": {"type": "string", "enum": ["basic", "pipeline"]},
                                "cyclomatic_complexity": {"type": "integer", "minimum": 1},
                                "branch_count": {"type": "integer", "minimum": 0},
                                "confidence": {"type": "number", "minimum": 0, "maximum": 1},
                                "reason": {"type": "string"},
                            },
                        },
                    )
                )
                span.set_attribute("gen_ai.response.model", model)
                span.set_attribute("gen_ai.response.finish_reason", "stop")
                self._add_usage_attributes(span, response.usage)
                if ctx.limits.log_step_content and response.text:
                    span.add_event(
                        "gen_ai.content.completion",
                        [
                            ("gen_ai.completion", response.text),
                            ("completion.role", "assistant"),
                            ("completion.finish_reason", "stop"),
                            ("gnougo-flow.plan.phase", "classification"),
                        ],
                    )

                selection = self._parse_plan_mode_selection(response)
                span.set_attribute("gnougo-flow.plan.mode.selected", selection.selected_mode)
                if selection.cyclomatic_complexity is not None:
                    span.set_attribute("gnougo-flow.plan.auto.cyclomatic_complexity", selection.cyclomatic_complexity)
                if selection.branch_count is not None:
                    span.set_attribute("gnougo-flow.plan.auto.branch_count", selection.branch_count)
                if selection.confidence is not None:
                    span.set_attribute("gnougo-flow.plan.auto.confidence", selection.confidence)
                if selection.used_fallback:
                    span.set_attribute("gnougo-flow.plan.auto.fallback", True)

            ctx.set_telemetry_attribute("gnougo-flow.plan.mode", selection.selected_mode)
            ctx.set_telemetry_attribute("gnougo-flow.plan.mode.source", "auto_fallback" if selection.used_fallback else "auto")
            return selection
        except Exception:
            fallback = _WorkflowPlanModeSelection(
                selected_mode="basic",
                reason="Classifier failed or returned invalid JSON; defaulted to basic mode.",
                used_fallback=True,
            )
            ctx.set_telemetry_attribute("gnougo-flow.plan.mode", fallback.selected_mode)
            ctx.set_telemetry_attribute("gnougo-flow.plan.mode.source", "auto_fallback")
            return fallback


    def _build_auto_mode_classification_prompt(self, input_obj: dict[str, Any]) -> str:
        generator = input_obj.get("generator") if isinstance(input_obj.get("generator"), dict) else {}
        raw_prompt = str(input_obj.get("raw_prompt") or generator.get("raw_prompt") or "")
        instruction = str(generator.get("instruction") or "")
        context_text = str(generator.get("context") or "")
        policy = json.dumps(input_obj.get("policy") or {}, indent=2, sort_keys=True)
        limits = json.dumps(input_obj.get("limits") or {}, indent=2, sort_keys=True)
        threshold = self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD
        return (
            "You classify a GnOuGo workflow.plan request before workflow generation.\n"
            "Return ONLY JSON that matches the requested schema.\n\n"
            "Decision rule:\n"
            f'- Choose "basic" when estimated cyclomatic complexity is less than {threshold} branching points and the workflow can be generated coherently in one plan.\n'
            f'- Choose "pipeline" when estimated cyclomatic complexity is {threshold} or more, or when the request should be decomposed into many small leaf workflows before assembling a main workflow.\n'
            "- Count branching points from conditions, switch/case paths, loops, retries, error handling, cleanup paths, validation branches, tool-orchestration choices, and state transitions.\n"
            '- Prefer "pipeline" when several independent phases, tools, or responsibilities would make one generated workflow brittle.\n'
            '- Prefer "basic" for simple linear flows, small conditionals, or requests with fewer than 10 meaningful branches.\n\n'
            f"<raw_prompt>\n{raw_prompt}\n</raw_prompt>\n\n"
            f"<generator_instruction>\n{instruction}\n</generator_instruction>\n\n"
            f"<generator_context>\n{context_text}\n</generator_context>\n\n"
            f"<policy_json>\n{policy}\n</policy_json>\n\n"
            f"<limits_json>\n{limits}\n</limits_json>"
        )


    def _parse_plan_mode_selection(self, response: LLMResponse) -> _WorkflowPlanModeSelection:
        payload = response.json_payload if isinstance(response.json_payload, dict) else None
        if payload is None and response.text:
            try:
                payload = json.loads(self._strip_markdown_code_fence(response.text).strip())
            except Exception:
                payload = None
        if not isinstance(payload, dict):
            return _WorkflowPlanModeSelection(
                selected_mode="basic",
                reason="Classifier returned non-JSON content; defaulted to basic mode.",
                used_fallback=True,
                raw_response=response.text,
            )

        mode = str(payload.get("mode") or "").strip().lower()
        complexity = self._coerce_int(payload.get("cyclomatic_complexity"))
        branch_count = self._coerce_int(payload.get("branch_count"))
        confidence = self._coerce_float(payload.get("confidence"))
        if mode not in {"basic", "pipeline"} and complexity is None and branch_count is None:
            return _WorkflowPlanModeSelection(
                selected_mode="basic",
                reason="Classifier JSON did not include a mode or complexity signal; defaulted to basic mode.",
                used_fallback=True,
                raw_response=response.text,
            )
        selected_mode = (
            "pipeline"
            if mode == "pipeline"
            or (complexity is not None and complexity >= self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD)
            or (branch_count is not None and branch_count >= self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD)
            else "basic"
        )
        return _WorkflowPlanModeSelection(
            selected_mode=selected_mode,
            cyclomatic_complexity=complexity,
            branch_count=branch_count,
            confidence=confidence,
            reason=str(payload.get("reason") or ""),
            raw_response=response.text,
        )


    def _attach_plan_mode_metadata(self, result: Any, mode: str, selection: _WorkflowPlanModeSelection | None) -> None:
        if not isinstance(result, dict):
            return
        meta = result.setdefault("meta", {})
        if not isinstance(meta, dict):
            meta = {}
            result["meta"] = meta
        meta["mode"] = mode
        if selection is None:
            return
        mode_selection: dict[str, Any] = {
            "source": "auto_fallback" if selection.used_fallback else "auto",
            "selected_mode": selection.selected_mode,
            "threshold": self._AUTO_MODE_BASIC_CYCLOMATIC_THRESHOLD,
        }
        if selection.cyclomatic_complexity is not None:
            mode_selection["cyclomatic_complexity"] = selection.cyclomatic_complexity
        if selection.branch_count is not None:
            mode_selection["branch_count"] = selection.branch_count
        if selection.confidence is not None:
            mode_selection["confidence"] = selection.confidence
        if selection.reason:
            mode_selection["reason"] = selection.reason
        meta["mode_selection"] = mode_selection
