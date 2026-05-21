
from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Iterable

from .models import (
    LLMModelMetadata,
    LLMOptions,
    LLMRequest,
    ModelCapabilityMetadata,
    ModelPricingMetadata,
)

_REASONING_EFFORTS = ["minimal", "low", "medium", "high"]
_BARE_MODEL_LOOKUP_PROVIDER_PREFERENCE = ["openai", "claude", "copilot", "ollama"]
_KNOWN_PROVIDER_PREFIXES = {
    "openai": "openai",
    "azure": "openai",
    "text-completion-openai": "openai",
    "claude": "claude",
    "anthropic": "claude",
    "copilot": "copilot",
    "github_copilot": "copilot",
    "github": "copilot",
    "ollama": "ollama",
}


def _normalize_known_provider_type(provider_type: str | None) -> str | None:
    if not provider_type:
        return None
    return _KNOWN_PROVIDER_PREFIXES.get(provider_type.strip().lower())


def _normalize_provider_type(provider_type: str | None) -> str | None:
    if not provider_type:
        return None
    return _normalize_known_provider_type(provider_type) or provider_type.strip().lower()


def _metadata(
    model_id: str,
    provider_type: str,
    owned_by: str,
    context_window_tokens: int | None,
    max_input_tokens: int | None,
    max_output_tokens: int | None,
    input_price: float | None,
    output_price: float | None,
    *,
    temperature: bool | None,
    reasoning: bool | None,
    structured: bool | None,
    tools: bool | None,
    json_mode: bool | None,
    vision: bool | None = None,
    embeddings: bool | None = None,
) -> LLMModelMetadata:
    return LLMModelMetadata(
        id=model_id,
        display_name=model_id,
        provider_type=provider_type,
        owned_by=owned_by,
        context_window_tokens=context_window_tokens,
        max_input_tokens=max_input_tokens,
        max_output_tokens=max_output_tokens,
        pricing=ModelPricingMetadata(input_per_1m_tokens=input_price, output_per_1m_tokens=output_price),
        capabilities=ModelCapabilityMetadata(
            supports_temperature=temperature,
            supports_reasoning_effort=reasoning,
            supports_structured_output=structured,
            supports_tools=tools,
            supports_json_mode=json_mode,
            supports_vision=vision,
            supports_embeddings=embeddings,
        ),
    )


def _reasoning_metadata(
    model_id: str,
    provider_type: str,
    owned_by: str,
    context_window_tokens: int | None,
    max_input_tokens: int | None,
    max_output_tokens: int | None,
    input_price: float | None,
    output_price: float | None,
) -> LLMModelMetadata:
    metadata = _metadata(
        model_id,
        provider_type,
        owned_by,
        context_window_tokens,
        max_input_tokens,
        max_output_tokens,
        input_price,
        output_price,
        temperature=False,
        reasoning=True,
        structured=True,
        tools=True,
        json_mode=True,
    )
    metadata.capabilities.supported_reasoning_efforts = list(_REASONING_EFFORTS)
    metadata.capabilities.unsupported_request_parameters = ["temperature"]
    return metadata


