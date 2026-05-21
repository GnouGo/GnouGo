from __future__ import annotations

import json
from pathlib import Path

from gnougo_flow_core.model_metadata import (
    LLMModelMetadataResolver,
    estimate_cost,
    get_missing_required_metadata_fields,
    has_complete_required_metadata,
    sanitize_llm_request,
)
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


def test_provider_qualified_inline_overrides_win_over_generic_model_id() -> None:
    options = LLMOptions(
        model_overrides={
            "shared-model": LLMModelMetadata(id="shared-model", pricing=ModelPricingMetadata(input_per_1m_tokens=1.0, output_per_1m_tokens=2.0)),
            "openai/shared-model": LLMModelMetadata(id="shared-model", pricing=ModelPricingMetadata(input_per_1m_tokens=3.0, output_per_1m_tokens=4.0)),
            "copilot/shared-model": LLMModelMetadata(id="shared-model", pricing=ModelPricingMetadata(input_per_1m_tokens=5.0, output_per_1m_tokens=6.0)),
        }
    )
    resolver = LLMModelMetadataResolver(options)

    openai = resolver.resolve("openai", "shared-model")
    copilot = resolver.resolve("copilot", "shared-model")

    assert openai.id == "shared-model"
    assert openai.provider_type == "openai"
    assert openai.pricing is not None
    assert openai.pricing.input_per_1m_tokens == 3.0
    assert copilot.id == "shared-model"
    assert copilot.provider_type == "copilot"
    assert copilot.pricing is not None
    assert copilot.pricing.input_per_1m_tokens == 5.0
    assert estimate_cost("shared-model", input_tokens=1_000_000, output_tokens=1_000_000, options=options, provider_type="openai") == 7.0
    assert estimate_cost("shared-model", input_tokens=1_000_000, output_tokens=1_000_000, options=options, provider_type="copilot") == 11.0


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


def test_provider_qualified_external_metadata_file(tmp_path: Path) -> None:
    path = tmp_path / "models.json"
    path.write_text(
        json.dumps(
            {
                "models": {
                    "openai/shared-model": {
                        "contextWindowTokens": 111,
                        "pricing": {"inputPer1MTokens": 0.5, "outputPer1MTokens": 1.5},
                    },
                    "copilot/shared-model": {
                        "contextWindowTokens": 222,
                        "pricing": {"inputPer1MTokens": 2.5, "outputPer1MTokens": 3.5},
                    },
                }
            }
        ),
        encoding="utf-8",
    )
    resolver = LLMModelMetadataResolver(LLMOptions(model_metadata_files=[str(path)]))

    openai = resolver.resolve("openai", "shared-model")
    copilot = resolver.resolve("copilot", "shared-model")

    assert openai.id == "shared-model"
    assert openai.provider_type == "openai"
    assert openai.context_window_tokens == 111
    assert openai.pricing is not None
    assert openai.pricing.input_per_1m_tokens == 0.5
    assert copilot.id == "shared-model"
    assert copilot.provider_type == "copilot"
    assert copilot.context_window_tokens == 222
    assert copilot.pricing is not None
    assert copilot.pricing.input_per_1m_tokens == 2.5
    openai_configured = [
        metadata for metadata in resolver.list_configured_metadata("openai") if metadata.id == "shared-model" and metadata.provider_type == "openai"
    ]
    copilot_configured = [
        metadata for metadata in resolver.list_configured_metadata("copilot") if metadata.id == "shared-model" and metadata.provider_type == "copilot"
    ]
    assert len(openai_configured) == 1
    assert len(copilot_configured) == 1


def test_estimate_cost_uses_builtin_pricing() -> None:
    assert estimate_cost("gpt-4o-mini", input_tokens=500_000, output_tokens=100_000) == 0.135


def test_provider_specific_builtin_metadata_for_duplicate_model_ids() -> None:
    resolver = LLMModelMetadataResolver()

    openai = resolver.resolve("openai", "gpt-4o")
    copilot = resolver.resolve("copilot", "gpt-4o")

    assert openai.id == "gpt-4o"
    assert openai.provider_type == "openai"
    assert openai.context_window_tokens == 128000
    assert openai.pricing is not None
    assert openai.pricing.input_per_1m_tokens == 2.5

    assert copilot.id == "gpt-4o"
    assert copilot.provider_type == "copilot"
    assert copilot.context_window_tokens == 64000
    assert copilot.pricing is None


def test_normalizes_provider_aliases_for_builtin_metadata() -> None:
    metadata = LLMModelMetadataResolver().resolve("anthropic", "claude-sonnet-4-20250514")

    assert metadata.id == "claude-sonnet-4-20250514"
    assert metadata.provider_type == "claude"
    assert metadata.pricing is not None
    assert metadata.pricing.input_per_1m_tokens == 3.0


def test_does_not_treat_non_provider_slash_prefix_as_vendor_prefix() -> None:
    metadata = LLMModelMetadataResolver().resolve("openai", "1024-x-1024/dall-e-2")

    assert metadata.id == "1024-x-1024/dall-e-2"
    assert metadata.provider_type == "openai"


def test_estimate_cost_uses_user_override_pricing() -> None:
    options = LLMOptions(
        model_overrides={
            "custom-priced-model": LLMModelMetadata(
                id="custom-priced-model",
                pricing=ModelPricingMetadata(input_per_1m_tokens=3.0, output_per_1m_tokens=9.0),
            )
        }
    )

    assert estimate_cost("custom-priced-model", input_tokens=1_000_000, output_tokens=500_000, options=options, provider_type="openai") == 7.5


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


def test_missing_required_metadata_fields_for_unknown_model() -> None:
    metadata = LLMModelMetadataResolver().resolve("openai", "vendor/new-model")

    missing = get_missing_required_metadata_fields(metadata)

    assert metadata.id == "vendor/new-model"
    assert "contextWindowTokens" in missing
    assert "maxInputTokens" in missing
    assert "maxOutputTokens" in missing
    assert "pricing.inputPer1MTokens" in missing
    assert "pricing.outputPer1MTokens" in missing


def test_has_complete_required_metadata_for_complete_override() -> None:
    options = LLMOptions(
        model_overrides={
            "custom-model": LLMModelMetadata(
                id="custom-model",
                context_window_tokens=32768,
                max_input_tokens=32768,
                max_output_tokens=4096,
                pricing=ModelPricingMetadata(input_per_1m_tokens=0.0, output_per_1m_tokens=0.0),
                capabilities=ModelCapabilityMetadata(
                    supports_temperature=True,
                    supports_reasoning_effort=False,
                    supports_structured_output=True,
                    supports_tools=True,
                    supports_json_mode=True,
                ),
            )
        }
    )

    assert has_complete_required_metadata(options, "openai", "custom-model") is True



