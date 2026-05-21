#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────
# update-model-metadata.sh
# Updates model pricing inside model-metadata.json and regenerates the C#
# model metadata catalog. model-metadata.json is the single source for
# builtin limits, capabilities and pricing.
# ─────────────────────────────────────────────────────────────────────
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
JSON_PATH="${SCRIPT_DIR}/../src/GnOuGo.AI.Core/Telemetry/model-metadata.json"
OUTPUT_PATH="${SCRIPT_DIR}/../src/GnOuGo.AI.Core/ModelMetadataCatalog.Generated.cs"
DOWNLOAD_LATEST=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --download-latest) DOWNLOAD_LATEST=true; shift ;;
    --json-path)       JSON_PATH="$2"; shift 2 ;;
    --output-path)     OUTPUT_PATH="$2"; shift 2 ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done
echo "Metadata JSON path : $JSON_PATH"
echo "Generated output   : $OUTPUT_PATH"
if ! command -v python3 &>/dev/null; then
  if [[ -f "$OUTPUT_PATH" ]]; then
    echo "WARNING: python3 not found but generated file already exists at $OUTPUT_PATH — skipping regeneration."
    exit 0
  fi
  echo "ERROR: python3 is required to generate the C# metadata catalog."
  exit 1
fi
if [[ "$DOWNLOAD_LATEST" == true ]]; then
  echo ""
  echo "Downloading latest pricing from LiteLLM..."
  LITELLM_URL="https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"
  TEMP_FILE="$(mktemp)"
  if command -v curl &>/dev/null; then
    curl -sS --max-time 30 -o "$TEMP_FILE" "$LITELLM_URL" || rm -f "$TEMP_FILE"
  elif command -v wget &>/dev/null; then
    wget -q --timeout=30 -O "$TEMP_FILE" "$LITELLM_URL" || rm -f "$TEMP_FILE"
  else
    echo "WARNING: Neither curl nor wget found. Skipping download."
    rm -f "$TEMP_FILE"
  fi
  if [[ -f "$TEMP_FILE" && -s "$TEMP_FILE" ]]; then
    python3 - "$TEMP_FILE" "$JSON_PATH" <<'PYEOF'
import json
import sys
from datetime import date
from pathlib import Path

PROVIDER_MAP = {
    "openai": "openai",
    "azure": "openai",
    "text-completion-openai": "openai",
    "anthropic": "claude",
    "claude": "claude",
    "github_copilot": "copilot",
    "github": "copilot",
    "copilot": "copilot",
    "ollama": "ollama",
}

def normalize_provider(provider):
    if provider is None:
        return None
    provider = str(provider).strip()
    if not provider:
        return None
    return PROVIDER_MAP.get(provider.lower(), provider.lower())

def split_provider_qualified_key(key):
    if not key or "/" not in key:
        return None
    provider, model_id = key.split("/", 1)
    provider = normalize_provider(provider)
    if not provider or not model_id:
        return None
    return provider, model_id

def provider_qualified_key(key, model):
    split = split_provider_qualified_key(key)
    if split:
        return f"{split[0]}/{split[1]}"
    provider = normalize_provider((model or {}).get("providerType"))
    if not provider:
        return key
    model_id = (model or {}).get("id") or key
    return f"{provider}/{model_id}"

def catalog_key_from_litellm(provider, model_name):
    provider = normalize_provider(provider)
    if provider not in {"openai", "claude", "copilot", "ollama"}:
        return None
    trimmed = model_name
    for prefix in {provider, str(provider), "anthropic", "github_copilot", "copilot", "openai", "ollama"}:
        prefix = f"{prefix}/"
        if trimmed.lower().startswith(prefix.lower()):
            trimmed = trimmed[len(prefix):]
            break
    return f"{provider}/{trimmed}"

def supported_reasoning_efforts(data):
    efforts = []
    if data.get("supports_minimal_reasoning_effort"):
        efforts.extend(["minimal", "low"])
    if data.get("supports_reasoning"):
        efforts.extend(["medium", "high"])
    if data.get("supports_max_reasoning_effort") or data.get("supports_xhigh_reasoning_effort"):
        efforts.append("max")
    return list(dict.fromkeys(efforts))

litellm_path = Path(sys.argv[1])
metadata_path = Path(sys.argv[2])
with litellm_path.open("r", encoding="utf-8-sig") as f:
    litellm = json.load(f)
with metadata_path.open("r", encoding="utf-8-sig") as f:
    local = json.load(f)
models = local.setdefault("models", {})
aliases = local.setdefault("aliases", {})
migrated_models = {}
canonical_key_map = {}
for key, model in list(models.items()):
    new_key = provider_qualified_key(key, model)
    migrated_models[new_key] = model
    canonical_key_map[key] = new_key
local["models"] = models = migrated_models
migrated_aliases = {}
for alias, canonical in list(aliases.items()):
    alias_split = split_provider_qualified_key(alias)
    alias_key = f"{alias_split[0]}/{alias_split[1]}" if alias_split else alias
    migrated_aliases[alias_key] = canonical_key_map.get(canonical, canonical)
