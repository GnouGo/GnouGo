"""Compare the retained deterministic cohorts, checking their frozen inputs first."""
import hashlib
import json
import math
import statistics
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
manifest = json.loads((HERE / "manifest.json").read_text())
for name, expected in manifest["sha256"].items():
    actual = hashlib.sha256((ROOT / name).read_bytes()).hexdigest()
    if actual != expected:
        raise SystemExit(f"Frozen harness/corpus mismatch: {name}")

cohorts = []
identities = {(name, repetition) for name in manifest["scenarios"] for repetition in range(manifest["repetitions"])}
for name in ("before", "after"):
    payload = (HERE / f"{name}.json").read_bytes()
    expected = manifest.get("resultSha256", {}).get(name)
    if expected is not None and hashlib.sha256(payload).hexdigest() != expected:
        raise SystemExit(f"Frozen measurement mismatch: {name}")
    rows = json.loads(payload)
    if len(rows) != len(identities) or {(r["name"], r["repetition"]) for r in rows} != identities:
        raise SystemExit(f"Incomplete/mismatched cohort: {name}")
    for row in rows:
        for key in ("calls", "repairs", "discoveryReads", "schemaBytes", "inputTokenEstimate", "planningMs", "executionMs"):
            if not isinstance(row[key], (float, int)) or not math.isfinite(row[key]) or row[key] < 0:
                raise SystemExit(f"Invalid measurement: {name}/{key}")
    cohorts.append(rows)
    print(f"{name}: execution/oracle success {sum(r['success'] and r['oracleCorrect'] for r in rows)}/{len(rows)}")

for key in ("schemaBytes", "inputTokenEstimate", "calls", "repairs", "discoveryReads", "planningMs", "executionMs"):
    def summary(rows):
        values = sorted(r[key] for r in rows)
        return f"{statistics.median(values):.3f} / {values[math.ceil(.95 * len(values)) - 1]:.3f}"
    print(f"{key}, median/p95: {summary(cohorts[0])} -> {summary(cohorts[1])}")
print("Provider usage: unknown. Deterministic adapter; paid calls/cost: 0.")
