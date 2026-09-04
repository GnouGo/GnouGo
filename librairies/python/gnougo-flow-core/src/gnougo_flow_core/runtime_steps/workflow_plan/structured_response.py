from __future__ import annotations

import hashlib
import json
from typing import Any

from gnougo_flow_core.errors import ErrorCodes, WorkflowRuntimeException
from gnougo_flow_core.json_schema_contract_validator import validate_instance, validate_schema
from gnougo_flow_core.models import LLMRequest, LLMResponse
from gnougo_flow_core.runtime import StepExecutionContext, _extract_usage_telemetry


class _WorkflowPlanStructuredResponseMixin:
    _PLANNER_RESPONSE_SCHEMA_VERSION = "workflow-plan-response-v1"

    @staticmethod
    def _planner_target_key(provider: str | None, model: str) -> str:
        return f"{(provider or '(default)').strip().lower()}\n{model.strip().lower()}"

    async def _should_use_strict_planner_response(
        self,
        ctx: StepExecutionContext,
        provider: str | None,
        model: str,
    ) -> bool:
        evidence = getattr(ctx.engine, "_workflow_plan_structured_targets", set())
        if self._planner_target_key(provider, model) in evidence:
            return True
        resolver = getattr(ctx.engine, "llm_capabilities", None)
        if resolver is None:
            return False
        try:
            return await resolver.supports_structured_output_async(provider, model) is True
        except Exception:
            return False

    def _record_strict_planner_response_evidence(
        self,
        ctx: StepExecutionContext,
        provider: str | None,
        model: str,
        payload: Any,
        schema: dict[str, Any],
    ) -> None:
        if not isinstance(payload, dict) or validate_instance(payload, schema):
            return
        evidence = getattr(ctx.engine, "_workflow_plan_structured_targets", None)
        if not isinstance(evidence, set):
            evidence = set()
            setattr(ctx.engine, "_workflow_plan_structured_targets", evidence)
        evidence.add(self._planner_target_key(provider, model))

    async def _execute_strict_planner_response(
        self,
        ctx: StepExecutionContext,
        phase: str,
        prompt: str,
        provider: str | None,
        model: str,
        reasoning: str | None,
        schema: dict[str, Any],
        *,
        phase_attempt: int,
        max_attempts: int | None,
        contract_fingerprint: str,
        base_candidate_fingerprint: str,
        diagnostic_fingerprint: str,
        contract_epoch: int,
    ) -> LLMResponse:
        schema_errors = validate_schema(schema, strict_profile=True)
        if schema_errors:
            raise WorkflowRuntimeException(
                ErrorCodes.LLM_SCHEMA,
                f"workflow.plan internal response schema for phase '{phase}' is invalid: {'; '.join(schema_errors)}",
            )

        for contract_attempt in (1, 2):
            attributes: list[tuple[str, Any]] = [
                ("gen_ai.operation.name", "chat"),
                ("gen_ai.system", provider or "unknown"),
                ("gen_ai.request.model", model),
                ("gnougo-flow.plan.response_mode", "structured"),
                ("gnougo-flow.plan.response_schema_version", self._PLANNER_RESPONSE_SCHEMA_VERSION),
                ("gnougo-flow.plan.phase", phase),
                ("gnougo-flow.plan.attempt", phase_attempt),
                ("gnougo-flow.plan.response_contract_attempt", contract_attempt),
                ("gnougo-flow.plan.contract_epoch", contract_epoch),
                ("gnougo-flow.plan.contract_fingerprint", contract_fingerprint),
                ("gnougo-flow.plan.base_candidate_fingerprint", base_candidate_fingerprint),
                ("gnougo-flow.plan.diagnostic_fingerprint", diagnostic_fingerprint),
            ]
            if max_attempts is not None:
                attributes.append(("gnougo-flow.plan.max_attempts", max_attempts))
            with ctx.begin_telemetry_span(
                f"workflow.plan.{phase}.structured_response",
                "structured_response",
                attributes,
            ) as span:
                response = await ctx.engine.call_llm_async(
                    LLMRequest(
                        provider=provider,
                        model=model,
                        prompt=prompt,
                        reasoning=reasoning,
                        use_background_mode=True,
                        structured_output_schema=schema,
                        structured_output_strict=True,
                    )
                )
                self._add_usage_attributes(span, response.usage, model, provider, ctx.engine.llm_options)
                _extract_usage_telemetry(ctx, response.usage, model, provider)
                payload = response.json_payload
                errors = validate_instance(payload, schema) if isinstance(payload, dict) else ["$: response did not contain parsed JSON"]
                addressed_codes = payload.get("addressed_diagnostic_codes") if isinstance(payload, dict) else None
                if (
                    isinstance(addressed_codes, list)
                    and all(isinstance(code, str) for code in addressed_codes)
                    and len(set(addressed_codes)) != len(addressed_codes)
                ):
                    errors.append("$.addressed_diagnostic_codes: values must be unique")
                if not errors:
                    self._record_strict_planner_response_evidence(ctx, provider, model, payload, schema)
                    span.set_attribute("gnougo-flow.plan.response_contract_status", "valid")
                    return response
                span.set_attribute("gnougo-flow.plan.response_contract_status", "invalid")
                span.set_attribute("gnougo-flow.plan.response_contract_error_count", len(errors))
                if contract_attempt == 1:
                    span.add_event(
                        "gnougo-flow.plan.structured_response.retry",
                        [
                            ("gnougo-flow.plan.phase", phase),
                            ("gnougo-flow.plan.response_schema_version", self._PLANNER_RESPONSE_SCHEMA_VERSION),
                            ("gnougo-flow.plan.response_contract_error_count", len(errors)),
                        ],
                    )
                    continue
                raise WorkflowRuntimeException(
                    ErrorCodes.LLM_SCHEMA,
                    f"workflow.plan phase '{phase}' returned JSON that did not satisfy its strict internal response contract after one exact retry: {'; '.join(errors[:8])}",
                )
        raise RuntimeError("unreachable structured workflow response retry state")

    @classmethod
    def _build_normalization_response_schema(cls) -> dict[str, Any]:
        return cls._strict_object_schema(
            {"normalized_markdown": {"type": "string", "minLength": 1}}
        )

    @classmethod
    def _build_workflow_generation_response_schema(
        cls,
        contract_fingerprint: str,
        base_candidate_fingerprint: str,
        diagnostic_fingerprint: str,
        diagnostic_codes: list[str],
        *,
        main_assembly: bool,
    ) -> dict[str, Any]:
        addressed_items: dict[str, Any] = {"type": "string"}
        if diagnostic_codes:
            addressed_items["enum"] = list(diagnostic_codes)
        properties: dict[str, Any] = {
            "schema_version": cls._single_string_enum(cls._PLANNER_RESPONSE_SCHEMA_VERSION),
            "contract_fingerprint": cls._single_string_enum(contract_fingerprint),
            "base_candidate_fingerprint": cls._single_string_enum(base_candidate_fingerprint),
            "diagnostic_fingerprint": cls._single_string_enum(diagnostic_fingerprint),
            "addressed_diagnostic_codes": {
                "type": "array",
                "items": addressed_items,
                "minItems": 1 if diagnostic_codes else 0,
                "maxItems": len(diagnostic_codes),
            },
        }
        if main_assembly:
            properties["document_yaml"] = {"type": "string", "minLength": 1}
            properties["graph_yaml"] = {"type": "string", "minLength": 1}
        else:
            properties["yaml"] = {"type": "string", "minLength": 1}
        return cls._strict_object_schema(properties)

    @classmethod
    def _append_generation_envelope_instruction(
        cls,
        prompt: str,
        contract_fingerprint: str,
        base_candidate_fingerprint: str,
        diagnostic_fingerprint: str,
        diagnostic_codes: list[str],
    ) -> str:
        addressed = (
            "an empty array"
            if not diagnostic_codes
            else "a non-empty subset of: " + ", ".join(diagnostic_codes)
        )
        return (
            prompt.rstrip()
            + "\n\nInternal response contract:\n"
            + "- Return only the strict JSON object selected by the response schema, not raw YAML or Markdown fences.\n"
            + "- Put the complete workflow YAML in `yaml`.\n"
            + f"- Echo schema_version `{cls._PLANNER_RESPONSE_SCHEMA_VERSION}`.\n"
            + f"- Echo contract_fingerprint `{contract_fingerprint}`.\n"
            + f"- Echo base_candidate_fingerprint `{base_candidate_fingerprint}`.\n"
            + f"- Echo diagnostic_fingerprint `{diagnostic_fingerprint}`.\n"
            + f"- addressed_diagnostic_codes must be {addressed}.\n"
            + "- These envelope values are immutable acknowledgements; they do not override deterministic validation."
        )

    @staticmethod
    def _strict_object_schema(properties: dict[str, Any]) -> dict[str, Any]:
        return {
            "type": "object",
            "properties": properties,
            "required": list(properties),
            "additionalProperties": False,
        }

    @staticmethod
    def _single_string_enum(value: str) -> dict[str, Any]:
        return {"type": "string", "enum": [value]}

    @staticmethod
    def _planner_fingerprint(*values: str | None) -> str:
        payload = "\n\x1f\n".join(value or "" for value in values)
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()

    @staticmethod
    def _planner_diagnostic_codes(exc: Exception) -> list[str]:
        codes: set[str] = set()
        current: BaseException | None = exc
        while current is not None:
            code = getattr(current, "code", None)
            if isinstance(code, str) and code:
                codes.add(code)
            current = current.__cause__ or current.__context__
        return sorted(codes or {ErrorCodes.TEMPLATE_PLAN})

    @staticmethod
    def _required_response_string(response: LLMResponse, property_name: str, phase: str) -> str:
        payload = response.json_payload
        value = payload.get(property_name) if isinstance(payload, dict) else None
        if isinstance(value, str) and value.strip():
            return value
        raise WorkflowRuntimeException(
            ErrorCodes.LLM_SCHEMA,
            f"workflow.plan phase '{phase}' did not return required internal field '{property_name}'.",
        )

    def _compose_structured_main_assembly_response(self, response: LLMResponse) -> str:
        document = self._strip_markdown_code_fence(
            self._required_response_string(response, "document_yaml", "main assembly")
        ).strip()
        graph = self._strip_markdown_code_fence(
            self._required_response_string(response, "graph_yaml", "main assembly")
        ).strip()
        return "document:\n" + self._indent_planner_yaml(document) + "\ngraph:\n" + self._indent_planner_yaml(graph)

    @staticmethod
    def _indent_planner_yaml(value: str) -> str:
        return "\n".join("  " + line for line in value.replace("\r\n", "\n").split("\n"))