local["aliases"] = aliases = migrated_aliases
added = updated = 0
for model_name, data in litellm.items():
    if not isinstance(data, dict):
        continue
    catalog_key = catalog_key_from_litellm(data.get("litellm_provider"), model_name)
    if not catalog_key:
        continue
    provider, clean_name = split_provider_qualified_key(catalog_key)
    input_cost = data.get("input_cost_per_token")
    output_cost = data.get("output_cost_per_token")
    input_per_1m = round(input_cost * 1_000_000, 4) if input_cost is not None else None
    output_per_1m = round(output_cost * 1_000_000, 4) if output_cost is not None else None
    model = models.setdefault(catalog_key, {"id": clean_name, "providerType": provider})
    if provider == "claude":
        model.setdefault("ownedBy", "anthropic")
    elif provider in {"openai", "ollama"}:
        model.setdefault("ownedBy", provider)
    if "id" not in model:
        model["id"] = clean_name
    if model.get("providerType") != provider:
        model["providerType"] = provider
    if catalog_key not in migrated_models:
        added += 1
    context = data.get("max_input_tokens") or data.get("max_tokens")
    if context is not None:
        model["contextWindowTokens"] = int(context)
        model["maxInputTokens"] = int(context)
    max_output = data.get("max_output_tokens") or data.get("max_tokens")
    if max_output is not None:
        model["maxOutputTokens"] = int(max_output)
    if input_per_1m is not None or output_per_1m is not None or provider == "ollama":
        pricing = model.setdefault("pricing", {})
        if pricing.get("inputPer1MTokens") != input_per_1m or pricing.get("outputPer1MTokens") != output_per_1m:
            updated += 1
        pricing["currency"] = "USD"
        if input_per_1m is not None:
            pricing["inputPer1MTokens"] = input_per_1m
        elif provider == "ollama":
            pricing.setdefault("inputPer1MTokens", 0)
        if output_per_1m is not None:
            pricing["outputPer1MTokens"] = output_per_1m
        elif provider == "ollama":
            pricing.setdefault("outputPer1MTokens", 0)
        if data.get("cache_read_input_token_cost") is not None:
            pricing["cachedInputPer1MTokens"] = round(data["cache_read_input_token_cost"] * 1_000_000, 4)
    capabilities = model.setdefault("capabilities", {})
    if data.get("mode") == "embedding":
        capabilities.update({
            "supportsEmbeddings": True,
            "supportsTemperature": False,
            "supportsStructuredOutput": False,
            "supportsTools": False,
            "supportsJsonMode": False,
        })
    if data.get("supports_function_calling") is not None:
        capabilities["supportsTools"] = bool(data["supports_function_calling"])
    if data.get("supports_response_schema") is not None:
        capabilities["supportsStructuredOutput"] = bool(data["supports_response_schema"])
        capabilities["supportsJsonMode"] = bool(data["supports_response_schema"])
    if data.get("supports_vision") is not None:
        capabilities["supportsVision"] = bool(data["supports_vision"])
    if data.get("supports_audio_input") is not None or data.get("supports_audio_output") is not None:
        capabilities["supportsAudio"] = bool(data.get("supports_audio_input") or data.get("supports_audio_output"))
    reasoning_efforts = supported_reasoning_efforts(data)
    if reasoning_efforts:
        capabilities["supportsReasoningEffort"] = True
        capabilities["supportedReasoningEfforts"] = reasoning_efforts
    elif provider == "claude":
        capabilities.setdefault("supportsReasoningEffort", False)
local["_updated"] = str(date.today())
with metadata_path.open("w", encoding="utf-8") as f:
    json.dump(local, f, indent=2, ensure_ascii=False)
    f.write("\n")
print(f"Merged pricing into metadata: {added} added, {updated} updated")
PYEOF
    rm -f "$TEMP_FILE"
  else
    echo "WARNING: Could not download LiteLLM pricing. Continuing with existing local metadata."
  fi
fi
python3 - "$JSON_PATH" "$OUTPUT_PATH" <<'PYEOF'
import json
import sys
from datetime import datetime
from pathlib import Path
json_path = Path(sys.argv[1])
output_path = Path(sys.argv[2])
with json_path.open("r", encoding="utf-8-sig") as f:
    data = json.load(f)
models = data.get("models", {})
aliases = data.get("aliases", {})
PROVIDER_MAP = {
    "openai": "openai",
    "azure": "openai",
    "text-completion-openai": "openai",
    "anthropic": "claude",
    "claude": "claude",
    "github_copilot": "copilot",
    "github": "copilot",
    "copilot": "copilot",
    "ollama": "ollama",
}
def normalize_provider(provider):
    if provider is None:
        return None
    provider = str(provider).strip()
    if not provider:
        return None
    return PROVIDER_MAP.get(provider.lower(), provider.lower())
def cs_string(value):
    if value is None or str(value).strip() == "":
        return "null"
    return json.dumps(str(value))
def cs_int(value):
    return "null" if value is None else str(int(value))
def cs_decimal(value):
    if value is None:
        return "null"
    return f"{value}m"
def cs_bool(value):
    if value is None:
        return "null"
    return "true" if bool(value) else "false"
