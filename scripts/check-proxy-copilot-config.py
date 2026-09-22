#!/usr/bin/env python3
"""Reject tracked workstation settings and accidental publish inclusions; never print values."""
import argparse
import json
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--publish", type=Path, help="Also check a completed publish directory")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
component = Path("src/GnOuGo.ProxyCopilot.Server")
tracked = subprocess.check_output(["git", "ls-files", "-z", "--", str(component)], cwd=root).decode().split("\0")


def private_settings(path):
    name = Path(path).name
    return name.endswith(".json") and name.startswith(("appsettings.Development", "appsettings.Local"))


def safe_defaults(path):
    settings = json.loads(path.read_text())
    expected = {
        "Urls": "http://127.0.0.1:5087",
        "ProxyCopilot": {"TenantId": "local", "Providers": {},
                         "Capture": {"MaxCalls": 200, "MaxBodyBytes": 262144, "MaxTotalBytes": 67108864}},
        "OpenTelemetry": {"Enabled": False},
        "Logging": {"LogLevel": {"Default": "Information", "Microsoft.AspNetCore": "Warning",
                                  "System.Net.Http.HttpClient": "Warning"}},
    }
    if settings != expected:
        raise SystemExit("FAIL: base appsettings.json must contain only the reviewed public defaults.")


if any(private_settings(path) for path in tracked):
    raise SystemExit("FAIL: a development/local settings file is tracked; remove it from the index.")
safe_defaults(root / component / "appsettings.json")
ignored = subprocess.run(["git", "check-ignore", "--quiet", "--", str(component / "appsettings.Development.json")], cwd=root)
if ignored.returncode:
    raise SystemExit("FAIL: development settings must be ignored.")
if args.publish:
    if any(private_settings(path) for path in args.publish.rglob("*.json")):
        raise SystemExit("FAIL: workstation settings were included in publish output.")
    safe_defaults(args.publish / "appsettings.json")
print("PASS: public defaults only; workstation settings ignored/untracked" + (" and excluded from publish." if args.publish else "."))
