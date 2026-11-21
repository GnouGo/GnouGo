"""Run every discovered Server case once against one frozen build.

Only the independently isolated workbook fixture class runs in parallel. Other
Server tests retain their single-process execution. TRX names must match the
complete unfiltered discovery inventory, including theory arguments.
"""

from collections import Counter
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET


PROJECT = "tests/GnOuGo.Agent.Server.Tests/GnOuGo.Agent.Server.Tests.csproj"
NAMESPACE = "GnOuGo.Agent.Server.Tests."
FIXTURE = NAMESPACE + "LocalProductOutcomeExecutionTests."
KNOWN_SKIP = NAMESPACE + (
    "PlanningClarificationBrowserTests."
    "DesignerBrowserUsesRealPersistedQuestionsAndExplicitSubmission"
)


def inventory_from_output(output):
    names = [line.strip() for line in output.splitlines()
             if line.strip().startswith(NAMESPACE)]
    if not names or len(set(names)) != len(names):
        raise ValueError("Server discovery must contain unique, nonempty case names")
    return Counter(names)


def partition_inventory(inventory):
    methods = Counter()
    remainder = Counter()
    for name, count in inventory.items():
        if name.startswith(FIXTURE):
            methods[name.split("(", 1)[0]] += count
        else:
            remainder[name] += count
    if not methods or not remainder:
        raise ValueError("Both the workbook fixtures and Server remainder must be discovered")
    groups = [[] for _ in range(min(4, len(methods)))]
    loads = [0] * len(groups)
    for method in sorted(methods, key=lambda name: (-methods[name], name)):
        index = min(range(len(groups)), key=lambda i: (loads[i], i))
        groups[index].append(method)
        loads[index] += methods[method]
    partitions = [{"name": "remainder", "filter": "FullyQualifiedName!~" + FIXTURE,
                   "expected": remainder}]
    for index, group in enumerate(groups, 1):
        expected = Counter({name: count for name, count in inventory.items()
                            if name.split("(", 1)[0] in group})
        partitions.append({"name": f"part-{index}", "expected": expected,
                           "filter": "|".join("FullyQualifiedName=" + method
                                              for method in sorted(group))})
    return partitions


def read_results(path):
    root = ET.parse(path).getroot()
    results = [(node.attrib["testName"], node.attrib.get("outcome", ""))
               for node in root.iter() if node.tag.rsplit("}", 1)[-1] == "UnitTestResult"]
    if not results:
        raise ValueError(f"No case results in {path}")
    return results


def validate_results(expected, results):
    actual = Counter(name for name, _ in results)
    missing, extra = expected - actual, actual - expected
    invalid = [(name, outcome) for name, outcome in results
               if outcome != "Passed" and not (name == KNOWN_SKIP and outcome == "NotExecuted")]
    return {"exact_coverage": not missing and not extra,
            "missing": dict(missing), "extra": dict(extra),
            "invalid_outcomes": invalid,
            "passed": sum(outcome == "Passed" for _, outcome in results),
            "skipped": [name for name, outcome in results if outcome == "NotExecuted"]}


def fingerprint_binaries(root):
    files = sorted(path for path in root.rglob("*")
                   if path.is_file() and (path.suffix == ".dll"
                                         or path.name.endswith((".deps.json", ".runtimeconfig.json"))))
    if not any(path.name == "GnOuGo.Agent.Server.Tests.dll" for path in files):
        raise ValueError("Built Server test assembly is missing")
    hashes = {}
    for path in files:
        with path.open("rb") as stream:
            hashes[str(path.relative_to(root))] = hashlib.file_digest(stream, "sha256").hexdigest()
    return hashes


def run_partition(partition, directory):
    directory.mkdir()
    command = ["dotnet", "test", PROJECT, "--no-build", "--no-restore",
               "--filter", partition["filter"], "--logger", "console;verbosity=normal",
               "--logger", "trx;LogFileName=server.trx", "--results-directory", str(directory),
               "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none"]
    with (directory / "console.log").open("w") as log:
        with subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, bufsize=1) as process:
            for line in process.stdout:
                log.write(line)
                log.flush()
                print(f"[{partition['name']}] {line}", end="", flush=True)
            exit_code = process.wait()
    return {"name": partition["name"], "exit_code": exit_code,
            **validate_results(partition["expected"], read_results(directory / "server.trx"))}


def main():
    root = Path(__file__).resolve().parents[2]
    if Path.cwd().resolve() != root:
        raise ValueError("Run from the repository root")
    subprocess.run(["dotnet", "build", PROJECT, "-m:1", "-warnaserror",
                    "-p:SkipClientBuild=true"], check=True)
    output = root / "artifacts/server-tests"
    output.mkdir(parents=True, exist_ok=True)
    directory = output / ("run-" + uuid.uuid4().hex)
    directory.mkdir()
    proof = {"source": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
             "success": False, "results_directory": str(directory.relative_to(root))}
    try:
        binary_root = root / "tests/GnOuGo.Agent.Server.Tests/bin/Debug/net10.0"
        before = fingerprint_binaries(binary_root)
        discovery = subprocess.check_output(["dotnet", "test", PROJECT, "--no-build", "--no-restore",
                                             "--list-tests"], text=True)
        (directory / "inventory.log").write_text(discovery)
        inventory = inventory_from_output(discovery)
        partitions = partition_inventory(inventory)
        proof.update(inventory=dict(inventory),
                     partitions=[{"name": p["name"], "filter": p["filter"],
                                  "expected": dict(p["expected"])} for p in partitions],
                     binaries_sha256=before)
        results = [run_partition(partitions[0], directory / partitions[0]["name"])]
        with ThreadPoolExecutor(max_workers=4) as executor:
            futures = [executor.submit(run_partition, p, directory / p["name"]) for p in partitions[1:]]
            results.extend(future.result() for future in futures)
        all_results = [item for p in partitions
                       for item in read_results(directory / p["name"] / "server.trx")]
        proof.update(results=results, aggregate=validate_results(inventory, all_results),
                     binaries_unchanged=before == fingerprint_binaries(binary_root))
        proof["success"] = (proof["binaries_unchanged"]
                            and proof["aggregate"]["exact_coverage"]
                            and not proof["aggregate"]["invalid_outcomes"]
                            and all(r["exit_code"] == 0 and r["exact_coverage"]
                                    and not r["invalid_outcomes"] for r in results))
    finally:
        (output / "server-test-coverage.json").write_text(json.dumps(proof, indent=2) + "\n")
    print(json.dumps({key: proof[key] for key in ("source", "success", "aggregate", "binaries_unchanged")}), flush=True)
    return 0 if proof["success"] else 1


if __name__ == "__main__":
    sys.exit(main())
