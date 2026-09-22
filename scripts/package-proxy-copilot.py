#!/usr/bin/env python3
"""Package a native publish and exercise the actual extracted release artifact."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
COMPONENT = ROOT / "src/GnOuGo.ProxyCopilot.Server"
RIDS = ("linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64")


def check_settings(directory):
    subprocess.run([sys.executable, str(ROOT / "scripts/check-proxy-copilot-config.py"),
                    "--publish", str(directory)], check=True, cwd=ROOT)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", type=Path, required=True)
    parser.add_argument("--rid", choices=RIDS, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/release")
    args = parser.parse_args()
    publish = args.publish.resolve()
    executable = "GnOuGo.ProxyCopilot.Server" + (".exe" if args.rid.startswith("win-") else "")
    for required in (executable, "wwwroot/ui/index.html", "appsettings.json"):
        if not (publish / required).is_file():
            raise SystemExit(f"Missing release file: {required}")
    if not any((publish / "wwwroot/ui/assets").glob("*.js")):
        raise SystemExit("Missing compiled dashboard assets")
    check_settings(publish)
    args.output.mkdir(parents=True, exist_ok=True)
    name = f"GnOuGo.ProxyCopilot.Server-{args.rid}-aot"
    extension = ".zip" if args.rid.startswith("win-") else ".tar.gz"
    destination = args.output.resolve() / (name + extension)
    with tempfile.TemporaryDirectory(prefix="gnougo-proxy-package-") as temporary:
        temporary = Path(temporary)
        staged = temporary / name
        shutil.copytree(publish, staged, ignore=shutil.ignore_patterns("*.pdb", "*.dbg", "*.dSYM"))
        shutil.copy2(COMPONENT / "README.md", staged / "README.md")
        shutil.copytree(COMPONENT / "examples", staged / "examples", dirs_exist_ok=True)
        check_settings(staged)
        archive = temporary / (name + extension)
        if extension == ".zip":
            with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as output:
                for path in sorted(staged.rglob("*")):
                    if path.is_file():
                        output.write(path, path.relative_to(temporary))
        else:
            with tarfile.open(archive, "w:gz") as output:
                output.add(staged, arcname=name)
        extracted = temporary / "extracted"
        if extension == ".zip":
            with zipfile.ZipFile(archive) as packaged:
                packaged.extractall(extracted)
        else:
            with tarfile.open(archive) as packaged:
                packaged.extractall(extracted, filter="data")
        unpacked = extracted / name
        check_settings(unpacked)
        env = dict(os.environ, DOTNET_ENVIRONMENT="Production", ASPNETCORE_ENVIRONMENT="Production")
        subprocess.run([sys.executable, str(ROOT / "scripts/smoke-proxy-copilot.py"),
                        "--binary", str(unpacked / executable)], check=True, cwd=ROOT, env=env)
        # Only make an archive eligible for upload after its extracted binary passes.
        shutil.copy2(archive, destination)
    print(f"PASS: release archive validated: {destination.name}")


if __name__ == "__main__":
    main()
