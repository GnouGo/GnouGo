#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────
# update-model-pricing.sh
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
litellm_path = Path(sys.argv[1])
metadata_path = Path(sys.argv[2])
with litellm_path.open("r", encoding="utf-8-sig") as f:
    litellm = json.load(f)
with metadata_path.open("r", encoding="utf-8-sig") as f:
    local = json.load(f)
models = local.setdefault("models", {})
valid_providers = {"openai", "anthropic", "mistralai", "deepseek", "cohere", "text-completion-openai"}
added = updated = 0
for model_name, data in litellm.items():
    if not isinstance(data, dict):
        continue
    input_cost = data.get("input_cost_per_token")
    output_cost = data.get("output_cost_per_token")
    if not input_cost and not output_cost:
        continue
    provider = str(data.get("litellm_provider") or "")
    if not any(vp in provider for vp in valid_providers):
        continue
    clean_name = model_name.split("/", 1)[-1]
    input_per_1m = round((input_cost or 0) * 1_000_000, 4)
    output_per_1m = round((output_cost or 0) * 1_000_000, 4)
    model = models.setdefault(clean_name, {"providerType": provider, "ownedBy": provider})
    if "pricing" not in model:
        added += 1
    pricing = model.setdefault("pricing", {})
    if pricing.get("inputPer1MTokens") != input_per_1m or pricing.get("outputPer1MTokens") != output_per_1m:
        updated += 1
    pricing["currency"] = "USD"
    pricing["inputPer1MTokens"] = input_per_1m
    pricing["outputPer1MTokens"] = output_per_1m
    context = data.get("max_input_tokens") or data.get("max_tokens")
    if context and not model.get("contextWindowTokens"):
        model["contextWindowTokens"] = int(context)
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
def append_if_not_null(lines, name, literal):
    if literal != "null":
        lines.append(f"                {name} = {literal},")
lines = []
lines.append("// <auto-generated/>")
lines.append(f"// Generated by scripts/update-model-pricing.sh on {datetime.now():%Y-%m-%d %H:%M:%S}")
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
    lines.append(f"        [{cs_string(name)}] = new LLMModelMetadata")
    lines.append("        {")
    lines.append(f"            Id = {cs_string(model.get('id') or name)},")
    lines.append(f"            DisplayName = {cs_string(model.get('displayName') or name)},")
    append_if_not_null(lines, "ProviderType", cs_string(model.get("providerType")))
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