BUILTIN_MODELS: dict[str, LLMModelMetadata] = {
    "text-embedding-3-large": _metadata(
        "text-embedding-3-large",
        "openai",
        "openai",
        None,
        None,
        None,
        0.13,
        0.0,
        temperature=False,
        reasoning=False,
        structured=False,
        tools=False,
        json_mode=False,
        embeddings=True,
    ),
    "text-embedding-3-small": _metadata(
        "text-embedding-3-small",
        "openai",
        "openai",
        None,
        None,
        None,
        0.02,
        0.0,
        temperature=False,
        reasoning=False,
        structured=False,
        tools=False,
        json_mode=False,
        embeddings=True,
    ),
    "text-embedding-ada-002": _metadata(
        "text-embedding-ada-002",
        "openai",
        "openai",
        None,
        None,
        None,
        0.10,
        0.0,
        temperature=False,
        reasoning=False,
        structured=False,
        tools=False,
        json_mode=False,
        embeddings=True,
    ),
    "gpt-4o": _metadata(
        "gpt-4o",
        "openai",
        "openai",
        128000,
        128000,
        16384,
        2.50,
        10.00,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
        vision=True,
    ),
    "gpt-4o-2024-11-20": _metadata(
        "gpt-4o-2024-11-20",
        "openai",
        "openai",
        128000,
        128000,
        16384,
        2.50,
        10.00,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
        vision=True,
    ),
    "gpt-4o-mini": _metadata(
        "gpt-4o-mini",
        "openai",
        "openai",
        128000,
        128000,
        16384,
        0.15,
        0.60,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
        vision=True,
    ),
    "gpt-4o-mini-2024-07-18": _metadata(
        "gpt-4o-mini-2024-07-18",
        "openai",
        "openai",
        128000,
        128000,
        16384,
        0.15,
        0.60,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
        vision=True,
    ),
    "gpt-4.1": _metadata(
        "gpt-4.1",
        "openai",
        "openai",
        1047576,
        1047576,
        32768,
        2.00,
        8.00,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
        vision=True,
    ),
    "gpt-4.1-mini": _metadata(
        "gpt-4.1-mini",
        "openai",
        "openai",
        1047576,
        1047576,
        32768,
        0.40,
        1.60,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
        vision=True,
    ),
    "gpt-4.1-nano": _metadata(
        "gpt-4.1-nano",
        "openai",
        "openai",
        1047576,
        1047576,
        32768,
        0.10,
        0.40,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
        vision=True,
    ),
    "gpt-4-turbo": _metadata(
        "gpt-4-turbo",
        "openai",
        "openai",
        128000,
        128000,
        4096,
        10.00,
        30.00,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
        vision=True,
    ),
    "gpt-3.5-turbo": _metadata(
        "gpt-3.5-turbo", "openai", "openai", 16385, 16385, 4096, 0.50, 1.50, temperature=True, reasoning=False, structured=True, tools=True, json_mode=True
    ),
    "o1": _reasoning_metadata("o1", "openai", "openai", 200000, 200000, 100000, 15.00, 60.00),
    "o1-mini": _reasoning_metadata("o1-mini", "openai", "openai", 128000, 128000, 65536, 1.10, 4.40),
    "o1-pro": _reasoning_metadata("o1-pro", "openai", "openai", 200000, 200000, 100000, 150.00, 600.00),
    "o3": _reasoning_metadata("o3", "openai", "openai", 200000, 200000, 100000, 2.00, 8.00),
    "o3-mini": _reasoning_metadata("o3-mini", "openai", "openai", 200000, 200000, 100000, 1.10, 4.40),
    "o4-mini": _reasoning_metadata("o4-mini", "openai", "openai", 200000, 200000, 100000, 1.10, 4.40),
    "claude-3-5-sonnet-20241022": _metadata(
        "claude-3-5-sonnet-20241022",
        "copilot",
        "anthropic",
        200000,
        200000,
        8192,
        3.00,
        15.00,
        temperature=True,
        reasoning=False,
        structured=False,
        tools=True,
        json_mode=True,
    ),
    "claude-3-5-haiku-20241022": _metadata(
        "claude-3-5-haiku-20241022",
        "copilot",
        "anthropic",
        200000,
        200000,
        8192,
        0.80,
        4.00,
        temperature=True,
        reasoning=False,
        structured=False,
        tools=True,
        json_mode=True,
    ),
    "claude-sonnet-4-20250514": _metadata(
        "claude-sonnet-4-20250514",
        "copilot",
        "anthropic",
        200000,
        200000,
        64000,
        3.00,
        15.00,
        temperature=True,
        reasoning=False,
        structured=False,
        tools=True,
        json_mode=True,
    ),
    "claude-haiku-4-20250514": _metadata(
        "claude-haiku-4-20250514",
        "copilot",
        "anthropic",
        200000,
        200000,
        8192,
        0.80,
        4.00,
        temperature=True,
        reasoning=False,
        structured=False,
        tools=True,
        json_mode=True,
    ),
    "claude-3-7-sonnet-20250219": _metadata(
        "claude-3-7-sonnet-20250219",
        "copilot",
        "anthropic",
        200000,
        200000,
        64000,
        3.00,
        15.00,
        temperature=True,
        reasoning=False,
        structured=False,
        tools=True,
        json_mode=True,
    ),
    "mistral-large-latest": _metadata(
        "mistral-large-latest",
        "copilot",
        "mistral",
        128000,
        128000,
        8192,
        2.00,
        6.00,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
    ),
    "mistral-small-latest": _metadata(
        "mistral-small-latest",
        "copilot",
        "mistral",
        128000,
        128000,
        8192,
        0.20,
        0.60,
        temperature=True,
        reasoning=False,
        structured=True,
        tools=True,
        json_mode=True,
    ),
    "mistral-embed": _metadata(
        "mistral-embed",
        "copilot",
        "mistral",
        None,
        None,
        None,
        0.10,
        0.0,
        temperature=False,
        reasoning=False,
        structured=False,
        tools=False,
        json_mode=False,
        embeddings=True,
    ),
    "deepseek-chat": _metadata(
        "deepseek-chat", "copilot", "deepseek", 64000, 64000, 8192, 0.14, 0.28, temperature=True, reasoning=False, structured=True, tools=True, json_mode=True
    ),
    "deepseek-reasoner": _reasoning_metadata("deepseek-reasoner", "copilot", "deepseek", 64000, 64000, 8192, 0.55, 2.19),
    "gpt-5.4": _reasoning_metadata("gpt-5.4", "openai", "openai", 400000, 400000, 128000, 75.00, 150.00),
}

