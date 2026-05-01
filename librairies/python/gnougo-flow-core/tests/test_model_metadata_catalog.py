from __future__ import annotations

import json
from pathlib import Path

from gnougo_flow_core.model_metadata import LLMModelMetadataResolver, estimate_cost, sanitize_llm_request
from gnougo_flow_core.models import LLMModelMetadata, LLMOptions, LLMRequest, LLMTool, ModelCapabilityMetadata, ModelPricingMetadata


def test_resolve_builtin_reasoning_model_metadata() -> None:
    metadata = LLMModelMetadataResolver().resolve("openai", "o4-mini")

    assert metadata.id == "o4-mini"
    assert metadata.context_window_tokens == 200000
    assert metadata.max_output_tokens == 100000
    assert metadata.pricing is not None
    assert metadata.pricing.input_per_1m_tokens == 1.10
    assert metadata.capabilities.supports_temperature is False
    assert metadata.capabilities.supports_reasoning_effort is True
    assert "temperature" in (metadata.capabilities.unsupported_request_parameters or [])


def test_sanitize_removes_temperature_for_reasoning_model() -> None:
    metadata = LLMModelMetadataResolver().resolve("openai", "o4-mini")
    request = LLMRequest(model="o4-mini", prompt="hello", temperature=0.7, reasoning="high")

    sanitized = sanitize_llm_request(request, metadata)

    assert sanitized.temperature is None
    assert sanitized.reasoning == "high"


def test_inline_override_disables_reasoning_and_tools() -> None:
    options = LLMOptions(
        model_overrides={
            "custom-model": LLMModelMetadata(
                id="custom-model",
                provider_type="openai",
                capabilities=ModelCapabilityMetadata(
                    supports_temperature=True,
                    supports_reasoning_effort=False,
                    supports_tools=False,
                ),
            )
        }
    )
    metadata = LLMModelMetadataResolver(options).resolve("openai", "custom-model")
    request = LLMRequest(
        model="custom-model",
        prompt="hello",
        temperature=0.2,
        reasoning="high",
        tools=[LLMTool(name="tool")],
    )

    sanitized = sanitize_llm_request(request, metadata)

    assert sanitized.temperature == 0.2
    assert sanitized.reasoning is None
    assert sanitized.tools is None


def test_external_metadata_file_and_alias(tmp_path: Path) -> None:
    path = tmp_path / "models.json"
    path.write_text(
        json.dumps(
            {
                "models": {
                    "my-model": {
                        "providerType": "openai",
                        "contextWindowTokens": 999,
                        "maxOutputTokens": 111,
                        "pricing": {"inputPer1MTokens": 0.5, "outputPer1MTokens": 1.5},
                        "capabilities": {"supportsTemperature": False, "supportsReasoningEffort": False},
                    }
                },
                "aliases": {"mine": "my-model"},
            }
        ),
        encoding="utf-8",
    )

    metadata = LLMModelMetadataResolver(LLMOptions(model_metadata_files=[str(path)])).resolve("openai", "mine")

    assert metadata.id == "my-model"
    assert metadata.context_window_tokens == 999
    assert metadata.max_output_tokens == 111
    assert metadata.pricing is not None
    assert metadata.pricing.output_per_1m_tokens == 1.5
    assert metadata.capabilities.supports_temperature is False
    assert metadata.capabilities.supports_reasoning_effort is False


def test_estimate_cost_uses_builtin_pricing() -> None:
    assert estimate_cost("gpt-4o-mini", input_tokens=500_000, output_tokens=100_000) == 0.135


def test_options_accept_csharp_casing() -> None:
    options = LLMOptions.model_validate(
        {
            "ModelOverrides": {
                "x": {
                    "id": "x",
                    "pricing": {"inputPer1MTokens": 1.0, "outputPer1MTokens": 2.0},
                    "capabilities": {"supportsTemperature": False},
                }
            }
        }
    )

    metadata = LLMModelMetadataResolver(options).resolve("openai", "x")

    assert metadata.pricing == ModelPricingMetadata(input_per_1m_tokens=1.0, output_per_1m_tokens=2.0)
    assert metadata.capabilities.supports_temperature is False
