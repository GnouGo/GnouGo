"""Disposable Copilot SDK prerequisite probe. The harness supplies a fresh working directory."""
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import urlopen


def main():
    directory = Path.cwd()
    seed = (directory / "seed.txt").read_text()
    cluster = "gnougo-" + hashlib.sha256(seed.encode()).hexdigest()[:18]
    kubeconfig = directory / "kubeconfig"
    if kubeconfig.exists() or kubeconfig.is_symlink():
        raise RuntimeError("Expected a fresh kubeconfig path")
    env = dict(os.environ, KUBECONFIG=str(kubeconfig), KIND_EXPERIMENTAL_PROVIDER="podman")
    observations = {}

    def observe(name, args, timeout=15):
        try:
            run = subprocess.run(args, env=env, capture_output=True, text=True, timeout=timeout)
            observations[name] = {"command": args, "exit_code": run.returncode,
                                  "stdout": run.stdout.strip()[-1500:], "stderr": run.stderr.strip()[-1500:]}
            if name in ("clusters_before", "clusters_after"):
                observations[name]["contains_run_cluster"] = cluster in run.stdout.splitlines()
        except (OSError, subprocess.TimeoutExpired) as error:
            observations[name] = {"command": args, "exit_code": None, "error": type(error).__name__}
        return observations[name]

    with (directory / "receipt.log").open("x") as log:
        log.write("command\n")
    for name, args in {
        "python": [sys.executable, "--version"], "kind": ["kind", "version"],
        "kubectl": ["kubectl", "version", "--client"], "engine": ["podman", "version", "--format", "{{.Server.Version}}"],
        "sdk": ["dotnet", "--list-sdks"], "node": ["node", "--version"], "pnpm": ["pnpm", "--version"], "uv": ["uv", "--version"]
    }.items():
        observe(name, args)
    for name, url in {"nuget": "https://api.nuget.org/v3/index.json", "pypi": "https://pypi.org/simple/",
                      "npm": "https://registry.npmjs.org/", "images": "https://registry-1.docker.io/v2/"}.items():
        try:
            with urlopen(url, timeout=6) as response:
                observations[name] = {"url": url, "status": response.status}
        except HTTPError as error:
            observations[name] = {"url": url, "status": error.code}
        except (URLError, OSError) as error:
            observations[name] = {"url": url, "status": None, "error": type(error).__name__}
    before = observe("clusters_before", ["kind", "get", "clusters"])
    commands_ready = all(observations[k]["exit_code"] == 0 for k in ("python", "kind", "kubectl", "engine", "clusters_before"))
    if commands_ready and not before["contains_run_cluster"]:
        try:
            creation = observe("create", ["kind", "create", "cluster", "--name", cluster, "--wait", "120s"], 175)
            if creation["exit_code"] == 0 and kubeconfig.is_file():
                observe("nodes", ["kubectl", "--context", "kind-" + cluster, "get", "nodes", "-o", "name"], 25)
        finally:
            observe("delete", ["kind", "delete", "cluster", "--name", cluster], 55)
            after = observe("clusters_after", ["kind", "get", "clusters"])
            containers = observe("containers_after", ["podman", "ps", "--all", "--filter",
                                  "label=io.x-k8s.kind.cluster=" + cluster, "--format", "{{.ID}}"])
            observations["cluster_absent"] = (after["exit_code"] == 0 and not after["contains_run_cluster"]
                                              and containers["exit_code"] == 0 and not containers["stdout"])
    kubeconfig.unlink(missing_ok=True)
    observations["kubeconfig_absent"] = not kubeconfig.exists()
    required = ("python", "kind", "kubectl", "engine", "sdk", "node", "pnpm", "uv", "create", "nodes", "delete")
    passed = all(observations.get(k, {}).get("exit_code") == 0 for k in required)
    passed &= all(observations[k]["status"] == 200 for k in ("nuget", "pypi", "npm"))
    passed &= observations["images"]["status"] in (200, 401) and observations.get("cluster_absent") is True
    report = {"observed": seed, "cluster": cluster, "checks": observations, "exit_code": 0 if passed else 1}
    (directory / "report.json").write_text(json.dumps(report))
    print(seed, "verified" if passed else "incomplete")
    return report["exit_code"]


if __name__ == "__main__":
    sys.exit(main())