BUILTIN_ALIASES: dict[str, str] = {
    "gpt4o": "gpt-4o",
    "gpt4o-mini": "gpt-4o-mini",
    "gpt-4-o": "gpt-4o",
    "gpt-4-o-mini": "gpt-4o-mini",
    "embedding-3-large": "text-embedding-3-large",
    "embedding-3-small": "text-embedding-3-small",
    "ada-002": "text-embedding-ada-002",
    "text-embedding-ada": "text-embedding-ada-002",
    "claude-3.5-sonnet": "claude-3-5-sonnet-20241022",
    "claude-3.5-haiku": "claude-3-5-haiku-20241022",
    "claude-3.7-sonnet": "claude-3-7-sonnet-20250219",
    "claude-4-sonnet": "claude-sonnet-4-20250514",
    "claude-4-haiku": "claude-haiku-4-20250514",
    "openai/gpt-4o": "gpt-4o",
    "openai/gpt-4o-mini": "gpt-4o-mini",
    "openai/o4-mini": "o4-mini",
    "openai/o3-mini": "o3-mini",
    "anthropic/claude-sonnet-4": "claude-sonnet-4-20250514",
    "anthropic/claude-haiku-4": "claude-haiku-4-20250514",
}

_STATIC_BUILTIN_MODELS = BUILTIN_MODELS
_STATIC_BUILTIN_ALIASES = BUILTIN_ALIASES


def _repo_root() -> Path | None:
    current = Path(__file__).resolve()
    for parent in current.parents:
        if (parent / "src" / "GnOuGo.AI.Core" / "Telemetry" / "model-metadata.json").is_file():
            return parent
    return None


def _shared_catalog_path() -> Path | None:
    root = _repo_root()
    if root is None:
        return None
    candidate = root / "src" / "GnOuGo.AI.Core" / "Telemetry" / "model-metadata.json"
    return candidate if candidate.is_file() else None


def _load_shared_builtin_catalog() -> tuple[dict[str, LLMModelMetadata], dict[str, str]] | None:
    path = _shared_catalog_path()
    if path is None:
        return None
    try:
        root = json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception:
        return None
    if not isinstance(root, dict):
        return None
    models: dict[str, LLMModelMetadata] = {}
    aliases: dict[str, str] = {}
    raw_models = root.get("models") or {}
    raw_aliases = root.get("aliases") or {}
    if not isinstance(raw_models, dict) or not isinstance(raw_aliases, dict):
        return None
    for model_id, payload in raw_models.items():
        if not isinstance(model_id, str) or not isinstance(payload, dict):
            continue
        data = dict(payload)
        split = _split_provider_qualified_key(model_id)
        if split is not None:
            data.setdefault("id", split[1])
            data.setdefault("providerType", split[0])
        else:
            data.setdefault("id", model_id)
        provider_type = _normalize_provider_type(data.get("providerType"))
        if provider_type is not None:
            data["providerType"] = provider_type
        try:
            models[model_id] = LLMModelMetadata.model_validate(data)
        except Exception:
            continue
    for alias, canonical in raw_aliases.items():
        if isinstance(alias, str) and isinstance(canonical, str):
            aliases[alias] = canonical
    return models, aliases


def strip_vendor_prefix(model: str) -> str:
    if not model:
        return model
    split = _split_provider_qualified_key(model)
    return split[1] if split is not None else model


