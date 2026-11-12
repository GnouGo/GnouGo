"""Read tool versions inside the SDK terminal; never install or use a network."""
import json
import os
from pathlib import Path
import shutil
import subprocess

observed = Path("seed.txt").read_text()
with Path("receipt.log").open("a") as log:
    log.write("command\n")

environment = dict(os.environ)
environment.update(COREPACK_ENABLE_NETWORK="0", PNPM_DISABLE_SELF_UPDATE_CHECK="1", UV_OFFLINE="1", npm_config_manage_package_manager_versions="false")
toolchains = []
for name in ("node", "pnpm", "python", "python3"):
    executable = shutil.which(name)
    row = {"name": name, "command": [name, "--version"], "available": executable is not None,
           "exit_code": None, "stdout": "", "stderr": "", "timed_out": False}
    if executable is not None:
        try:
            result = subprocess.run([executable, "--version"], capture_output=True, text=True,
                                    timeout=5, env=environment, check=False)
            row.update(exit_code=result.returncode, stdout=result.stdout, stderr=result.stderr)
        except subprocess.TimeoutExpired:
            row["timed_out"] = True
        except OSError as error:
            row["launch_error"] = type(error).__name__
    toolchains.append(row)

report = {"observed": observed, "exit_code": 0, "toolchains": toolchains,
          "installs_requested": False, "network_requested": False}
Path("report.json").write_text(json.dumps(report))
print("GNOU_GO_CODE_ENVIRONMENT_REPORT=" + json.dumps(report))
