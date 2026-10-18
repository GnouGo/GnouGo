from pathlib import Path
import datetime
import json
import subprocess
import sys

root = Path(__file__).resolve().parent
freeze = json.loads((root / "freeze.json").read_text())
cases = ["local", "read_transform", "nullable_defaults", "conditional", "collections", "protected_cleanup", "review_french", "review_distractors", "files_read", "files_copy", "files_loop_cleanup"]
cohorts = [("baseline", freeze["base_commit"], "baseline"), ("candidate", freeze["candidate_commit"], "final")]
status = {"started_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "completed_commands": [], "state": "running"}

def save_status():
    (root / "collection-status.json").write_text(json.dumps(status, indent=2) + "\n")

for case in cases:
    for checkout, source, cohort in cohorts:
        directory = root / checkout
        name = checkout + "-" + case
        log = root / (name + ".jsonl")
        if log.exists():
            raise RuntimeError("Refusing to overwrite retained log: " + str(log))
        status["active"] = name
        save_status()
        print("START " + name, flush=True)
        command = ["dotnet", "tests/GnOuGo.Agent.Planning.Benchmark/bin/Release/net10.0/GnOuGo.Agent.Planning.Benchmark.dll", "--stabilization", "--campaign", freeze["campaign"], "--model", freeze["model"], "--cohort", cohort, "--source", source, "--cases", case, "--cmd-executable", str(root / "cmd" / "GnOuGo.Cmd.Mcp")]
        with log.open("w") as output:
            process = subprocess.Popen(command, cwd=directory, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            for line in process.stdout:
                output.write(line)
                output.flush()
                try:
                    row = json.loads(line)
                except json.JSONDecodeError:
                    print(line.rstrip(), flush=True)
                    continue
                if "case" in row:
                    print(json.dumps({key: row.get(key) for key in ["case", "cohort", "repetition", "status", "first_proposal_structurally_valid", "logical_planning_calls", "repairs", "diagnostics", "failure", "termination_reason"]}), flush=True)
                elif "cost_upper_bound_eur" in row:
                    print("ACCOUNTING " + json.dumps(row), flush=True)
            code = process.wait()
        status["completed_commands"].append({"name": name, "exit_code": code, "log": log.name})
        if code:
            status["state"] = "stopped"
            status["stop_reason"] = "harness_exit_" + str(code)
            save_status()
            sys.exit(code)
        save_status()
status["state"] = "planning_collection_complete"
status["active"] = None
status["finished_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
save_status()
print("PLANNING COLLECTION COMPLETE; approvals and execution remain separate.", flush=True)