def _split_provider_qualified_key(key: str) -> tuple[str, str] | None:
    if not key or "/" not in key:
        return None
    provider, model = key.split("/", 1)
    normalized_provider = _normalize_known_provider_type(provider)
    if not normalized_provider or not model:
        return None
    return normalized_provider, model


_shared_catalog = _load_shared_builtin_catalog()
if _shared_catalog is not None:
    BUILTIN_MODELS, BUILTIN_ALIASES = _shared_catalog
else:
    BUILTIN_MODELS, BUILTIN_ALIASES = _STATIC_BUILTIN_MODELS, _STATIC_BUILTIN_ALIASES


def _key_variants(provider_type: str | None, key: str) -> list[str]:
    if not key:
        return []
    provider_type = _normalize_provider_type(provider_type)
    if _split_provider_qualified_key(key) is not None:
        return [key]
    variants: list[str] = []
    if provider_type:
        variants.append(f"{provider_type}/{key}")
    variants.append(key)
    return variants


def _candidate_keys(provider_type: str | None, model: str, clean_model: str, canonical: str | None) -> list[str]:
    keys: list[str] = []
    seen: set[str] = set()
    for key in [model, clean_model, canonical, strip_vendor_prefix(canonical or "")]:
        if not key:
            continue
        for candidate in _key_variants(provider_type, key):
            lowered = candidate.lower()
            if lowered not in seen:
                keys.append(candidate)
                seen.add(lowered)
    return keys


def clone_metadata(metadata: LLMModelMetadata) -> LLMModelMetadata:
    return metadata.model_copy(deep=True)


def _try_get_builtin_model(model_name: str) -> LLMModelMetadata | None:
    if model_name in BUILTIN_MODELS:
        return clone_metadata(BUILTIN_MODELS[model_name])
    return None


def _try_get_builtin_core(model_name: str) -> LLMModelMetadata | None:
    metadata = _try_get_builtin_model(model_name)
    if metadata is not None:
        return metadata
    alias = BUILTIN_ALIASES.get(model_name)
    if alias and alias in BUILTIN_MODELS:
        return clone_metadata(BUILTIN_MODELS[alias])
    if _split_provider_qualified_key(model_name) is None:
        for provider in _BARE_MODEL_LOOKUP_PROVIDER_PREFERENCE:
            metadata = _try_get_builtin_model(f"{provider}/{model_name}")
            if metadata is not None:
                return metadata
    return None


def try_get_builtin(model_name: str, provider_type: str | None = None) -> LLMModelMetadata | None:
    keys: list[str] = []
    seen: set[str] = set()
    provider_type = _normalize_provider_type(provider_type)
    for key in _key_variants(provider_type, model_name) + _key_variants(provider_type, strip_vendor_prefix(model_name)):
        lowered = key.lower()
        if key and lowered not in seen:
            keys.append(key)
            seen.add(lowered)
    for key in list(keys):
        alias = BUILTIN_ALIASES.get(key)
        if alias:
            for candidate in _key_variants(provider_type, alias):
                lowered = candidate.lower()
                if lowered not in seen:
                    keys.append(candidate)
                    seen.add(lowered)
    for key in keys:
        if key and (metadata := _try_get_builtin_core(key)) is not None:
            return metadata
    return None


def get_missing_required_metadata_fields(metadata: LLMModelMetadata) -> list[str]:
    """Return metadata fields required by the .NET /llm configuration flow."""

    missing: list[str] = []
    if metadata.context_window_tokens is None or metadata.context_window_tokens <= 0:
        missing.append("contextWindowTokens")
    if metadata.max_input_tokens is None or metadata.max_input_tokens <= 0:
        missing.append("maxInputTokens")
    if metadata.max_output_tokens is None or metadata.max_output_tokens <= 0:
        missing.append("maxOutputTokens")

    if metadata.pricing is None:
        missing.extend(["pricing.inputPer1MTokens", "pricing.outputPer1MTokens"])
    else:
        if metadata.pricing.input_per_1m_tokens is None or metadata.pricing.input_per_1m_tokens < 0:
            missing.append("pricing.inputPer1MTokens")
        if metadata.pricing.output_per_1m_tokens is None or metadata.pricing.output_per_1m_tokens < 0:
            missing.append("pricing.outputPer1MTokens")

    capabilities = metadata.capabilities
    if capabilities.supports_temperature is None:
        missing.append("capabilities.supportsTemperature")
    if capabilities.supports_reasoning_effort is None:
        missing.append("capabilities.supportsReasoningEffort")
    if capabilities.supports_structured_output is None:
        missing.append("capabilities.supportsStructuredOutput")
    if capabilities.supports_tools is None:
        missing.append("capabilities.supportsTools")
    if capabilities.supports_json_mode is None:
        missing.append("capabilities.supportsJsonMode")

    return missing


