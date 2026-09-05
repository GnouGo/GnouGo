#!/usr/bin/env python3
"""Compare paired planner measurements. Never infer success from simulation alone."""
import argparse
import hashlib
import json
import math
import statistics
from pathlib import Path


def load_records(path):
    return [json.loads(line) for line in Path(path).read_text().splitlines() if line.strip()]


def evaluate(corpus, baseline, candidate):
    if corpus.get("schemaVersion") != 2 or corpus.get("repetitions") != 3:
        raise ValueError("Expected corpus version 2 with three repetitions")
    cases = {case["id"]: case for case in corpus["requests"]}
    if len(cases) < 20 or len(cases) != len(corpus["requests"]):
        raise ValueError("At least twenty distinct supported cases are required")
    expected = {(case, repeat) for case in cases for repeat in range(1, 4)}

    def indexed(records, version):
        result = {}
        for record in records:
            key = (record["caseId"], record["repetition"])
            if key in result or key not in expected or record["plannerVersion"] != version:
                raise ValueError("Duplicate, unknown, or incorrectly versioned measurement")
            prompt_hash = hashlib.sha256(cases[key[0]]["prompt"].encode()).hexdigest()
            if record["promptHash"] != prompt_hash:
                raise ValueError("The measured request differs from the corpus")
            for name in ("activeMilliseconds", "inputTokens"):
                value = record[name]
                if not isinstance(value, (float, int)) or isinstance(value, bool) or not math.isfinite(value) or value < 0:
                    raise ValueError("Invalid active time or usage measurement")
            if not isinstance(record["unsupportedOperationAccepted"], bool) or not isinstance(record["unapprovedRelaxationAccepted"], bool):
                raise ValueError("Explicit safety results are required")
            if record["outcome"] not in ("generated", "saved", "cancelled", "unsupported", "failed"):
                raise ValueError("Unknown terminal outcome")
            checks = record["checks"]
            for name in cases[key[0]]["checks"]:
                if checks.get(name) not in ("passed", "failed", "inconclusive"):
                    raise ValueError("Every independent required check needs an explicit outcome")
            result[key] = record
        if set(result) != expected:
            raise ValueError("Incomplete evaluation: every supported request needs three runs")
        return result

    old, new = indexed(baseline, 1), indexed(candidate, 2)
    for key in expected:
        for field in ("model", "catalogFingerprint", "policyFingerprint", "budgetFingerprint", "answersFingerprint"):
            if not old[key].get(field) or old[key][field] != new[key].get(field):
                raise ValueError("Paired runs differ in " + field)

    def passed(record):
        return record["outcome"] in ("generated", "saved") and all(record["checks"][check] == "passed" for check in cases[record["caseId"]]["checks"])

    success = sum(passed(record) for record in new.values()) / len(expected)
    safety = all(not record["unsupportedOperationAccepted"] and not record["unapprovedRelaxationAccepted"] for record in new.values())
    pairs = [key for key in expected if passed(old[key]) and passed(new[key])]
    # Early failures cannot manufacture an apparent speed improvement.
    comparable = len(pairs) >= math.ceil(len(expected) * .95)
    old_time = statistics.median(old[key]["activeMilliseconds"] for key in pairs) if pairs else 0
    old_tokens = statistics.median(old[key]["inputTokens"] for key in pairs) if pairs else 0
    time_ratio = statistics.median(new[key]["activeMilliseconds"] for key in pairs) / old_time if old_time else None
    token_ratio = statistics.median(new[key]["inputTokens"] for key in pairs) / old_tokens if old_tokens else None
    gates = {"quality": success >= .95, "safety": safety,
             "active_time": comparable and time_ratio is not None and time_ratio <= .75,
             "input_tokens": comparable and token_ratio is not None and token_ratio <= .70}
    return {"schemaVersion": 2, "runsPerPlanner": len(expected), "pairedSuccessfulRuns": len(pairs),
            "candidateSuccessRate": success, "medianActiveTimeRatio": time_ratio,
            "medianInputTokenRatio": token_ratio, "gates": gates, "benchmarkAccepted": all(gates.values())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", default="evaluations/workflow-planning/corpus.v2.json")
    parser.add_argument("--v1", required=True)
    parser.add_argument("--v2", required=True)
    args = parser.parse_args()
    try:
        result = evaluate(json.loads(Path(args.corpus).read_text()), load_records(args.v1), load_records(args.v2))
    except (ValueError, KeyError, TypeError) as error:
        parser.error(str(error))
    print(json.dumps(result, indent=2))
    return 0 if result["benchmarkAccepted"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
