from collections import Counter
from pathlib import Path
import tempfile
import unittest

from test_server_partitions import (FIXTURE, KNOWN_SKIP, NAMESPACE, inventory_from_output,
                                    partition_inventory, read_results, validate_results)


class ServerPartitionTests(unittest.TestCase):
    def test_every_case_has_one_deterministic_partition_and_exact_method_filter(self):
        # Prefix-colliding methods and all theory rows must remain distinct.
        names = [FIXTURE + "A(x: 1)", FIXTURE + "A(x: 2)", FIXTURE + "AB(x: 1)",
                 FIXTURE + "C", FIXTURE + "D", FIXTURE + "E", NAMESPACE + "Other.Test"]
        inventory = Counter(names)
        partitions = partition_inventory(inventory)
        self.assertEqual(partitions, partition_inventory(Counter(reversed(names))))
        self.assertEqual(inventory, sum((p["expected"] for p in partitions), Counter()))
        self.assertTrue(all(count == 1 for p in partitions for count in p["expected"].values()))
        a = next(p for p in partitions if FIXTURE + "A(x: 1)" in p["expected"])
        self.assertIn("FullyQualifiedName=" + FIXTURE + "A", a["filter"].split("|"))
        self.assertNotIn("~", a["filter"])

    def test_same_total_with_a_duplicate_and_a_missing_case_fails(self):
        result = validate_results(Counter({"A": 1, "B": 1}), [("A", "Passed"), ("A", "Passed")])
        self.assertFalse(result["exact_coverage"])
        self.assertEqual({"B": 1}, result["missing"])
        self.assertEqual({"A": 1}, result["extra"])

    def test_only_the_existing_platform_skip_is_accepted(self):
        expected = Counter({KNOWN_SKIP: 1, "Other": 1})
        result = validate_results(expected, [(KNOWN_SKIP, "NotExecuted"), ("Other", "Passed")])
        self.assertEqual([], result["invalid_outcomes"])
        for outcome in ("NotExecuted", "Failed", "Timeout", "Aborted", ""):
            with self.subTest(outcome=outcome):
                self.assertTrue(validate_results(expected, [("Other", outcome)])["invalid_outcomes"])
        self.assertTrue(validate_results(Counter({KNOWN_SKIP: 1}), [(KNOWN_SKIP, "Failed")])["invalid_outcomes"])

    def test_discovery_fails_closed_on_empty_or_duplicate_cases(self):
        for output in ("No tests", NAMESPACE + "A\n" + NAMESPACE + "A"):
            with self.assertRaises(ValueError):
                inventory_from_output(output)
        self.assertEqual(Counter({NAMESPACE + "A(x: 1)": 1}),
                         inventory_from_output("Header\n    " + NAMESPACE + "A(x: 1)\n"))

    def test_trx_reader_preserves_full_case_names_and_rejects_empty_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.trx"
            path.write_text('<TestRun xmlns="urn:test"><Results>'
                            '<UnitTestResult testName="A(x: &quot;one&quot;)" outcome="Passed"/>'
                            '</Results></TestRun>')
            self.assertEqual([('A(x: "one")', "Passed")], read_results(path))
            path.write_text("<TestRun/>")
            with self.assertRaises(ValueError):
                read_results(path)


if __name__ == "__main__":
    unittest.main()