def has_complete_required_metadata(options: LLMOptions | None, provider_type: str | None, model: str) -> bool:
    metadata = LLMModelMetadataResolver(options).resolve(provider_type, model)
    return not get_missing_required_metadata_fields(metadata)


def _merge_into(target: LLMModelMetadata, source: LLMModelMetadata, fallback_id: str | None = None) -> None:
    if source.id:
        target.id = source.id
    elif not target.id and fallback_id:
        target.id = fallback_id
    if source.provider_type:
        target.provider_type = source.provider_type
    if source.display_name:
        target.display_name = source.display_name
    if source.owned_by:
        target.owned_by = source.owned_by
    target.context_window_tokens = source.context_window_tokens or target.context_window_tokens
    target.max_input_tokens = source.max_input_tokens or target.max_input_tokens
    target.max_output_tokens = source.max_output_tokens or target.max_output_tokens

    if source.pricing is not None:
        if target.pricing is None:
            target.pricing = ModelPricingMetadata()
        if source.pricing.currency:
            target.pricing.currency = source.pricing.currency
        target.pricing.input_per_1m_tokens = (
            source.pricing.input_per_1m_tokens if source.pricing.input_per_1m_tokens is not None else target.pricing.input_per_1m_tokens
        )
        target.pricing.output_per_1m_tokens = (
            source.pricing.output_per_1m_tokens if source.pricing.output_per_1m_tokens is not None else target.pricing.output_per_1m_tokens
        )
        target.pricing.cached_input_per_1m_tokens = (
            source.pricing.cached_input_per_1m_tokens if source.pricing.cached_input_per_1m_tokens is not None else target.pricing.cached_input_per_1m_tokens
        )
        target.pricing.reasoning_output_per_1m_tokens = (
            source.pricing.reasoning_output_per_1m_tokens
            if source.pricing.reasoning_output_per_1m_tokens is not None
            else target.pricing.reasoning_output_per_1m_tokens
        )

    sc = source.capabilities
    tc = target.capabilities
    tc.supports_temperature = sc.supports_temperature if sc.supports_temperature is not None else tc.supports_temperature
    tc.supports_reasoning_effort = sc.supports_reasoning_effort if sc.supports_reasoning_effort is not None else tc.supports_reasoning_effort
    tc.supports_structured_output = sc.supports_structured_output if sc.supports_structured_output is not None else tc.supports_structured_output
    tc.supports_tools = sc.supports_tools if sc.supports_tools is not None else tc.supports_tools
    tc.supports_json_mode = sc.supports_json_mode if sc.supports_json_mode is not None else tc.supports_json_mode
    tc.supports_vision = sc.supports_vision if sc.supports_vision is not None else tc.supports_vision
    tc.supports_audio = sc.supports_audio if sc.supports_audio is not None else tc.supports_audio
    tc.supports_embeddings = sc.supports_embeddings if sc.supports_embeddings is not None else tc.supports_embeddings
    if sc.supported_reasoning_efforts is not None:
        tc.supported_reasoning_efforts = list(sc.supported_reasoning_efforts)
    if sc.unsupported_request_parameters is not None:
        tc.unsupported_request_parameters = list(sc.unsupported_request_parameters)

    target.aliases.update(source.aliases)
    target.extra.update(source.extra)


def _resolve_file_path(path: str) -> Path | None:
    candidate = Path(path)
    candidates = [candidate] if candidate.is_absolute() else [Path.cwd() / path, Path(__file__).resolve().parent / path]
    for item in candidates:
        if item.exists() and item.is_file():
            return item
    return None


