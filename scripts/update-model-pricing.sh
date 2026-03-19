#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────
# update-model-pricing.sh
# Downloads latest model pricing data and regenerates the C# pricing catalog.
#
# Usage:
#   ./update-model-pricing.sh                          # regenerate C# from existing JSON
#   ./update-model-pricing.sh --download-latest        # download from LiteLLM + merge + regenerate
#   ./update-model-pricing.sh --json-path X --output-path Y
# ─────────────────────────────────────────────────────────────────────
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
JSON_PATH="${SCRIPT_DIR}/../src/GnOuGo.AI.Core/Telemetry/model-pricing.json"
OUTPUT_PATH="${SCRIPT_DIR}/../src/GnOuGo.AI.Core/Telemetry/ModelPricingCatalog.Generated.cs"
DOWNLOAD_LATEST=false

# Parse arguments
while [[ $# -gt 0 ]]; do
  case "$1" in
    --download-latest) DOWNLOAD_LATEST=true; shift ;;
    --json-path)       JSON_PATH="$2"; shift 2 ;;
    --output-path)     OUTPUT_PATH="$2"; shift 2 ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

echo "JSON path : $JSON_PATH"
echo "Output path: $OUTPUT_PATH"

# ── 1) Optionally download & merge from LiteLLM ─────────────────────
if [ "$DOWNLOAD_LATEST" = true ]; then
  echo ""
  echo "Downloading latest pricing from LiteLLM..."
  LITELLM_URL="https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json"
  TEMP_FILE=$(mktemp)

  if command -v curl &>/dev/null; then
    curl -sS --max-time 30 -o "$TEMP_FILE" "$LITELLM_URL" || {
      echo "WARNING: Could not download LiteLLM pricing. Continuing with existing local data..."
      rm -f "$TEMP_FILE"
    }
  elif command -v wget &>/dev/null; then
    wget -q --timeout=30 -O "$TEMP_FILE" "$LITELLM_URL" || {
      echo "WARNING: Could not download LiteLLM pricing. Continuing with existing local data..."
      rm -f "$TEMP_FILE"
    }
  else
    echo "WARNING: Neither curl nor wget found. Skipping download."
  fi

  # Merge logic requires python3 or jq — use python3 if available
  if [ -f "$TEMP_FILE" ] && [ -s "$TEMP_FILE" ]; then
    if command -v python3 &>/dev/null; then
      python3 - "$TEMP_FILE" "$JSON_PATH" <<'PYEOF'
import json, sys, math
from datetime import date

litellm_path = sys.argv[1]
local_path = sys.argv[2]

with open(litellm_path, "r", encoding="utf-8-sig") as f:
    litellm = json.load(f)
with open(local_path, "r", encoding="utf-8-sig") as f:
    local = json.load(f)

valid_providers = {"openai", "anthropic", "mistralai", "deepseek", "cohere", "text-completion-openai"}
added = updated = 0

for model_name, data in litellm.items():
    if not isinstance(data, dict):
        continue
    input_cost = data.get("input_cost_per_token")
    output_cost = data.get("output_cost_per_token")
    if not input_cost and not output_cost:
        continue

    provider = data.get("litellm_provider", "")
    if not any(vp in provider for vp in valid_providers):
        continue

    input_per_1m = round((input_cost or 0) * 1_000_000, 4)
    output_per_1m = round((output_cost or 0) * 1_000_000, 4)

    # Remove provider prefix (e.g. "openai/gpt-4o" -> "gpt-4o")
    clean_name = model_name.split("/", 1)[-1] if "/" in model_name else model_name

    if clean_name not in local["models"]:
        local["models"][clean_name] = {
            "inputPer1MTokens": input_per_1m,
            "outputPer1MTokens": output_per_1m
        }
        added += 1
    else:
        existing = local["models"][clean_name]
        if existing.get("inputPer1MTokens") != input_per_1m or existing.get("outputPer1MTokens") != output_per_1m:
            existing["inputPer1MTokens"] = input_per_1m
            existing["outputPer1MTokens"] = output_per_1m
            updated += 1

local["_updated"] = str(date.today())

with open(local_path, "w") as f:
    json.dump(local, f, indent=2, ensure_ascii=False)
    f.write("\n")

print(f"Merged: {added} added, {updated} updated")
PYEOF
    else
      echo "WARNING: python3 not found, skipping merge. JSON not updated."
    fi
    rm -f "$TEMP_FILE"
  fi
fi

# ── 2) Generate C# from JSON ────────────────────────────────────────
# Use python3 for JSON parsing + C# codegen (always available on modern Linux)
if ! command -v python3 &>/dev/null; then
  if [ -f "$OUTPUT_PATH" ]; then
    echo "WARNING: python3 not found but generated file already exists at $OUTPUT_PATH — skipping regeneration."
    exit 0
  fi
  echo "WARNING: python3 is required to generate the C# catalog."
  echo "         On Ubuntu/Debian: sudo apt install python3"
  echo "         On macOS: brew install python3"
  exit 1
fi

python3 - "$JSON_PATH" "$OUTPUT_PATH" <<'PYEOF'
import json, sys
from datetime import datetime

json_path = sys.argv[1]
output_path = sys.argv[2]

with open(json_path, "r", encoding="utf-8-sig") as f:
    data = json.load(f)

models = data.get("models", {})
aliases = data.get("aliases", {})

now = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

lines = []
lines.append("// <auto-generated/>")
lines.append(f"// Generated by scripts/update-model-pricing.sh on {now}")
lines.append("// Source: src/GnOuGo.AI.Core/Telemetry/model-pricing.json")
lines.append("// DO NOT EDIT MANUALLY - run the script to regenerate.")
lines.append("")
lines.append("using System.Collections.Frozen;")
lines.append("")
lines.append("namespace GnOuGo.AI.Core.Telemetry;")
lines.append("")
lines.append("public static partial class ModelPricingCatalog")
lines.append("{")
lines.append("    private static readonly FrozenDictionary<string, ModelPricing> Models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)")
lines.append("    {")

model_count = 0
for name, pricing in models.items():
    if name.startswith("_"):
        continue
    inp = pricing.get("inputPer1MTokens", 0)
    outp = pricing.get("outputPer1MTokens", 0)
    # Format as C# decimal literals with invariant culture
    inp_str = f"{inp}m"
    outp_str = f"{outp}m"
    lines.append(f'        ["{name}"] = new({inp_str}, {outp_str}),')
    model_count += 1

lines.append("    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);")
lines.append("")
lines.append("    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)")
lines.append("    {")

alias_count = 0
for alias_name, canonical in aliases.items():
    lines.append(f'        ["{alias_name}"] = "{canonical}",')
    alias_count += 1

lines.append("    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);")
lines.append("}")

with open(output_path, "w", encoding="utf-8") as f:
    f.write("\n".join(lines))
    f.write("\n")

print(f"\nGenerated {output_path} ({model_count} models, {alias_count} aliases)")
PYEOF

