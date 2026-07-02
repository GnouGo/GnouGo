from __future__ import annotations

from .shared import *  # noqa: F401,F403


class _WorkflowPlanTelemetryMixin:
    @staticmethod
    def _add_pipeline_extraction_retry_telemetry(ctx: StepExecutionContext, attempt: int, max_attempts: int, exc: Exception) -> None:
        ctx.add_telemetry_event(
            "gnougo-flow.step.thinking",
            [
                (
                    "gnougo-flow.thinking.message",
                    f"Pipeline extraction attempt {attempt}/{max_attempts} failed; retrying mark_extractable_blocks with validation feedback.",
                ),
                ("gnougo-flow.thinking.level", "info"),
            ],
        )
        ctx.add_telemetry_event(
            "gnougo-flow.plan.pipeline.extractable_blocks_retry",
            [
                ("gnougo-flow.plan.attempt", attempt),
                ("gnougo-flow.plan.max_attempts", max_attempts),
                ("error.type", type(exc).__name__),
                ("error.message", str(exc)),
            ],
        )


    @staticmethod
    def _add_prefilter_usage_event(
        ctx: StepExecutionContext,
        usage: Any,
        model: str | None,
        provider: str | None,
        phase: str,
        event_name: str,
    ) -> None:
        if not isinstance(usage, dict):
            return

        attrs: list[tuple[str, Any]] = [
            ("gnougo-flow.plan.phase", phase),
            ("gen_ai.operation.name", "chat"),
            ("gen_ai.system", provider or "unknown"),
        ]
        if model:
            attrs.append(("gen_ai.request.model", model))

        input_tokens = usage.get("prompt_tokens", usage.get("input_tokens"))
        output_tokens = usage.get("completion_tokens", usage.get("output_tokens"))
        total_tokens = usage.get("total_tokens")
        if input_tokens is not None:
            attrs.append(("gen_ai.usage.input_tokens", int(input_tokens)))
        if output_tokens is not None:
            attrs.append(("gen_ai.usage.output_tokens", int(output_tokens)))
        if total_tokens is not None:
            attrs.append(("gen_ai.usage.total_tokens", int(total_tokens)))

        ctx.add_telemetry_event(event_name, attrs)


    @staticmethod
    def _add_usage_attributes(span: Any, usage: Any) -> None:
        if not isinstance(usage, dict):
            return

        input_tokens = usage.get("prompt_tokens", usage.get("input_tokens"))
        output_tokens = usage.get("completion_tokens", usage.get("output_tokens"))
        total_tokens = usage.get("total_tokens")
        if input_tokens is not None:
            span.set_attribute("gen_ai.usage.input_tokens", int(input_tokens))
        if output_tokens is not None:
            span.set_attribute("gen_ai.usage.output_tokens", int(output_tokens))
        if total_tokens is not None:
            span.set_attribute("gen_ai.usage.total_tokens", int(total_tokens))