def _load_metadata_files(paths: Iterable[str]) -> tuple[dict[str, LLMModelMetadata], dict[str, str]]:
    models: dict[str, LLMModelMetadata] = {}
    aliases: dict[str, str] = {}
    for path in paths:
        resolved = _resolve_file_path(path)
        if resolved is None:
            continue
        try:
            root = json.loads(resolved.read_text(encoding="utf-8"))
        except Exception:
            continue
        if not isinstance(root, dict):
            continue
        for alias, canonical in (root.get("aliases") or {}).items():
            if isinstance(alias, str) and isinstance(canonical, str):
                aliases[alias] = canonical
        raw_models = root.get("models") or {}
        if isinstance(raw_models, dict):
            for model_id, payload in raw_models.items():
                if not isinstance(payload, dict):
                    continue
                data = dict(payload)
                split = _split_provider_qualified_key(model_id)
                if split is not None:
                    data.setdefault("id", split[1])
                    data.setdefault("providerType", split[0])
                else:
                    data.setdefault("id", model_id)
                provider_type = _normalize_provider_type(data.get("providerType"))
                if provider_type is not None:
                    data["providerType"] = provider_type
                try:
                    metadata = LLMModelMetadata.model_validate(data)
                except Exception:
                    continue
                models[model_id] = metadata
                for alias, canonical in metadata.aliases.items():
                    aliases[alias] = canonical
    for alias, canonical in list(aliases.items()):
        if canonical in models:
            models[alias] = clone_metadata(models[canonical])
    return models, aliases


class LLMModelMetadataResolver:
    """Resolve model metadata from built-ins, external files and inline overrides."""

    def __init__(self, options: LLMOptions | None = None) -> None:
        self.options = options or LLMOptions()
        self._file_models, self._file_aliases = _load_metadata_files(self.options.model_metadata_files)
        self._inline_overrides = self.options.model_overrides

    def resolve(self, provider_type: str | None, model: str) -> LLMModelMetadata:
        provider_type = _normalize_provider_type(provider_type)
        clean_model = strip_vendor_prefix(model)
        canonical = self._resolve_alias(provider_type, model) or self._resolve_alias(provider_type, clean_model)
        candidates = _candidate_keys(provider_type, model, clean_model, canonical)

        metadata = next((candidate for key in candidates if (candidate := _try_get_builtin_core(key)) is not None), None)
        if metadata is None:
            metadata = LLMModelMetadata(id=canonical or clean_model, display_name=canonical or clean_model)

        for key in reversed(candidates):
            if key in self._file_models:
                _merge_into(metadata, self._file_models[key], key)
            if key in self._inline_overrides:
                _merge_into(metadata, self._inline_overrides[key], key)

        if not metadata.id:
            metadata.id = canonical or clean_model
        if not metadata.display_name:
            metadata.display_name = metadata.id
        if not metadata.provider_type and provider_type:
            metadata.provider_type = provider_type
        _apply_heuristic_defaults(metadata, provider_type, clean_model)
        return metadata

    def _resolve_alias(self, provider_type: str | None, key: str) -> str | None:
        provider_type = _normalize_provider_type(provider_type)
        for candidate in _key_variants(provider_type, key):
            if candidate in self._file_aliases:
                return self._file_aliases[candidate]
            if candidate in BUILTIN_ALIASES:
                return BUILTIN_ALIASES[candidate]
        return None

    def list_configured_metadata(self, provider_type: str | None = None) -> list[LLMModelMetadata]:
        provider_type = _normalize_provider_type(provider_type)
        merged: dict[str, LLMModelMetadata] = {}
        for mapping in (self._file_models, self._inline_overrides):
            for key, value in mapping.items():
                metadata = clone_metadata(value)
                split = _split_provider_qualified_key(key)
                if not metadata.id:
                    metadata.id = split[1] if split is not None else key
                if metadata.provider_type is None and split is not None:
                    metadata.provider_type = split[0]
                metadata.provider_type = _normalize_provider_type(metadata.provider_type)
                if provider_type and metadata.provider_type and metadata.provider_type.lower() != provider_type.lower():
                    continue
                merged_key = metadata.id if provider_type or not metadata.provider_type else f"{metadata.provider_type}/{metadata.id}"
                merged[merged_key] = metadata
        return sorted(merged.values(), key=lambda m: (m.display_name or m.id).lower())


