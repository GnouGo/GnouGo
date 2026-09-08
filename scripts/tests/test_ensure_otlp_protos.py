import importlib.util
from pathlib import Path
from subprocess import CompletedProcess
import unittest

spec = importlib.util.spec_from_file_location("ensure_otlp_protos", Path(__file__).resolve().parents[1] / "ensure-otlp-protos.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ProtocolAcquisitionTests(unittest.TestCase):
    def execute(self, responses):
        calls, waits, output = [], [], []

        def run(args, **options):
            calls.append(args)
            self.assertEqual("en-US", options["env"]["DOTNET_CLI_UI_LANGUAGE"])
            return CompletedProcess(args, *responses.pop(0))

        code = module.ensure(run, waits.append, output.append)
        return code, calls, waits, output

    def test_download_failure_retries_only_acquisition(self):
        code, calls, waits, output = self.execute([(0, "restored"), (1, "file(84): error MSB3923: connection reset"), (0, "ready")])
        self.assertEqual(0, code)
        self.assertEqual(["restore", "msbuild", "msbuild"], [call[1] for call in calls])
        self.assertEqual([2], waits)
        self.assertIn("ready", output)

    def test_non_download_errors_and_unrecognized_failures_do_not_retry(self):
        for message in ["error MSB3923: network\nerror CS1001: syntax", "error MSB1009: missing project", "process failed"]:
            with self.subTest(message=message):
                code, calls, waits, output = self.execute([(0, "restored"), (1, message)])
                self.assertEqual(1, code)
                self.assertEqual(2, len(calls))
                self.assertEqual([], waits)
                self.assertIn(message, output)

    def test_exhausted_download_failure_is_reported_completely(self):
        failure = "file(84): error MSB3923: connection reset\nnetwork details"
        code, calls, waits, output = self.execute([(0, "restored"), *[(1, failure)] * 3])
        self.assertEqual(1, code)
        self.assertEqual(4, len(calls))
        self.assertEqual([2, 2], waits)
        self.assertEqual(failure, output[-1])

    def test_restore_failure_is_not_retried(self):
        code, calls, waits, output = self.execute([(1, "restore failed")])
        self.assertEqual(1, code)
        self.assertEqual(1, len(calls))
        self.assertEqual([], waits)
        self.assertEqual(["restore failed"], output)


if __name__ == "__main__":
    unittest.main()