def cs_string_list(value):
    if not value:
        return "null"
    items = [cs_string(item) for item in value if str(item).strip()]
    return "[" + ", ".join(items) + "]" if items else "null"
def split_provider_qualified_key(key):
    if not key or "/" not in key:
        return None
    provider, model_id = key.split("/", 1)
    provider = normalize_provider(provider)
    if not provider or not model_id:
        return None
    return provider, model_id
def append_if_not_null(lines, name, literal):
    if literal != "null":
        lines.append(f"                {name} = {literal},")
lines = []
lines.append("// <auto-generated/>")
lines.append(f"// Generated by scripts/update-model-metadata.sh on {datetime.now():%Y-%m-%d %H:%M:%S}")
lines.append("// Source: src/GnOuGo.AI.Core/Telemetry/model-metadata.json")
lines.append("// DO NOT EDIT MANUALLY - run the script to regenerate.")
lines.append("")
lines.append("using System.Collections.Frozen;")
lines.append("")
lines.append("namespace GnOuGo.AI.Core;")
lines.append("")
lines.append("public static partial class ModelMetadataCatalog")
lines.append("{")
lines.append("    private static readonly FrozenDictionary<string, LLMModelMetadata> BuiltinModels = new Dictionary<string, LLMModelMetadata>(StringComparer.OrdinalIgnoreCase)")
lines.append("    {")
model_count = 0
for name, model in models.items():
    if name.startswith("_"):
        continue
    pricing = model.get("pricing") or None
    cap = model.get("capabilities") or None
    qualified_key = split_provider_qualified_key(name)
    model_id = model.get("id") or (qualified_key[1] if qualified_key else name)
    display_name = model.get("displayName") or model_id
    provider_type = normalize_provider(model.get("providerType")) or (qualified_key[0] if qualified_key else None)
    lines.append(f"        [{cs_string(name)}] = new LLMModelMetadata")
    lines.append("        {")
    lines.append(f"            Id = {cs_string(model_id)},")
    lines.append(f"            DisplayName = {cs_string(display_name)},")
    append_if_not_null(lines, "ProviderType", cs_string(provider_type))
    append_if_not_null(lines, "OwnedBy", cs_string(model.get("ownedBy")))
    append_if_not_null(lines, "ContextWindowTokens", cs_int(model.get("contextWindowTokens")))
    append_if_not_null(lines, "MaxInputTokens", cs_int(model.get("maxInputTokens")))
    append_if_not_null(lines, "MaxOutputTokens", cs_int(model.get("maxOutputTokens")))
    if pricing:
        lines.append("            Pricing = new ModelPricingMetadata")
        lines.append("            {")
        lines.append(f"                Currency = {cs_string(pricing.get('currency') or 'USD')},")
        append_if_not_null(lines, "InputPer1MTokens", cs_decimal(pricing.get("inputPer1MTokens")))
        append_if_not_null(lines, "OutputPer1MTokens", cs_decimal(pricing.get("outputPer1MTokens")))
        append_if_not_null(lines, "CachedInputPer1MTokens", cs_decimal(pricing.get("cachedInputPer1MTokens")))
        append_if_not_null(lines, "ReasoningOutputPer1MTokens", cs_decimal(pricing.get("reasoningOutputPer1MTokens")))
        lines.append("            },")
    if cap:
        lines.append("            Capabilities = new ModelCapabilityMetadata")
        lines.append("            {")
        append_if_not_null(lines, "SupportsTemperature", cs_bool(cap.get("supportsTemperature")))
        append_if_not_null(lines, "SupportsReasoningEffort", cs_bool(cap.get("supportsReasoningEffort")))
        append_if_not_null(lines, "SupportsStructuredOutput", cs_bool(cap.get("supportsStructuredOutput")))
        append_if_not_null(lines, "SupportsTools", cs_bool(cap.get("supportsTools")))
        append_if_not_null(lines, "SupportsJsonMode", cs_bool(cap.get("supportsJsonMode")))
        append_if_not_null(lines, "SupportsVision", cs_bool(cap.get("supportsVision")))
        append_if_not_null(lines, "SupportsAudio", cs_bool(cap.get("supportsAudio")))
        append_if_not_null(lines, "SupportsEmbeddings", cs_bool(cap.get("supportsEmbeddings")))
        append_if_not_null(lines, "SupportedReasoningEfforts", cs_string_list(cap.get("supportedReasoningEfforts")))
        append_if_not_null(lines, "UnsupportedRequestParameters", cs_string_list(cap.get("unsupportedRequestParameters")))
        lines.append("            },")
    lines.append("        },")
    model_count += 1
lines.append("    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);")
lines.append("")
lines.append("    private static readonly FrozenDictionary<string, string> BuiltinAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)")
lines.append("    {")
alias_count = 0
for alias, canonical in aliases.items():
    lines.append(f"        [{cs_string(alias)}] = {cs_string(canonical)},")
    alias_count += 1
lines.append("    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);")
lines.append("}")
output_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"Generated {output_path} ({model_count} models, {alias_count} aliases)")
PYEOF