def _apply_heuristic_defaults(metadata: LLMModelMetadata, provider_type: str | None, model: str) -> None:
    capabilities = metadata.capabilities
    provider = (metadata.provider_type or provider_type or "").lower()
    lowered = model.lower()
    is_reasoning_model = lowered.startswith(("o1", "o3", "o4", "gpt-5")) or "reasoner" in lowered
    is_embedding = "embedding" in lowered or bool(capabilities.supports_embeddings)

    if provider in {"openai", "copilot"}:
        capabilities.supports_temperature = capabilities.supports_temperature if capabilities.supports_temperature is not None else not is_reasoning_model
        capabilities.supports_reasoning_effort = (
            capabilities.supports_reasoning_effort if capabilities.supports_reasoning_effort is not None else is_reasoning_model
        )
        capabilities.supports_structured_output = (
            capabilities.supports_structured_output if capabilities.supports_structured_output is not None else not is_embedding
        )
        capabilities.supports_tools = capabilities.supports_tools if capabilities.supports_tools is not None else not is_embedding
        capabilities.supports_json_mode = capabilities.supports_json_mode if capabilities.supports_json_mode is not None else not is_embedding
    elif provider == "ollama":
        supports_think = any(marker in lowered for marker in ("deepseek-r1", "qwen3", "qwq"))
        capabilities.supports_temperature = capabilities.supports_temperature if capabilities.supports_temperature is not None else True
        capabilities.supports_reasoning_effort = (
            capabilities.supports_reasoning_effort if capabilities.supports_reasoning_effort is not None else supports_think
        )
        capabilities.supports_structured_output = capabilities.supports_structured_output if capabilities.supports_structured_output is not None else True
        capabilities.supports_tools = capabilities.supports_tools if capabilities.supports_tools is not None else True
        capabilities.supports_json_mode = capabilities.supports_json_mode if capabilities.supports_json_mode is not None else True
    elif not provider:
        if is_reasoning_model:
            capabilities.supports_temperature = capabilities.supports_temperature if capabilities.supports_temperature is not None else False
            capabilities.supports_reasoning_effort = capabilities.supports_reasoning_effort if capabilities.supports_reasoning_effort is not None else True
    else:
        capabilities.supports_temperature = capabilities.supports_temperature if capabilities.supports_temperature is not None else True
        capabilities.supports_reasoning_effort = capabilities.supports_reasoning_effort if capabilities.supports_reasoning_effort is not None else False

    if capabilities.supports_temperature is False:
        _add_unsupported(capabilities, "temperature")
    if capabilities.supports_reasoning_effort is False:
        _add_unsupported(capabilities, "reasoning_effort")


def _add_unsupported(capabilities: ModelCapabilityMetadata, name: str) -> None:
    capabilities.unsupported_request_parameters = capabilities.unsupported_request_parameters or []
    if name.lower() not in {item.lower() for item in capabilities.unsupported_request_parameters}:
        capabilities.unsupported_request_parameters.append(name)


def sanitize_llm_request(request: LLMRequest, metadata: LLMModelMetadata) -> LLMRequest:
    sanitized = request.model_copy(deep=True)
    capabilities = metadata.capabilities
    unsupported = {item.lower() for item in (capabilities.unsupported_request_parameters or [])}

    if capabilities.supports_temperature is False or "temperature" in unsupported:
        sanitized.temperature = None
    if capabilities.supports_reasoning_effort is False or "reasoning" in unsupported or "reasoning_effort" in unsupported:
        sanitized.reasoning = None
    if capabilities.supports_structured_output is False or "structured_output" in unsupported or "response_format" in unsupported:
        sanitized.structured_output_schema = None
        sanitized.structured_output_strict = None
    if capabilities.supports_tools is False or "tools" in unsupported:
        sanitized.tools = None
    return sanitized


def try_get_pricing(model_name: str, options: LLMOptions | None = None, provider_type: str | None = None) -> ModelPricingMetadata | None:
    metadata = LLMModelMetadataResolver(options).resolve(provider_type, model_name) if options is not None or provider_type else try_get_builtin(model_name)
    return copy.deepcopy(metadata.pricing) if metadata and metadata.pricing else None


def estimate_cost(
    model_name: str,
    input_tokens: int | None = 0,
    output_tokens: int | None = 0,
    options: LLMOptions | None = None,
    provider_type: str | None = None,
) -> float | None:
    pricing = try_get_pricing(model_name, options=options, provider_type=provider_type)
    if pricing is None:
        return None
    return ((input_tokens or 0) / 1_000_000.0) * (pricing.input_per_1m_tokens or 0.0) + ((output_tokens or 0) / 1_000_000.0) * (
        pricing.output_per_1m_tokens or 0.0
    )



