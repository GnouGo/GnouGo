import copy
import hashlib
import importlib.util
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("comparison", ROOT / "scripts/evaluate_planning_benchmarks.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ComparisonTests(unittest.TestCase):
    def setUp(self):
        self.corpus = json.loads((ROOT / "evaluations/workflow-planning/corpus.v2.json").read_text())
        self.baseline = []
        for case in self.corpus["requests"]:
            for repeat in range(1, 4):
                self.baseline.append(dict(caseId=case["id"], repetition=repeat, plannerVersion=1,
                    promptHash=hashlib.sha256(case["prompt"].encode()).hexdigest(), model="fake",
                    catalogFingerprint="catalog", policyFingerprint="policy", budgetFingerprint="budget", answersFingerprint="answers",
                    outcome="generated", activeMilliseconds=100, inputTokens=100,
                    unsupportedOperationAccepted=False, unapprovedRelaxationAccepted=False,
                    checks={check: "passed" for check in case["checks"]}))
        self.candidate = copy.deepcopy(self.baseline)
        for record in self.candidate:
            record.update(plannerVersion=2, activeMilliseconds=75, inputTokens=70)

    def test_complete_paired_cohort_meeting_targets(self):
        self.assertTrue(MODULE.evaluate(self.corpus, self.baseline, self.candidate)["benchmarkAccepted"])

    def test_missing_repetition_is_not_success(self):
        with self.assertRaises(ValueError):
            MODULE.evaluate(self.corpus, self.baseline, self.candidate[:-1])

    def test_changed_catalog_is_not_a_comparable_speedup(self):
        self.candidate[0]["catalogFingerprint"] = "changed"
        with self.assertRaises(ValueError):
            MODULE.evaluate(self.corpus, self.baseline, self.candidate)

    def test_safety_failure_rejects_even_with_fast_generations(self):
        self.candidate[0]["unsupportedOperationAccepted"] = True
        self.assertFalse(MODULE.evaluate(self.corpus, self.baseline, self.candidate)["benchmarkAccepted"])

    def test_inconclusive_checks_and_early_failures_do_not_improve_speed(self):
        for record in self.candidate[:4]:
            record["checks"][next(iter(record["checks"]))] = "inconclusive"
            record["activeMilliseconds"] = 0
        result = MODULE.evaluate(self.corpus, self.baseline, self.candidate)
        self.assertFalse(result["gates"]["quality"])
        self.assertFalse(result["gates"]["active_time"])


if __name__ == "__main__":
    unittest.main()
